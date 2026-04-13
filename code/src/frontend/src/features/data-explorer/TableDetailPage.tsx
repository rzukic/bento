import { useEffect, useMemo, useState } from "react";
import { NavLink, useNavigate, useParams } from "react-router-dom";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { EmptyState } from "../../components/ui/EmptyState";
import { Panel } from "../../components/ui/Panel";
import { Spinner } from "../../components/ui/Spinner";
import { useExplorerStore } from "../../stores/explorerStore";
import { useSchemaStore } from "../../stores/schemaStore";
import {
  DEFAULT_QUERY,
  type FilterClause,
  type SortClause,
} from "../../types/explorer";
import type { ModelColumn } from "../../types/api";
import {
  DataPaginationBar,
  DataQueryControls,
} from "./components/DataQueryControls";
import { ReadOnlyDataGrid } from "./components/ReadOnlyDataGrid";
import {
  getReferencedTableName,
  getRelatedDrillIns,
} from "./relationNavigation";
import "./DataExplorer.css";

export function TableDetailPage() {
  const { tableId } = useParams<{ tableId: string }>();
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
  const openRecordContext = useExplorerStore((state) => state.openRecordContext);
  const toggleRowSelection = useExplorerStore((state) => state.toggleRowSelection);
  const toggleSelectAllVisible = useExplorerStore((state) => state.toggleSelectAllVisible);
  const setSelectedRows = useExplorerStore((state) => state.setSelectedRows);
  const setHiddenColumns = useExplorerStore((state) => state.setHiddenColumns);
  const clearSelection = useExplorerStore((state) => state.clearSelection);
  const openRelatedContext = useExplorerStore((state) => state.openRelatedContext);
  const clearContext = useExplorerStore((state) => state.clearContext);
  const view = useExplorerStore((state) =>
    tableId ? (state.views[tableId] ?? null) : null,
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
  const [filterShortcut, setFilterShortcut] = useState<{
    columnName: string;
    nonce: number;
  } | null>(null);
  const [sortShortcut, setSortShortcut] = useState<{
    columnName: string;
    nonce: number;
  } | null>(null);

  const table = useMemo(() => {
    if (!schemaReady) return null;
    return snapshot.tables.find((t) => t.name === tableId) ?? null;
  }, [schemaReady, snapshot, tableId]);
  const relationDrillIns = useMemo(
    () => (snapshot && table ? getRelatedDrillIns(snapshot, table) : []),
    [snapshot, table]
  );
  const hiddenColumnNames = useMemo(
    () =>
      !table
        ? []
        : (view?.hiddenColumnNames ?? []).filter((columnName) =>
            table.columns.some(
              (column) => !column.primaryKey && column.name === columnName,
            ),
          ),
    [table, view?.hiddenColumnNames],
  );
  const visibleTable = useMemo(
    () =>
      !table
        ? null
        : {
            ...table,
            columns: table.columns.filter(
              (column) => column.primaryKey || !hiddenColumnNames.includes(column.name),
            ),
          },
    [hiddenColumnNames, table],
  );
  const visibleRowIds = useMemo(
    () =>
      view?.rows
        .map((row) => row.id)
        .filter(
          (value): value is string | number =>
            value !== null && value !== undefined && value !== "",
        )
        .map((value) => String(value)) ?? [],
    [view?.rows],
  );

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
                {schemaError ?? "Unable to load the schema snapshot."}
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

  // Table found — render detail
  const displayName = resolvedTable.displayName ?? resolvedTable.name;

  const dataStatus = view?.status ?? "idle";
  const dataError = view?.error ?? null;
  const query = view?.query ?? DEFAULT_QUERY;
  const totalPages = view?.meta?.total_pages ?? 1;
  const selectedRowIds = view?.selectedRowIds ?? [];
  const context = view?.context ?? null;

  function handleSortChange(sortClauses: SortClause[]) {
    setSort(sortClauses);
  }

  function handlePerPageChange(perPage: number) {
    setQuery({ perPage, page: 1 });
  }

  function handleFiltersChange(filters: FilterClause[]) {
    if (context?.kind === "reference") {
      clearContext(resolvedTable.name, {
        search: query.search,
        sort: query.sort,
        perPage: query.perPage,
        filters,
        page: 1
      });
      void fetchRows();
      return;
    }
    setFilters(filters);
  }

  function handleResetView() {
    setQuery({
      page: 1,
      perPage: DEFAULT_QUERY.perPage,
      sort: [],
      search: "",
      filters: [],
    });
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
    row: Record<string, unknown>
  ) {
    const targetTable = getReferencedTableName(column);
    const targetRowId = Number(value);
    const sourceRowId = Number(row.id);
    if (!targetTable || Number.isNaN(targetRowId) || Number.isNaN(sourceRowId)) {
      return;
    }

    openRecordContext(targetTable, targetRowId, {
      sourceTable: resolvedTable.name,
      sourceRowId,
      sourceColumn: column.displayName ?? column.name
    });
    navigate(`/data/${targetTable}`);
  }

  function handleOpenRelated(accessor: string, targetTable: string) {
    const drillIn = relationDrillIns.find((item) => item.accessor === accessor);
    if (!drillIn || selectedRowIds.length === 0) return;

    openRelatedContext(
      targetTable,
      resolvedTable.name,
      selectedRowIds,
      accessor,
      drillIn.filterColumn,
    );
    navigate(`/data/${targetTable}`);
  }

  function handleToggleRowSelection(row: Record<string, unknown>) {
    const rowId = Number(row.id);
    if (Number.isNaN(rowId)) return;

    toggleRowSelection(resolvedTable.name, String(rowId));
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

  const selectionActionItems = relationDrillIns.map((drillIn) => ({
    id: drillIn.accessor,
    label: drillIn.label,
    disabled: selectedRowIds.length === 0,
    onSelect: () => handleOpenRelated(drillIn.accessor, drillIn.targetTable),
  }));

  return (
    <div className="table-detail">
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
          </NavLink>
        ))}
      </nav>

      {/* Data panel */}
      {view && dataStatus !== "idle" ? (
        <section className="table-detail__workspace" aria-label={`${displayName} data grid`}>
          {context ? (
            <div className="explorer-context">
              <div className="explorer-context__summary">
                {context.kind === "reference" ? (
                  <>
                    <span className="explorer-context__eyebrow">Focused record</span>
                    <span className="explorer-context__title">
                      {displayName} row #{context.rowId}
                    </span>
                    <span className="explorer-context__description">
                      Opened from {context.sourceTable} #{context.sourceRowId} via {context.sourceColumn}.
                    </span>
                  </>
                ) : (
                  <>
                    <span className="explorer-context__eyebrow">Related records</span>
                    <span className="explorer-context__title">
                      {displayName} related to {context.parentTable} rows{" "}
                      {context.parentRowIds.slice(0, 3).join(", ")}
                      {context.parentRowIds.length > 3
                        ? ` +${context.parentRowIds.length - 3} more`
                        : ""}
                    </span>
                    <span className="explorer-context__description">
                      Using relation accessor {context.accessor} across{" "}
                      {context.parentRowIds.length} candidate
                      {context.parentRowIds.length !== 1 ? "s" : ""}.
                    </span>
                  </>
                )}
              </div>
              <Button type="button" variant="ghost" size="sm" onClick={handleClearContext}>
                Show all {displayName}
              </Button>
            </div>
          ) : null}

          <DataQueryControls
            table={resolvedTable}
            query={query}
            total={view.meta?.total ?? 0}
            totalPages={totalPages}
            disabled={dataStatus === "loading" && view.rows.length === 0}
            hiddenColumnNames={hiddenColumnNames}
            selectionPanel={
              selectedRowIds.length > 0 && relationDrillIns.length > 0
                ? {
                    count: selectedRowIds.length,
                    content: (
                      <div className="explorer-related">
                        <span className="explorer-related__label">Selection operations</span>
                        <span className="explorer-context__description">
                          Open related rows from the {selectedRowIds.length} selected row
                          {selectedRowIds.length !== 1 ? "s" : ""}.
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
                          {relationDrillIns.map((drillIn) => (
                            <Button
                              key={drillIn.accessor}
                              type="button"
                              variant="ghost"
                              size="sm"
                              title={drillIn.description}
                              disabled={selectedRowIds.length === 0}
                              onClick={() =>
                                handleOpenRelated(
                                  drillIn.accessor,
                                  drillIn.targetTable,
                                )
                              }
                              trailingIcon={<Icon name="arrow-right" size={13} />}
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
            onSearch={setSearch}
            onSortChange={handleSortChange}
            onPerPageChange={handlePerPageChange}
            onPageChange={setPage}
            onFiltersChange={handleFiltersChange}
            onHiddenColumnsChange={handleHiddenColumnsChange}
            onResetView={handleResetView}
          />

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

          {dataStatus === "loading" && view.rows.length > 0 ? (
            <div className="data-panel__status" aria-live="polite">
              Refreshing results...
            </div>
          ) : null}

          {dataStatus === "loading" && view.rows.length === 0 ? (
            <div className="explorer-loading" data-testid="data-loading">
              <Spinner label="Loading rows" />
            </div>
          ) : view.rows.length === 0 ? (
            <>
              <EmptyState
                icon="database"
                title={
                  query.search || query.filters.length > 0
                    ? "No matching rows"
                    : "No rows"
                }
                description={
                  query.search || query.filters.length > 0
                    ? "The current query returned no results. Adjust the search, filters, or sort to widen the view."
                    : "This table exists in the schema but has no data yet."
                }
                actions={
                  query.search || query.filters.length > 0 ? (
                    <Button
                      type="button"
                      variant="ghost"
                      onClick={handleResetView}
                    >
                      Reset view
                    </Button>
                  ) : undefined
                }
              />
            </>
          ) : (
            <>
              <ReadOnlyDataGrid
                table={visibleTable ?? resolvedTable}
                rows={view.rows}
                page={query.page}
                perPage={query.perPage}
                selectedRowIds={selectedRowIds}
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
                selectionMenuItems={
                  selectedRowIds.length > 0
                    ? [
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
              <DataPaginationBar
                query={query}
                total={view.meta?.total ?? 0}
                totalPages={totalPages}
                disabled={dataStatus === "loading"}
                placement="bottom"
                onPerPageChange={handlePerPageChange}
                onPageChange={setPage}
              />
            </>
          )}
        </section>
      ) : (
        <Panel muted>
          <EmptyState
            icon="database"
            title="Data grid coming soon"
            description="TASK-069 will add the read-only data grid here, backed by the query client from TASK-068."
          />
        </Panel>
      )}
    </div>
  );
}
