import { useEffect, useMemo, useRef, useState } from "react";
import { NavLink, useLocation, useNavigate, useParams } from "react-router-dom";
import { parseQueryString } from "../../api/data";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { ConfirmDialog } from "../../components/ui/Dialog";
import { EmptyState } from "../../components/ui/EmptyState";
import { Panel } from "../../components/ui/Panel";
import { Spinner } from "../../components/ui/Spinner";
import {
  getExplorerQueryForView,
  useExplorerStore,
} from "../../stores/explorerStore";
import { getDraftFocusColumn } from "../../stores/explorer/helpers";
import { useRowCountStore } from "../../stores/rowCountStore";
import { useAuthStore } from "../../stores/authStore";
import { useSchemaStore } from "../../stores/schemaStore";
import {
  DEFAULT_QUERY,
  type FilterClause,
  type SortClause,
} from "../../types/explorer";
import type { ModelColumn, ModelTable } from "../../types/api";
import {
  DataPaginationBar,
  DataQueryControls,
} from "./components/DataQueryControls";
import { KanbanConfigPanel } from "./components/KanbanConfigPanel";
import { ViewRenderer } from "./components/ViewRenderer";
import { RecordEditor } from "./RecordEditor";
import {
  type ExportFormat,
  getOrderedVisibleColumns,
} from "./export/serializers";
import {
  EXPORT_ROW_LIMIT,
  useExportCurrentView,
} from "./export/useExportCurrentView";
import {
  getReferencedTableName,
  getRelatedDrillIns,
} from "./relationNavigation";
import "./DataExplorer.css";

const BULK_DELETE_CONCURRENCY = 4;

async function runInConcurrentBatches<T, R>(
  items: T[],
  concurrency: number,
  worker: (item: T) => Promise<R>,
): Promise<R[]> {
  const results: R[] = [];
  for (let index = 0; index < items.length; index += concurrency) {
    const batch = items.slice(index, index + concurrency);
    results.push(...(await Promise.all(batch.map((item) => worker(item)))));
  }
  return results;
}

export function TableDetailPage() {
  const { tableId } = useParams<{ tableId: string }>();
  const location = useLocation();
  const navigate = useNavigate();
  const schemaStatus = useSchemaStore((state) => state.status);
  const snapshot = useSchemaStore((state) => state.snapshot);
  const schemaError = useSchemaStore((state) => state.error);
  const loadSchema = useSchemaStore((state) => state.load);

  // Explorer store — view state for this table
  const setActiveTable = useExplorerStore((state) => state.setActiveTable);
  const fetchRows = useExplorerStore((state) => state.fetchRows);
  const setPage = useExplorerStore((state) => state.setPage);
  const setSort = useExplorerStore((state) => state.setSort);
  const setSearch = useExplorerStore((state) => state.setSearch);
  const setFilters = useExplorerStore((state) => state.setFilters);
  const setQuery = useExplorerStore((state) => state.setQuery);
  const applyRouteQuery = useExplorerStore((state) => state.applyRouteQuery);
  const toggleRowSelection = useExplorerStore(
    (state) => state.toggleRowSelection,
  );
  const toggleSelectAllVisible = useExplorerStore(
    (state) => state.toggleSelectAllVisible,
  );
  const setSelectedRows = useExplorerStore((state) => state.setSelectedRows);
  const setHiddenColumns = useExplorerStore((state) => state.setHiddenColumns);
  const clearSelection = useExplorerStore((state) => state.clearSelection);
  const openRelatedContext = useExplorerStore(
    (state) => state.openRelatedContext,
  );
  const openFilteredRelatedContext = useExplorerStore(
    (state) => state.openFilteredRelatedContext,
  );
  const clearContext = useExplorerStore((state) => state.clearContext);
  const loadSavedViews = useExplorerStore((state) => state.loadSavedViews);
  const switchView = useExplorerStore((state) => state.switchView);
  const saveCurrentAsView = useExplorerStore(
    (state) => state.saveCurrentAsView,
  );
  const updateActiveViewConfig = useExplorerStore(
    (state) => state.updateActiveViewConfig,
  );
  const renameView = useExplorerStore((state) => state.renameView);
  const toggleViewShared = useExplorerStore((state) => state.toggleViewShared);
  const deleteView = useExplorerStore((state) => state.deleteView);
  const changeViewType = useExplorerStore((state) => state.changeViewType);
  const setDefaultView = useExplorerStore((state) => state.setDefaultView);
  const setAutoSave = useExplorerStore((state) => state.setAutoSave);
  const setKanbanConfig = useExplorerStore((state) => state.setKanbanConfig);
  const duplicateView = useExplorerStore((state) => state.duplicateView);
  const startCellEdit = useExplorerStore((state) => state.startCellEdit);
  const cancelCellEdit = useExplorerStore((state) => state.cancelCellEdit);
  const commitCellEdit = useExplorerStore((state) => state.commitCellEdit);
  const addBlankRow = useExplorerStore((state) => state.addBlankRow);
  const deleteRow = useExplorerStore((state) => state.deleteRow);
  const duplicateRow = useExplorerStore((state) => state.duplicateRow);
  const pushUndoDeleteEntry = useExplorerStore(
    (state) => state.pushUndoDeleteEntry,
  );
  const undoLast = useExplorerStore((state) => state.undoLast);
  const openRecordEditor = useExplorerStore((state) => state.openRecordEditor);
  const currentUserId = useAuthStore((state) => state.user?.id ?? null);
  const defaultViewId = useExplorerStore((state) =>
    tableId ? (state.defaultViewIdByTable[tableId] ?? null) : null,
  );
  const view = useExplorerStore((state) =>
    tableId ? (state.views[tableId] ?? null) : null,
  );
  const topUndoEntry = useExplorerStore((state) =>
    tableId ? ((state.undoStackByTable[tableId] ?? []).at(-1) ?? null) : null,
  );

  // Ensure schema is loaded
  useEffect(() => {
    if (schemaStatus === "idle") {
      void loadSchema();
    }
  }, [schemaStatus, loadSchema]);

  // Set active table and trigger initial fetch when table is known
  useEffect(() => {
    if (!tableId) return;
    setActiveTable(tableId);
  }, [tableId, setActiveTable]);

  // Load saved views once per table (when savedViewsStatus is still idle).
  // Declared before fetchRows so the views fetch is registered first in tests
  // that track fetch call order via mock.calls.at(-1).
  useEffect(() => {
    if (!tableId) return;
    const v = useExplorerStore.getState().views[tableId];
    if (v && v.savedViewsStatus !== "idle") return;
    void loadSavedViews(tableId);
  }, [tableId, loadSavedViews, view?.savedViewsStatus]);

  // Once saved views have loaded, switch to the default view for this table
  // (if one was marked). Only runs once per table mount.
  const autoSwitchedForTable = useRef<string | null>(null);
  const appliedRouteQueryRef = useRef<string | null>(null);
  const routeQuery = useMemo(() => parseQueryString(location.search), [location.search]);
  const hasRouteQueryState = Object.keys(routeQuery).length > 0;
  useEffect(() => {
    if (!tableId) return;
    if (hasRouteQueryState) return;
    if (view?.savedViewsStatus !== "ready") return;
    const key = `${tableId}:plain`;
    if (autoSwitchedForTable.current === key) return;
    autoSwitchedForTable.current = key;
    if (
      defaultViewId !== null &&
      view.activeViewId === null &&
      view.savedViews.some((sv) => sv.id === defaultViewId)
    ) {
      switchView(defaultViewId);
    }
  }, [
    tableId,
    view?.savedViewsStatus,
    view?.activeViewId,
    view?.savedViews,
    defaultViewId,
    switchView,
    hasRouteQueryState,
  ]);

  // Auto-open the kanban config panel the first time we land on a kanban
  // view whose grouping column hasn't been chosen yet. Keyed by
  // table+activeViewId so switching views can re-trigger.
  useEffect(() => {
    if (!tableId) return;
    if (!view) return;
    if (view.config.type !== "kanban") {
      setKanbanConfigOpen(false);
      kanbanAutoOpenedFor.current = null;
      return;
    }
    const key = `${tableId}:${view.activeViewId ?? "none"}`;
    if (kanbanAutoOpenedFor.current === key) return;
    if (view.config.kanbanColumn === null) {
      kanbanAutoOpenedFor.current = key;
      setKanbanConfigOpen(true);
    }
  }, [tableId, view?.config, view?.activeViewId, view]);

  useEffect(() => {
    if (!tableId) return;
    if (!hasRouteQueryState) {
      appliedRouteQueryRef.current = null;
      return;
    }

    const key = `${tableId}?${location.search}`;
    if (appliedRouteQueryRef.current === key) return;
    appliedRouteQueryRef.current = key;

    applyRouteQuery(tableId, routeQuery);
  }, [tableId, location.search, hasRouteQueryState, routeQuery, applyRouteQuery]);

  // Fetch rows once the table is active and schema is ready, but only on
  // the first load (idle → loading).
  useEffect(() => {
    if (!tableId || schemaStatus !== "ready") return;
    const v = useExplorerStore.getState().views[tableId];
    if (v && v.status === "idle") {
      void fetchRows();
    }
  }, [tableId, schemaStatus, fetchRows, view?.status]);

  const schemaLoading = schemaStatus === "loading" || schemaStatus === "idle";
  const schemaReady = schemaStatus === "ready" && snapshot !== null;
  const [kanbanConfigOpen, setKanbanConfigOpen] = useState(false);
  const kanbanAutoOpenedFor = useRef<string | null>(null);
  const [filterShortcut, setFilterShortcut] = useState<{
    columnName: string;
    nonce: number;
  } | null>(null);
  const [sortShortcut, setSortShortcut] = useState<{
    columnName: string;
    nonce: number;
  } | null>(null);
  const [deleteDialogRowIds, setDeleteDialogRowIds] = useState<string[] | null>(
    null,
  );
  const [operationNotice, setOperationNotice] = useState<{
    tone: "success" | "warning" | "error";
    title: string;
    message: string;
  } | null>(null);
  const [undoToastVisible, setUndoToastVisible] = useState(false);
  const [exportMenuOpen, setExportMenuOpen] = useState(false);
  // The header hosts the view selector next to the table tabs.
  const [viewSlot, setViewSlot] = useState<HTMLDivElement | null>(null);
  // Row counts for the tabs come from what is already known (Home's
  // counts, unfiltered views); this page fetches none of its own.
  const tabCounts = useRowCountStore((state) => state.counts);
  const setRowCount = useRowCountStore((state) => state.setCount);
  const unfilteredTotal =
    view &&
    view.status === "ready" &&
    view.context === null &&
    view.source.kind === "table" &&
    view.config.filters.length === 0 &&
    !view.config.search &&
    view.meta
      ? view.meta.total - view.pendingNewRowIds.length
      : null;
  useEffect(() => {
    if (tableId && unfilteredTotal !== null) setRowCount(tableId, unfilteredTotal);
  }, [tableId, unfilteredTotal, setRowCount]);
  const exportMenuRef = useRef<HTMLDivElement | null>(null);

  const table = useMemo(() => {
    if (!schemaReady) return null;
    return snapshot.tables.find((t) => t.name === tableId) ?? null;
  }, [schemaReady, snapshot, tableId]);
  const relationDrillIns = useMemo(
    () => (snapshot && table ? getRelatedDrillIns(snapshot, table) : []),
    [snapshot, table],
  );
  const hiddenColumnNames = useMemo(
    () =>
      !table
        ? []
        : (view?.config.hiddenColumnNames ?? []).filter((columnName) =>
            table.columns.some(
              (column) => !column.primaryKey && column.name === columnName,
            ),
          ),
    [table, view?.config.hiddenColumnNames],
  );
  const visibleColumns = useMemo(
    () =>
      !table
        ? []
        : getOrderedVisibleColumns(
            table,
            hiddenColumnNames,
            view?.config.columnOrder ?? [],
          ),
    [hiddenColumnNames, table, view?.config.columnOrder],
  );
  const visibleTable = useMemo(
    () =>
      !table
        ? null
        : {
            ...table,
            columns: visibleColumns,
          },
    [table, visibleColumns],
  );
  const visibleRowIds = useMemo(
    () =>
      view?.rows
        .map((row) => row.id)
        .filter(
          (value): value is string | number =>
            value !== null &&
            value !== undefined &&
            value !== "" &&
            !(view?.pendingNewRowIds ?? []).includes(String(value)),
        )
        .map((value) => String(value)) ?? [],
    [view?.pendingNewRowIds, view?.rows],
  );
  const dataStatus = view?.status ?? "idle";
  const dataError = view?.error ?? null;
  const query = view ? getExplorerQueryForView(view) : DEFAULT_QUERY;
  const totalPages = view?.meta?.total_pages ?? 1;
  const selectedRowIds = view?.selectedRowIds ?? [];
  const recordEditorOpen = useExplorerStore(
    (state) => state.recordEditor?.open === true,
  );
  // When the full-screen record editor is open it owns inline-edit state for the
  // row; the grid should not mount its own inline editor (they would fight for
  // focus and cause the edit to collapse immediately).
  const editingCell = recordEditorOpen ? null : (view?.editingCell ?? null);
  const cellErrors = view?.cellErrors ?? {};
  const rowErrors = view?.rowErrors ?? {};
  const pendingNewRowIds = view?.pendingNewRowIds ?? [];
  const context = view?.context ?? null;
  const selectedRows = useMemo(
    () =>
      view?.rows.filter((row) =>
        selectedRowIds.includes(String(row.id ?? "")),
      ) ?? [],
    [selectedRowIds, view?.rows],
  );
  const targetTableName = table?.name ?? tableId ?? null;
  const exportTable: ModelTable = table ?? {
    name: tableId ?? "",
    columns: [],
  };
  const activeViewName =
    view?.savedViews.find((savedView) => savedView.id === view.activeViewId)
      ?.name ?? "AllRecords";
  const {
    isExporting,
    format: exportFormat,
    phase: exportPhase,
    fetchedRows: exportedRowsSoFar,
    totalRows: exportTotalRows,
    exportCurrentView,
    cancelExport,
  } = useExportCurrentView({
    table: exportTable,
    columns: visibleColumns,
    query,
    source: view?.source ?? { kind: "table" },
    viewName: activeViewName,
  });

  function isEditableEventTarget(target: EventTarget | null): boolean {
    return (
      target instanceof HTMLElement &&
      (target instanceof HTMLInputElement ||
        target instanceof HTMLTextAreaElement ||
        target instanceof HTMLSelectElement ||
        target.isContentEditable)
    );
  }

  async function handleUndoLast() {
    if (!topUndoEntry || !targetTableName) return;
    setOperationNotice(null);
    setUndoToastVisible(false);
    const result = await undoLast(targetTableName);
    if (result.total === 0) return;

    const restoredLabel = result.total === 1 ? "row" : "rows";
    if (result.failureCount === 0) {
      setOperationNotice({
        tone: "success",
        title: "Undo completed",
        message: `Restored ${result.successCount} ${restoredLabel}.`,
      });
      return;
    }

    setOperationNotice({
      tone: result.successCount > 0 ? "warning" : "error",
      title:
        result.successCount > 0 ? "Some rows couldn't be restored" : "Undo failed",
      message:
        result.successCount > 0
          ? `Restored ${result.successCount} of ${result.total} rows.`
          : `Could not restore ${result.total} ${restoredLabel}.`,
    });
  }

  useEffect(() => {
    if (!topUndoEntry) {
      setUndoToastVisible(false);
      return;
    }
    setUndoToastVisible(true);
    const timeout = window.setTimeout(() => {
      setUndoToastVisible(false);
    }, 10_000);
    return () => window.clearTimeout(timeout);
  }, [topUndoEntry?.at]);

  useEffect(() => {
    function handleKeyDown(event: KeyboardEvent) {
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === "z") {
        if (event.shiftKey || event.altKey) return;
        if (isEditableEventTarget(event.target)) return;
        if (!topUndoEntry) return;
        event.preventDefault();
        void handleUndoLast();
      }
    }

    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  }, [topUndoEntry?.at, targetTableName]);

  useEffect(() => {
    if (!exportMenuOpen) return;

    function handlePointerDown(event: PointerEvent) {
      if (!exportMenuRef.current?.contains(event.target as Node)) {
        setExportMenuOpen(false);
      }
    }

    window.addEventListener("pointerdown", handlePointerDown);
    return () => window.removeEventListener("pointerdown", handlePointerDown);
  }, [exportMenuOpen]);

  // Loading schema
  if (schemaLoading) {
    return (
      <div className="explorer-loading" data-testid="explorer-loading">
        <Spinner label="Loading schema" />
      </div>
    );
  }

  // Schema error
  if (schemaStatus === "error") {
    return (
      <Panel>
        <Alert
          tone="error"
          title="Schema unavailable"
          message={
            <>
              <span>
                {schemaError ??
                  "Your tables could not be loaded. Try again in a moment."}
              </span>{" "}
              <Button
                variant="ghost"
                size="sm"
                onClick={() => void loadSchema({ force: true })}
                leadingIcon={<Icon name="refresh" size={13} />}
              >
                Retry
              </Button>
            </>
          }
        />
      </Panel>
    );
  }

  // Table not found in schema
  if (!table) {
    return (
      <Panel>
        <EmptyState
          icon="database"
          title="Table not found"
          description={`No table named "${tableId ?? "unknown"}" exists in the current schema. It may have been renamed or removed.`}
          actions={
            <Button
              variant="default"
              leadingIcon={<Icon name="chevron-left" size={14} />}
              onClick={() => window.history.back()}
            >
              Back to tables
            </Button>
          }
        />
      </Panel>
    );
  }

  const resolvedTable = table;
  const tableTabs = snapshot?.tables ?? [];
  // Counts show only when every tab has one; a lone number reads as broken.
  const allTabCountsKnown = tableTabs.every((tab) => typeof tabCounts[tab.name] === "number");

  // Table found — render detail
  const displayName = resolvedTable.displayName ?? resolvedTable.name;
  const contextParentDisplayName =
    context?.kind === "related"
      ? (tableTabs.find((item) => item.name === context.parentTable)
          ?.displayName ?? context.parentTable)
      : null;

  function handleSortChange(sortClauses: SortClause[]) {
    if (context) {
      clearContext(resolvedTable.name, {
        search: query.search,
        sort: sortClauses,
        perPage: query.perPage,
        filters: query.filters,
        page: 1,
      });
      void fetchRows();
      return;
    }
    setSort(sortClauses);
  }

  function handlePerPageChange(perPage: number) {
    if (context) {
      clearContext(resolvedTable.name, {
        search: query.search,
        sort: query.sort,
        perPage,
        filters: query.filters,
        page: 1,
      });
      void fetchRows();
      return;
    }
    setQuery({ perPage, page: 1 });
  }

  function handleFiltersChange(filters: FilterClause[]) {
    if (context) {
      clearContext(resolvedTable.name, {
        search: query.search,
        sort: query.sort,
        perPage: query.perPage,
        filters,
        page: 1,
      });
      void fetchRows();
      return;
    }
    setFilters(filters);
  }

  function handleSearchChange(search: string) {
    if (context) {
      clearContext(resolvedTable.name, {
        search,
        sort: query.sort,
        perPage: query.perPage,
        filters: query.filters,
        page: 1,
      });
      void fetchRows();
      return;
    }
    setSearch(search);
  }

  function handlePageChange(page: number) {
    if (context) {
      clearContext(resolvedTable.name, {
        search: query.search,
        sort: query.sort,
        perPage: query.perPage,
        filters: query.filters,
        page,
      });
      void fetchRows();
      return;
    }
    setPage(page);
  }

  function handleResetView() {
    if (context) {
      clearContext(resolvedTable.name, { ...DEFAULT_QUERY });
      void fetchRows();
      return;
    }
    // Re-apply the active saved view's config (or default if on All Records).
    // switchView fully re-hydrates config (including hidden columns) and fetches.
    switchView(view?.activeViewId ?? null);
  }

  function handleRequestFilterColumn(columnName: string) {
    setFilterShortcut({ columnName, nonce: Date.now() });
  }

  function handleRequestSortColumn(columnName: string) {
    setSortShortcut({ columnName, nonce: Date.now() });
  }

  function handleNavigateReference(
    column: ModelColumn,
    value: unknown,
    _row: Record<string, unknown>,
  ) {
    if (!snapshot) return;

    const targetTable = getReferencedTableName(snapshot, resolvedTable, column);
    const targetRowId =
      value === null || value === undefined || value === ""
        ? null
        : String(value);
    if (!targetTable || !targetRowId) {
      return;
    }

    void openRecordEditor({
      table: targetTable,
      rowId: targetRowId,
    });
  }

  function handleOpenRelated(drillInAccessor: string, targetTable: string) {
    const drillIn = relationDrillIns.find(
      (item) => item.accessor === drillInAccessor,
    );
    if (!drillIn || selectedRowIds.length === 0) return;

    if (drillIn.mode === "filter" && drillIn.filterColumn) {
      const filterValues = drillIn.valueColumn
        ? selectedRows
            .map((row) => row[drillIn.valueColumn!])
            .filter(
              (value): value is string | number =>
                value !== null && value !== undefined && value !== "",
            )
            .map((value) => String(value))
        : selectedRowIds;
      const uniqueValues = Array.from(new Set(filterValues));
      if (uniqueValues.length === 0) return;

      openFilteredRelatedContext(
        targetTable,
        resolvedTable.name,
        selectedRowIds,
        drillInAccessor,
        drillIn.filterColumn,
        [
          uniqueValues.length === 1
            ? { column: drillIn.filterColumn, op: "eq", value: uniqueValues[0] }
            : {
                column: drillIn.filterColumn,
                op: "in",
                value: uniqueValues.join(","),
              },
        ],
      );
      navigate(`/data/${targetTable}`);
      return;
    }

    if (selectedRowIds.length !== 1) return;
    const [parentRowId] = selectedRowIds;
    if (!parentRowId) return;

    openRelatedContext(
      targetTable,
      resolvedTable.name,
      parentRowId,
      drillInAccessor,
    );
    navigate(`/data/${targetTable}`);
  }

  function handleToggleRowSelection(row: Record<string, unknown>) {
    const rowId = String(row.id ?? "");
    if (!rowId) return;

    toggleRowSelection(resolvedTable.name, rowId);
  }

  function handleToggleSelectAllRows() {
    toggleSelectAllVisible(resolvedTable.name, visibleRowIds);
  }

  function handleSetSelectedRows(rowIds: string[]) {
    setSelectedRows(resolvedTable.name, rowIds);
  }

  function handleClearContext() {
    clearContext(resolvedTable.name);
    void fetchRows();
  }

  function handleClearSelection() {
    clearSelection(resolvedTable.name);
  }

  function handleHiddenColumnsChange(columnNames: string[]) {
    setHiddenColumns(
      resolvedTable.name,
      columnNames.filter((columnName) =>
        resolvedTable.columns.some(
          (column) => !column.primaryKey && column.name === columnName,
        ),
      ),
    );
  }

  function handleRequestHideColumn(column: ModelColumn) {
    if (column.primaryKey) return;
    if (hiddenColumnNames.includes(column.name)) return;
    handleHiddenColumnsChange([...hiddenColumnNames, column.name]);
  }

  function handleAddRow() {
    if (context) return;
    addBlankRow(resolvedTable.name);
  }

  async function handleExport(format: ExportFormat) {
    setOperationNotice(null);
    setExportMenuOpen(false);

    try {
      const result = await exportCurrentView(format);
      if (result.status === "cancelled") {
        setOperationNotice({
          tone: "warning",
          title: "Export canceled",
          message:
            result.exportedRows > 0
              ? `Export canceled after ${result.exportedRows} ${getRowLabel(result.exportedRows)}.`
              : `Canceled ${format.toUpperCase()} export before a file was created.`,
        });
        return;
      }

      setOperationNotice({
        tone: result.truncated ? "warning" : "success",
        title: result.truncated ? "Export truncated" : "Export ready",
        message: result.truncated
          ? `Downloaded ${result.exportedRows} rows to ${result.filename}. Export truncated to ${EXPORT_ROW_LIMIT.toLocaleString()} rows.`
          : `Downloaded ${result.exportedRows} rows to ${result.filename}.`,
      });
    } catch (error) {
      const message =
        error instanceof Error ? error.message : "Unable to export this view.";
      setOperationNotice({
        tone: "error",
        title: "Export failed",
        message,
      });
    }
  }

  function handleRequestDeleteRows(rowIds: string[]) {
    const nextRowIds = Array.from(new Set(rowIds.filter(Boolean)));
    if (nextRowIds.length === 0) return;
    setOperationNotice(null);
    setDeleteDialogRowIds(nextRowIds);
  }

  function handleDeleteShortcut(focusedRowId: string | null) {
    const rowIds =
      selectedRowIds.length > 0
        ? selectedRowIds
        : focusedRowId
          ? [focusedRowId]
          : [];
    handleRequestDeleteRows(rowIds);
  }

  async function handleConfirmDeleteRows() {
    const rowIds = deleteDialogRowIds ?? [];
    setDeleteDialogRowIds(null);
    if (rowIds.length === 0) return;
    const deleteSnapshots = rowIds
      .map((rowId) => {
        const index =
          view?.rows.findIndex((row) => String(row.id ?? "") === rowId) ?? -1;
        if (index === -1) return null;
        return { row: { ...view!.rows[index]! }, index };
      })
      .filter(
        (item): item is { row: Record<string, unknown>; index: number } =>
          item !== null,
      );

    const results = await runInConcurrentBatches(
      rowIds,
      BULK_DELETE_CONCURRENCY,
      (rowId) => deleteRow(resolvedTable.name, rowId),
    );
    const successCount = results.filter(Boolean).length;
    const failureCount = rowIds.length - successCount;
    const successfulDeletes = deleteSnapshots.filter(
      (snapshot, index) => results[index],
    );

    if (successfulDeletes.length > 0) {
      pushUndoDeleteEntry(resolvedTable.name, successfulDeletes);
    }

    if (rowIds.length > 1) {
      const tone =
        failureCount === 0
          ? "success"
          : successCount === 0
            ? "error"
            : "warning";
      const title =
        failureCount === 0
          ? "Rows deleted"
          : successCount === 0
            ? "No rows deleted"
            : "Some rows couldn't be deleted";
      const message =
        failureCount === 0
          ? `Deleted ${successCount} ${getRowLabel(successCount)}.`
          : `Deleted ${successCount} of ${rowIds.length} rows.`;
      setOperationNotice({ tone, title, message });
    }
  }

  function handleDuplicateSelectedRow() {
    const rowId = selectedRowIds[0];
    if (!rowId || selectedRowIds.length !== 1) return;
    void duplicateRow(resolvedTable.name, rowId);
  }

  const selectionActionItems = relationDrillIns.map((drillIn) => ({
    id: drillIn.accessor,
    label: drillIn.label,
    disabled:
      selectedRowIds.length === 0 ||
      (drillIn.mode === "endpoint" && selectedRowIds.length !== 1) ||
      (drillIn.mode === "filter" &&
        !!drillIn.valueColumn &&
        selectedRows.every(
          (row) =>
            row[drillIn.valueColumn!] === null ||
            row[drillIn.valueColumn!] === undefined ||
            row[drillIn.valueColumn!] === "",
        )),
    onSelect: () => handleOpenRelated(drillIn.accessor, drillIn.targetTable),
  }));

  const rowSelectionMenuItems =
    selectedRowIds.length > 0
      ? [
          {
            id: "duplicate-row",
            label: "Duplicate row",
            disabled: selectedRowIds.length !== 1,
            onSelect: handleDuplicateSelectedRow,
          },
          {
            id: "delete-row",
            label:
              selectedRowIds.length === 1
                ? "Delete row"
                : `Delete ${selectedRowIds.length} rows`,
            onSelect: () => handleRequestDeleteRows(selectedRowIds),
          },
        ]
      : [];

  return (
    <div className="table-detail">
      <header className="table-detail__header">
      <div className="table-detail__heading">
      <h1 className="table-detail__title">{displayName}</h1>
      <nav className="table-tabs" aria-label="Tables">
        {tableTabs.map((tab) => (
          <NavLink
            key={tab.name}
            to={`/data/${tab.name}`}
            className={({ isActive }) =>
              isActive
                ? "table-tabs__tab table-tabs__tab--active"
                : "table-tabs__tab"
            }
          >
            <span className="table-tabs__tab-label">
              {tab.displayName ?? tab.name}
            </span>
            {allTabCountsKnown ? (
              <span className="table-tabs__tab-count" aria-hidden="true">
                {tabCounts[tab.name]}
              </span>
            ) : null}
          </NavLink>
        ))}
      </nav>
      </div>
      <div className="table-detail__views" ref={setViewSlot} />
      </header>

      {/* Data panel */}
      {view && dataStatus !== "idle" ? (
        <section
          className="table-detail__workspace"
          aria-label={`${displayName} data grid`}
        >
          {context && context.kind !== "reference" ? (
            <div className="explorer-context__wrapper">
              <div className="explorer-context">
                <div className="explorer-context__summary">
                  <span className="explorer-context__eyebrow">
                    <Icon name="arrow-right" size={12} strokeWidth={1.5} />
                    Related records
                  </span>
                  <span className="explorer-context__title">
                    {displayName} related to {contextParentDisplayName}{" "}
                    {context.parentRowIds.map((rowId) => `#${rowId}`).join(", ")}
                  </span>
                </div>
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  onClick={handleClearContext}
                >
                  Show all {displayName}
                </Button>
              </div>
            </div>
          ) : null}

          <DataQueryControls
            viewSlot={viewSlot}
            table={resolvedTable}
            query={query}
            disabled={dataStatus === "loading" && view.rows.length === 0}
            externalPopoverOpen={exportMenuOpen}
            onActivePanelChange={(panel) => {
              if (panel !== null) setExportMenuOpen(false);
            }}
            toolbarActions={
              <>
                <div className="data-controls__export" ref={exportMenuRef}>
                  <Button
                    type="button"
                    variant="ghost"
                    className={
                      exportMenuOpen
                        ? "data-controls__menu-button data-controls__menu-button--active"
                        : "data-controls__menu-button"
                    }
                    aria-label="Export"
                    aria-haspopup="menu"
                    aria-expanded={exportMenuOpen}
                    onClick={() => setExportMenuOpen((open) => !open)}
                    disabled={isExporting}
                  >
                    <span className="data-controls__menu-button-content">
                      <Icon name="download" size={14} />
                      <span className="data-controls__menu-button-label">Export</span>
                    </span>
                  </Button>
                  {exportMenuOpen ? (
                    <div
                      className="data-controls__export-menu"
                      role="menu"
                      aria-label="Export current view"
                    >
                      <button
                        type="button"
                        className="data-controls__export-option"
                        role="menuitem"
                        onClick={() => void handleExport("csv")}
                      >
                        CSV
                      </button>
                      <button
                        type="button"
                        className="data-controls__export-option"
                        role="menuitem"
                        onClick={() => void handleExport("json")}
                      >
                        JSON
                      </button>
                    </div>
                  ) : null}
                </div>
                {isExporting ? (
                  <div className="data-controls__export-status" aria-live="polite">
                    <span className="data-controls__export-progress">
                      {getExportProgressLabel(
                        exportFormat,
                        exportPhase,
                        exportedRowsSoFar,
                        exportTotalRows,
                      )}
                    </span>
                    <Button
                      type="button"
                      variant="ghost"
                      size="sm"
                      onClick={cancelExport}
                    >
                      Cancel
                    </Button>
                  </div>
                ) : null}
                {undoToastVisible && topUndoEntry ? (
                  <Button
                    type="button"
                    variant="ghost"
                    size="sm"
                    onClick={() => void handleUndoLast()}
                    leadingIcon={<Icon name="refresh" size={13} />}
                  >
                    {topUndoEntry.rows.length === 1
                      ? "Undo delete"
                      : `Undo delete (${topUndoEntry.rows.length})`}
                  </Button>
                ) : null}
              </>
            }
            savedViews={view.savedViews}
            activeViewId={view.activeViewId}
            onSwitchView={switchView}
            onSaveCurrentAsView={(name, type) =>
              saveCurrentAsView(name, type, false)
            }
            onUpdateActiveViewConfig={updateActiveViewConfig}
            onRenameView={renameView}
            onToggleViewShared={toggleViewShared}
            onDeleteView={deleteView}
            onChangeViewType={changeViewType}
            defaultViewId={defaultViewId}
            onSetDefaultView={setDefaultView}
            onDuplicateView={duplicateView}
            currentUserId={currentUserId}
            autoSave={view.autoSave}
            onSetAutoSave={setAutoSave}
            currentViewConfig={view.config}
            hiddenColumnNames={hiddenColumnNames}
            selectionPanel={
              selectedRowIds.length > 0
                ? {
                    count: selectedRowIds.length,
                    content: (
                      <div className="explorer-related">
                        <span className="explorer-related__label">
                          Selected rows
                        </span>
                        <span className="explorer-context__description">
                          {selectedRowIds.length === 1
                            ? "Duplicate, delete, or open related rows from the selected row."
                            : "Delete the selected rows or open their related records."}
                        </span>
                        <div className="explorer-related__actions">
                          <Button
                            type="button"
                            variant="ghost"
                            size="sm"
                            onClick={handleClearSelection}
                          >
                            Clear selection
                          </Button>
                          <Button
                            type="button"
                            variant="ghost"
                            size="sm"
                            disabled={selectedRowIds.length !== 1}
                            onClick={handleDuplicateSelectedRow}
                            leadingIcon={<Icon name="copy" size={13} />}
                          >
                            Duplicate
                          </Button>
                          <Button
                            type="button"
                            variant="ghost"
                            size="sm"
                            onClick={() =>
                              handleRequestDeleteRows(selectedRowIds)
                            }
                            leadingIcon={<Icon name="trash" size={13} />}
                          >
                            Delete
                          </Button>
                          {relationDrillIns.map((drillIn) => (
                            <Button
                              key={drillIn.accessor}
                              type="button"
                              variant="ghost"
                              size="sm"
                              title={drillIn.description}
                              disabled={
                                selectedRowIds.length === 0 ||
                                (drillIn.mode === "endpoint" &&
                                  selectedRowIds.length !== 1) ||
                                (drillIn.mode === "filter" &&
                                  !!drillIn.valueColumn &&
                                  selectedRows.every(
                                    (row) =>
                                      row[drillIn.valueColumn!] === null ||
                                      row[drillIn.valueColumn!] === undefined ||
                                      row[drillIn.valueColumn!] === "",
                                  ))
                              }
                              onClick={() =>
                                handleOpenRelated(
                                  drillIn.accessor,
                                  drillIn.targetTable,
                                )
                              }
                              trailingIcon={
                                <Icon name="arrow-right" size={13} />
                              }
                            >
                              {drillIn.label}
                            </Button>
                          ))}
                        </div>
                      </div>
                    ),
                  }
                : null
            }
            suppressAutoPanel={context !== null}
            panelResetKey={`${resolvedTable.name}:${context?.kind ?? "base"}:${selectedRowIds.join(",") || "none"}`}
            filterShortcut={filterShortcut}
            sortShortcut={sortShortcut}
            onSearch={handleSearchChange}
            onSortChange={handleSortChange}
            onPerPageChange={handlePerPageChange}
            onPageChange={handlePageChange}
            onFiltersChange={handleFiltersChange}
            onHiddenColumnsChange={handleHiddenColumnsChange}
            onResetView={handleResetView}
            viewType={view.config.type}
            onConfigureKanban={() => setKanbanConfigOpen(true)}
          />

          {view.config.type === "kanban" ? (
            <KanbanConfigPanel
              open={kanbanConfigOpen}
              table={resolvedTable}
              kanbanColumn={view.config.kanbanColumn}
              cardFieldNames={view.config.cardFieldNames}
              hiddenLaneValues={view.config.hiddenLaneValues ?? []}
              onApply={(patch) => setKanbanConfig(patch)}
              onClose={() => setKanbanConfigOpen(false)}
            />
          ) : null}

          {dataStatus === "error" && dataError ? (
            <Alert
              tone="error"
              title="Failed to load data"
              message={
                <>
                  <span>{dataError}</span>{" "}
                  <Button
                    variant="ghost"
                    size="sm"
                    onClick={() => void fetchRows()}
                    leadingIcon={<Icon name="refresh" size={13} />}
                  >
                    Retry
                  </Button>
                </>
              }
            />
          ) : null}

          {operationNotice ? (
            <Alert
              tone={operationNotice.tone}
              title={operationNotice.title}
              message={operationNotice.message}
            />
          ) : null}

          {dataStatus === "loading" && view.rows.length > 0 ? (
            <div className="data-panel__status" aria-live="polite">
              Refreshing results...
            </div>
          ) : null}

          {dataStatus === "loading" &&
          view.rows.length === 0 &&
          view.config.type !== "kanban" ? (
            <div className="explorer-loading" data-testid="data-loading">
              <Spinner label="Loading rows" />
            </div>
          ) : (
            <>
              <ViewRenderer
                type={view.config.type}
                viewConfig={view.config}
                loading={dataStatus === "loading"}
                onConfigureView={() => setKanbanConfigOpen(true)}
                table={visibleTable ?? resolvedTable}
                rows={view.rows}
                page={query.page}
                perPage={query.perPage}
                pendingNewRowIds={pendingNewRowIds}
                rowErrors={rowErrors}
                selectedRowIds={selectedRowIds}
                onAddRow={
                  view.config.type === "table" && context === null
                    ? handleAddRow
                    : undefined
                }
                onDeleteRowsShortcut={handleDeleteShortcut}
                onOpenRecord={(row) => {
                  const rowId = row.id;
                  if (rowId === null || rowId === undefined || rowId === "") return;
                  void openRecordEditor({
                    table: resolvedTable.name,
                    rowId: String(rowId),
                    row,
                  });
                }}
                onRequestFilterColumn={(column) =>
                  handleRequestFilterColumn(column.name)
                }
                onRequestSortColumn={(column) =>
                  handleRequestSortColumn(column.name)
                }
                onRequestHideColumn={handleRequestHideColumn}
                onNavigateReference={handleNavigateReference}
                onToggleRowSelection={handleToggleRowSelection}
                onToggleSelectAllRows={handleToggleSelectAllRows}
                onSetSelectedRowIds={handleSetSelectedRows}
                onClearSelection={handleClearSelection}
                editingCell={editingCell}
                cellErrors={cellErrors}
                onStartEdit={(row, column) => {
                  if (!resolvedTable) return;
                  const rowId = String(row.id ?? "");
                  if (!rowId) return;
                  startCellEdit(resolvedTable.name, rowId, column.name);
                }}
                onCancelEdit={() => {
                  if (!resolvedTable) return;
                  cancelCellEdit(resolvedTable.name);
                }}
                onCommitEdit={(row, column, nextValue, intent) => {
                  if (!resolvedTable) return;
                  const rowId = String(row.id ?? "");
                  if (!rowId) return;
                  void commitCellEdit(
                    resolvedTable.name,
                    rowId,
                    column.name,
                    nextValue,
                    intent,
                  );
                }}
                onResumeDraft={(row) => {
                  if (!resolvedTable) return;
                  const rowId = String(row.id ?? "");
                  const columnName = getDraftFocusColumn(resolvedTable, row, cellErrors[rowId]);
                  if (rowId && columnName) startCellEdit(resolvedTable.name, rowId, columnName);
                }}
                onDiscardDraft={(row) => {
                  if (!resolvedTable) return;
                  const rowId = String(row.id ?? "");
                  if (rowId) void deleteRow(resolvedTable.name, rowId);
                }}
                selectionMenuItems={
                  selectedRowIds.length > 0
                    ? [
                        ...rowSelectionMenuItems,
                        {
                          id: "clear-selection",
                          label: "Clear selection",
                          onSelect: handleClearSelection,
                        },
                        ...selectionActionItems,
                      ]
                    : []
                }
              />
              {view.config.type !== "kanban" ? (
                <DataPaginationBar
                  query={query}
                  total={view.meta?.total ?? 0}
                  totalPages={totalPages}
                  disabled={dataStatus === "loading"}
                  placement="bottom"
                  onPerPageChange={handlePerPageChange}
                  onPageChange={handlePageChange}
                />
              ) : null}
            </>
          )}
        </section>
      ) : (
        <div className="explorer-loading" data-testid="data-loading">
          <Spinner label="Loading rows" />
        </div>
      )}
      <ConfirmDialog
        open={deleteDialogRowIds !== null}
        title={
          (deleteDialogRowIds?.length ?? 0) === 1
            ? "Delete this row?"
            : `Delete ${deleteDialogRowIds?.length ?? 0} rows?`
        }
        description="You can undo this right after."
        confirmLabel={
          (deleteDialogRowIds?.length ?? 0) === 1 ? "Delete row" : "Delete rows"
        }
        confirmVariant="danger"
        onConfirm={() => void handleConfirmDeleteRows()}
        onCancel={() => setDeleteDialogRowIds(null)}
      />
      <RecordEditor />
    </div>
  );
}

function getExportProgressLabel(
  format: ExportFormat | null,
  phase: "paging" | "references" | "download" | null,
  fetchedRows: number,
  totalRows: number | null,
) {
  const formatLabel = format?.toUpperCase() ?? "Export";
  if (phase === "references") {
    return `Preparing ${formatLabel}…`;
  }
  if (phase === "download") {
    return `Preparing ${formatLabel} download`;
  }
  if (totalRows !== null) {
    return `Exporting ${formatLabel} ${fetchedRows.toLocaleString()}/${totalRows.toLocaleString()} rows`;
  }
  return `Exporting ${formatLabel} ${fetchedRows.toLocaleString()} ${getRowLabel(fetchedRows)}`;
}

function getRowLabel(count: number) {
  return count === 1 ? "row" : "rows";
}
