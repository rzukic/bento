import { create } from "zustand";
import {
  createRow,
  deleteRow as deleteRowApi,
  fetchRelatedRows,
  fetchRow,
  fetchTableRows,
  patchRow,
  type ExplorerDataSource,
} from "../api/data";
import { ApiClientError } from "../api/client";
import {
  listViews,
  createView,
  updateView,
  deleteView as deleteViewApi
} from "../api/views";
import { updateExplorerTablePreferences } from "../api/explorerPreferences";
import { registerLogoutHook, useAuthStore } from "./authStore";
import { useSchemaStore } from "./schemaStore";
import type {
  ExplorerViewConfig,
  ExplorerViewType,
  ExplorerQuery,
  FilterClause,
  ListMeta,
  SortClause
} from "../types/explorer";
import {
  buildExplorerQuery,
  DEFAULT_EXPLORER_VIEW_CONFIG,
  DEFAULT_QUERY,
  patchExplorerViewConfig
} from "../types/explorer";
import type { SavedView } from "../types/views";
import type { ModelColumn, ModelTable } from "../types/api";

// ── Per-table view state ──────────────────────────────────────────────

export type TableViewStatus = "idle" | "loading" | "ready" | "error";
export type SavedViewsStatus = "idle" | "loading" | "ready" | "error";
const UNDO_STACK_LIMIT = 10;
const UNDO_RESTORE_CONCURRENCY = 4;

export type TableViewState = {
  status: TableViewStatus;
  config: ExplorerViewConfig;
  page: number;
  perPage: number;
  rows: Record<string, unknown>[];
  meta: ListMeta | null;
  error: string | null;
  source: ExplorerDataSource;
  selectedRowIds: string[];
  editingCell: { rowId: string; columnName: string } | null;
  cellErrors: Record<string, Record<string, string>>;
  /**
   * In-flight cell PATCH saves keyed `${rowId}:${columnName}`. Used by the
   * grid to render a per-cell pending indicator while the round-trip runs.
   */
  pendingCellSaves: string[];
  pendingNewRowIds: string[];
  rowErrors: Record<string, string>;
  context:
    | {
        kind: "reference";
        rowId: number;
        sourceTable: string;
        sourceRowId: number;
        sourceColumn: string;
      }
    | {
        kind: "related";
        parentTable: string;
        parentRowIds: string[];
        accessor: string;
        via: "filter" | "endpoint";
        filterColumn?: string;
      }
    | null;
  // ── Saved-views state ──
  savedViews: SavedView[];
  savedViewsStatus: SavedViewsStatus;
  activeViewId: number | null;  // null = "All Records" default
  autoSave: boolean;
};

type UndoDeletedRowSnapshot = {
  row: Record<string, unknown>;
  index: number;
};

type UndoEntry = {
  kind: "delete";
  table: string;
  rows: UndoDeletedRowSnapshot[];
  at: number;
};

function cloneExplorerViewConfig(config: ExplorerViewConfig): ExplorerViewConfig {
  return patchExplorerViewConfig(config, {});
}

/**
 * Rebuild a config to match a target type. When saved.type and config.type
 * disagree (e.g. a view was saved while the in-memory config happened to
 * belong to a different type), this forces the config shape to the correct
 * type, preserving the shared base fields.
 */
function rebuildConfigForType(
  config: ExplorerViewConfig,
  type: ExplorerViewType
): ExplorerViewConfig {
  const base = {
    search: config.search,
    sort: config.sort.map((s) => ({ ...s })),
    filters: config.filters.map((f) => ({ ...f })),
    hiddenColumnNames: [...config.hiddenColumnNames],
    columnOrder: [...config.columnOrder]
  };
  if (type === "kanban") {
    return {
      ...base,
      type: "kanban",
      kanbanColumn: config.type === "kanban" ? config.kanbanColumn : null,
      cardFieldNames: config.type === "kanban" ? [...config.cardFieldNames] : [],
      hiddenLaneValues: config.type === "kanban" ? [...(config.hiddenLaneValues ?? [])] : []
    };
  }
  return { ...base, type: "table" };
}

export function extractExplorerViewConfig(view: TableViewState): ExplorerViewConfig {
  return cloneExplorerViewConfig(view.config);
}

export function hydrateTableViewFromConfig(
  config: ExplorerViewConfig
): TableViewState {
  return {
    status: "idle",
    config: cloneExplorerViewConfig(config),
    page: DEFAULT_QUERY.page,
    perPage: DEFAULT_QUERY.perPage,
    rows: [],
    meta: null,
    error: null,
    source: { kind: "table" },
    selectedRowIds: [],
    editingCell: null,
    cellErrors: {},
    pendingCellSaves: [],
    pendingNewRowIds: [],
    rowErrors: {},
    context: null,
    savedViews: [],
    savedViewsStatus: "idle",
    activeViewId: null,
    autoSave: false
  };
}

function freshTableView(): TableViewState {
  return hydrateTableViewFromConfig(DEFAULT_EXPLORER_VIEW_CONFIG);
}

// ── Cell-error helpers (used by the inline-edit save round-trip) ──────────

function clearCellError(
  errors: Record<string, Record<string, string>>,
  rowId: string,
  columnName: string,
): Record<string, Record<string, string>> {
  const rowErrors = errors[rowId];
  if (!rowErrors || rowErrors[columnName] === undefined) return errors;
  const { [columnName]: _removed, ...rest } = rowErrors;
  const nextErrors = { ...errors };
  if (Object.keys(rest).length === 0) {
    delete nextErrors[rowId];
  } else {
    nextErrors[rowId] = rest;
  }
  return nextErrors;
}

function setCellErrorIn(
  errors: Record<string, Record<string, string>>,
  rowId: string,
  columnName: string,
  message: string,
): Record<string, Record<string, string>> {
  const rowErrors = { ...(errors[rowId] ?? {}) };
  rowErrors[columnName] = message;
  return { ...errors, [rowId]: rowErrors };
}

type FieldError = { field: string; message: string };

function extractFieldErrors(err: unknown): FieldError[] | null {
  if (!(err instanceof ApiClientError)) return null;
  if (err.status < 400 || err.status >= 500) return null;
  const details = err.details as { errors?: unknown } | undefined;
  const list = details?.errors;
  if (!Array.isArray(list)) return null;
  const out: FieldError[] = [];
  for (const item of list) {
    if (item && typeof item === "object") {
      const field = (item as { field?: unknown }).field;
      const message = (item as { message?: unknown }).message;
      if (typeof field === "string" && typeof message === "string") {
        out.push({ field, message });
      }
    }
  }
  return out.length > 0 ? out : null;
}

export function getExplorerQueryForView(view: TableViewState): ExplorerQuery {
  return buildExplorerQuery(view.config, {
    page: view.page,
    perPage: view.perPage
  });
}

// ── Auto-save debounce ─────────────────────────────────────────────────────
// Module-level timers so they survive store re-renders without polluting
// persisted state. Timers are isolated per scheduling table/view target.

type AutoSaveTarget = { tableId: string; viewId: number };

const AUTO_SAVE_DEBOUNCE_MS = 500;
const _autoSaveTimers = new Map<string, ReturnType<typeof setTimeout>>();

function getAutoSaveTarget(tableId?: string | null): AutoSaveTarget | null {
  const store = useExplorerStore.getState();
  const targetTable = tableId ?? store.activeTable;
  if (!targetTable) return null;
  const view = store.views[targetTable];
  if (!view || view.activeViewId === null) return null;
  return { tableId: targetTable, viewId: view.activeViewId };
}

function getAutoSaveKey(target: AutoSaveTarget): string {
  return `${target.tableId}:${target.viewId}`;
}

function scheduleAutoSave(target = getAutoSaveTarget()): void {
  if (!target) return;
  const key = getAutoSaveKey(target);
  const existing = _autoSaveTimers.get(key);
  if (existing !== undefined) {
    clearTimeout(existing);
  }

  const timer = setTimeout(() => {
    _autoSaveTimers.delete(key);
    const latest = useExplorerStore.getState();
    const view = latest.views[target.tableId];
    if (!view?.autoSave) return;
    void latest.updateViewConfigFor(target.tableId, target.viewId);
  }, AUTO_SAVE_DEBOUNCE_MS);

  _autoSaveTimers.set(key, timer);
}

function cancelAutoSave(target?: AutoSaveTarget | null): void {
  if (!target) {
    for (const timer of _autoSaveTimers.values()) {
      clearTimeout(timer);
    }
    _autoSaveTimers.clear();
    return;
  }

  const key = getAutoSaveKey(target);
  const timer = _autoSaveTimers.get(key);
  if (timer === undefined) return;
  clearTimeout(timer);
  _autoSaveTimers.delete(key);
}

// ── Store shape ───────────────────────────────────────────────────────

/**
 * A single entry in the record-editor navigation stack (TASK-099). Each push
 * represents a related record that the user drilled into from the previous
 * entry's FK value. Loading and error states are per-entry so a failed deep
 * push does not blow away earlier entries.
 */
export type RecordEditorEntry = {
  table: string;
  rowId: string;
  status: "loading" | "ready" | "error";
  row: Record<string, unknown> | null;
  error: string | null;
};

/**
 * The in-modal navigation stack. When `open` is true and `stack` is non-empty
 * the modal renders the top entry; closing clears the stack entirely.
 */
export type RecordEditorState = {
  open: boolean;
  stack: RecordEditorEntry[];
};

type RecordCacheByTable = Record<string, Record<string, Record<string, unknown>>>;

type ExplorerStoreState = {
  /** The currently-active table name (from the route param). */
  activeTable: string | null;

  /** Cached view state per table name. Survives in-app navigation. */
  views: Record<string, TableViewState>;

  /**
   * Single-record cache used by the full-screen editor. Unlike `views[table]`,
   * this cache is allowed to hold records that are not part of the table's
   * current query/page result set.
   */
  recordCacheByTable: RecordCacheByTable;

  /** Session-only undo history for destructive row actions, keyed by table. */
  undoStackByTable: Record<string, UndoEntry[]>;

  /**
   * The currently-open full-screen record editor (TASK-098). Null when
   * dismissed. The editor reads from a dedicated single-record cache while
   * still reconciling visible grid rows in-place when they are part of the
   * current table result set.
   */
  recordEditor: RecordEditorState | null;

  // ── Row-fetch actions ──

  /** Set the active table and ensure a view entry exists. */
  setActiveTable: (table: string) => void;

  /** Fetch rows for the active table using its current query state. */
  fetchRows: () => Promise<void>;

  /** Replace the full query and refetch. Page resets to 1. */
  setQuery: (patch: Partial<ExplorerQuery>) => void;

  /**
   * Apply route-query state against a clean "All Records" base instead of the
   * currently active saved/ad-hoc view. Used for shareable explorer URLs.
   */
  applyRouteQuery: (table: string, patch: Partial<ExplorerQuery>) => void;

  /** Navigate to a specific page and refetch. */
  setPage: (page: number) => void;

  /** Replace the sort array and refetch from page 1. */
  setSort: (sort: SortClause[]) => void;

  /** Replace the search string and refetch from page 1. */
  setSearch: (search: string) => void;

  /** Replace the filter array and refetch from page 1. */
  setFilters: (filters: FilterClause[]) => void;

  // ── Context/selection actions ──

  /** Open a table focused on a single related/reference row. */
  openRecordContext: (table: string, rowId: number, context: {
    sourceTable: string;
    sourceRowId: number;
    sourceColumn: string;
  }) => void;

  /** Toggle a single selected record from the current table grid. */
  toggleRowSelection: (table: string, rowId: string) => void;

  /** Toggle all visible rows in the current table grid. */
  toggleSelectAllVisible: (table: string, rowIds: string[]) => void;

  /** Replace the current visible-row selection with an explicit set. */
  setSelectedRows: (table: string, rowIds: string[]) => void;

  /** Replace the hidden columns for a table. */
  setHiddenColumns: (table: string, columnNames: string[]) => void;

  /** Clear selection for one table or the current table. */
  clearSelection: (table?: string) => void;

  // ── Inline edit-mode actions (TASK-092) ──

  /** Enter edit mode for a single cell on the given table. */
  startCellEdit: (table: string, rowId: string, columnName: string) => void;

  /** Exit edit mode without committing. */
  cancelCellEdit: (table?: string) => void;

  /**
   * Editor finished and produced a value. Exits edit mode and clears any
   * pending error on that cell. Synchronous local-only seam used by tests
   * and by `commitCellEdit` itself; UI callers should prefer
   * `commitCellEdit` (TASK-093) which performs the PATCH round-trip.
   */
  cellEditCommitted: (
    table: string,
    rowId: string,
    columnName: string,
    nextValue: unknown,
  ) => void;

  /**
   * Optimistically apply an inline cell edit and PATCH the row.
   * - No-op when `nextValue === prevValue`
   * - Applies `nextValue` immediately, exits edit mode, marks the cell pending
   * - On 2xx, reconciles the row from the response
   * - On 4xx with field errors, restores `prevValue` and writes per-field
   *   messages into `cellErrors`
   * - On 5xx / network failure, restores `prevValue` and surfaces a generic
   *   error on the edited cell
   */
  commitCellEdit: (
    table: string,
    rowId: string,
    columnName: string,
    nextValue: unknown,
  ) => Promise<void>;

  /**
   * Set or clear a per-cell error message (typically from a backend
   * validation response). Pass null to clear.
   */
  setCellError: (
    table: string,
    rowId: string,
    columnName: string,
    message: string | null,
  ) => void;

  /** Open a related-records view backed by the current sub-resource API. */
  openRelatedContext: (
    table: string,
    parentTable: string,
    parentRowId: string,
    accessor: string
  ) => void;

  /** Open related records as a normal filtered table view. */
  openFilteredRelatedContext: (
    table: string,
    parentTable: string,
    parentRowIds: string[],
    accessor: string,
    filterColumn: string,
    filters: FilterClause[]
  ) => void;

  /** Clear the current table back to its normal base-table browsing state. */
  clearContext: (table?: string, nextQuery?: Partial<ExplorerQuery>) => void;

  /** Insert a local blank row and enter edit mode on its first writable cell. */
  addBlankRow: (table: string) => void;

  /** Delete a row with optimistic rollback; confirmation lives in the UI layer. */
  deleteRow: (table: string, rowId: string) => Promise<boolean>;

  /** Duplicate a row server-side, then insert it locally after the source row. */
  duplicateRow: (table: string, rowId: string) => Promise<void>;

  /** Push a delete action onto the table's session undo stack. */
  pushUndoDeleteEntry: (table: string, rows: UndoDeletedRowSnapshot[]) => void;

  /** Consume and execute the last undoable action for a table. */
  undoLast: (
    table: string,
  ) => Promise<{ successCount: number; failureCount: number; total: number }>;

  // ── Saved-views actions ──

  /** Load saved views from the backend for a table. Called on mount. */
  loadSavedViews: (tableId: string) => Promise<void>;

  /**
   * Switch to a saved view (by id) or to "All Records" (null).
   * Hydrates config, resets runtime state, and fetches rows.
   */
  switchView: (viewId: number | null) => void;

  /** Save the current config as a new named view and make it active. */
  saveCurrentAsView: (name: string, type: ExplorerViewType, isShared: boolean) => Promise<void>;

  /** Push the current config back to the active saved view (used by auto-save). */
  updateActiveViewConfig: () => Promise<void>;

  /** Push the config back to a specific table's active saved view (used by auto-save). */
  updateViewConfigFor: (tableId: string, viewId: number) => Promise<void>;

  /** Rename a saved view (owner only). */
  renameView: (id: number, name: string) => Promise<void>;

  /** Toggle the is_shared flag on a saved view (owner only). */
  toggleViewShared: (id: number) => Promise<void>;

  /** Delete a saved view. Falls back to "All Records" if it was active. */
  deleteView: (id: number) => Promise<void>;

  /**
   * Change a saved view's type (table ↔ kanban). Updates the config shape
   * in-place and PUTs the new config to the backend.
   */
  changeViewType: (id: number, type: ExplorerViewType) => Promise<void>;

  /**
   * Create an owned copy of a shared view (typically one owned by another
   * user). The duplicate is private (is_shared: false), inherits type and
   * config, and becomes the active view. The caller supplies the new name.
   */
  duplicateView: (id: number, name: string) => Promise<void>;

  /**
   * Mark a saved view as the default for the active table (or pass null
   * to clear). Persisted per-user in the backend so it works across browsers.
   */
  setDefaultView: (id: number | null) => Promise<void>;

  /** Id of the default view per table (null = "All Records"). */
  defaultViewIdByTable: Record<string, number | null>;

  /** Enable or disable auto-save for the active table's active view. */
  setAutoSave: (enabled: boolean) => void;

  /**
   * Update the kanban-specific config (grouping column and secondary card
   * fields) on the current active view. No-op if the active view config is
   * not a kanban view. Triggers auto-save if enabled.
   */
  setKanbanConfig: (patch: { kanbanColumn?: string | null; cardFieldNames?: string[]; hiddenLaneValues?: string[] }) => void;

  // ── Record editor (TASK-098, TASK-099) ──

  /**
   * Open the full-screen record editor with a fresh stack containing the given
   * entry. Fetches the row from `GET /data/:table/:id` and writes it into the
   * editor's single-record cache, reconciling the current table view only when
   * that row is already part of the visible result set.
   */
  openRecordEditor: (params: { table: string; rowId: string | number }) => Promise<void>;

  /**
   * Push another record onto the in-modal navigation stack (TASK-099).
   * Typically invoked from a clicked FK value. Fetches the row; on error the
   * stack entry enters an error state without affecting earlier entries. No-op
   * when the editor is closed.
   */
  pushRecordEditor: (params: { table: string; rowId: string | number }) => Promise<void>;

  /**
   * Drop the top entry from the stack. If the stack becomes empty, closes the
   * editor entirely (TASK-099).
   */
  popRecordEditor: () => void;

  /** Close the record editor and clear the entire stack. */
  closeRecordEditor: () => void;

  /** Re-run the fetch for the top stack entry after a load error. */
  retryRecordEditorLoad: () => Promise<void>;

  /** Full reset (e.g. on logout). */
  reset: () => void;
};

// ── Helpers ───────────────────────────────────────────────────────────

function getView(
  views: Record<string, TableViewState>,
  table: string | null
): TableViewState | null {
  if (!table) return null;
  return views[table] ?? null;
}

/**
 * Returns true if the current authenticated user owns the given view.
 * Used to gate destructive / mutating actions so users can't rename or
 * delete someone else's shared view.
 */
function currentUserOwns(view: SavedView | undefined): boolean {
  if (!view) return false;
  const user = useAuthStore.getState().user;
  if (!user) return false;
  return view.owner_user_id === user.id;
}

function getTableModel(tableName: string): ModelTable | null {
  return (
    useSchemaStore.getState().snapshot?.tables.find((table) => table.name === tableName) ??
    null
  );
}

function isWritableColumn(column: ModelColumn): boolean {
  return !column.primaryKey && column.type !== "computed";
}

function isTemporalColumn(column: ModelColumn): boolean {
  return column.type === "date" || column.type === "datetime";
}

function isServerManagedColumn(column: ModelColumn): boolean {
  return Boolean(column.primaryKey) || (isTemporalColumn(column) && column.default !== undefined);
}

function getWritableColumns(table: ModelTable): ModelColumn[] {
  return table.columns.filter(isWritableColumn);
}

function buildLocalBlankRow(table: ModelTable, localRowId: string): Record<string, unknown> {
  const row: Record<string, unknown> = { id: localRowId };
  for (const column of table.columns) {
    if (column.name === "id") continue;
    row[column.name] = null;
  }
  return row;
}

function buildCreatePayload(
  table: ModelTable,
  row: Record<string, unknown>,
  nextColumnName: string,
  nextValue: unknown,
): Record<string, unknown> {
  const payload: Record<string, unknown> = {};
  for (const column of getWritableColumns(table)) {
    const value = column.name === nextColumnName ? nextValue : row[column.name];
    if (value === undefined || value === null || value === "") continue;
    payload[column.name] = value;
  }
  return payload;
}

function buildDuplicatePayload(
  table: ModelTable,
  row: Record<string, unknown>,
): Record<string, unknown> {
  const payload: Record<string, unknown> = {};
  for (const column of getWritableColumns(table)) {
    if (isServerManagedColumn(column)) continue;
    if (column.name === "id") continue;
    const value = row[column.name];
    if (value === undefined) continue;
    payload[column.name] = value;
  }
  return payload;
}

function adjustMetaTotal(meta: ListMeta | null, perPage: number, delta: number): ListMeta | null {
  if (!meta) return meta;
  const total = Math.max(0, meta.total + delta);
  return {
    ...meta,
    total,
    total_pages: Math.max(1, Math.ceil(total / Math.max(1, perPage))),
  };
}

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

function trimUndoStack(stack: UndoEntry[]): UndoEntry[] {
  return stack.length > UNDO_STACK_LIMIT
    ? stack.slice(stack.length - UNDO_STACK_LIMIT)
    : stack;
}

function dropPendingNewRows(
  view: TableViewState,
  options?: { preserveRowIds?: string[] },
): TableViewState {
  if (view.pendingNewRowIds.length === 0) return view;

  const preserve = new Set(options?.preserveRowIds ?? []);
  const toDrop = view.pendingNewRowIds.filter((rowId) => !preserve.has(rowId));
  if (toDrop.length === 0) return view;

  const dropSet = new Set(toDrop);
  const nextCellErrors = { ...view.cellErrors };
  const nextRowErrors = { ...view.rowErrors };
  for (const rowId of toDrop) {
    delete nextCellErrors[rowId];
    delete nextRowErrors[rowId];
  }

  return {
    ...view,
    rows: view.rows.filter((row) => !dropSet.has(String(row.id ?? ""))),
    meta: adjustMetaTotal(view.meta, view.perPage, -toDrop.length),
    pendingNewRowIds: view.pendingNewRowIds.filter((rowId) => preserve.has(rowId)),
    pendingCellSaves: view.pendingCellSaves.filter((key) => !dropSet.has(key.split(":")[0] ?? "")),
    selectedRowIds: view.selectedRowIds.filter((rowId) => !dropSet.has(rowId)),
    editingCell:
      view.editingCell && dropSet.has(view.editingCell.rowId) ? null : view.editingCell,
    cellErrors: nextCellErrors,
    rowErrors: nextRowErrors,
  };
}

function upsertRecordCacheEntry(
  cacheByTable: RecordCacheByTable,
  table: string,
  rowId: string,
  row: Record<string, unknown>,
): RecordCacheByTable {
  return {
    ...cacheByTable,
    [table]: {
      ...(cacheByTable[table] ?? {}),
      [rowId]: { ...row },
    },
  };
}

function removeRecordCacheEntry(
  cacheByTable: RecordCacheByTable,
  table: string,
  rowId: string,
): RecordCacheByTable {
  const tableCache = cacheByTable[table];
  if (!tableCache || tableCache[rowId] === undefined) return cacheByTable;

  const nextTableCache = { ...tableCache };
  delete nextTableCache[rowId];

  if (Object.keys(nextTableCache).length === 0) {
    const nextCache = { ...cacheByTable };
    delete nextCache[table];
    return nextCache;
  }

  return {
    ...cacheByTable,
    [table]: nextTableCache,
  };
}

/**
 * Drive the async fetch for a single record-editor stack entry (TASK-099). On
 * success the record is written into the editor cache, and into `views[table]`
 * only if that row is already present in the current result set. On failure
 * only the target entry flips to "error" — earlier entries in the stack are
 * untouched.
 *
 * `expectedIndex` lets us tolerate out-of-order async completions: if the
 * user pops before the fetch resolves, or pushes a new entry above, we bail
 * out instead of clobbering unrelated state.
 */
async function loadRecordEditorEntry(
  set: (partial: Partial<ExplorerStoreState> | ((state: ExplorerStoreState) => Partial<ExplorerStoreState> | ExplorerStoreState)) => void,
  _get: () => ExplorerStoreState,
  table: string,
  rowIdString: string,
  expectedIndex: number
): Promise<void> {
  try {
    const fetched = await fetchRow(table, rowIdString);
    set((state) => {
      const editor = state.recordEditor;
      if (!editor) return state;
      const target = editor.stack[expectedIndex];
      if (!target || target.table !== table || target.rowId !== rowIdString) return state;

      const nextStack = editor.stack.slice();
      nextStack[expectedIndex] = {
        ...target,
        status: "ready",
        row: fetched,
        error: null,
      };

      const view = state.views[table];
      const nextRecordCache = upsertRecordCacheEntry(
        state.recordCacheByTable,
        table,
        rowIdString,
        fetched,
      );
      const updatedViews = view
        ? (() => {
            const idx = view.rows.findIndex(
              (row) => String(row.id ?? "") === rowIdString,
            );
            if (idx === -1) return state.views;
            const nextRows = view.rows.slice();
            nextRows[idx] = { ...nextRows[idx]!, ...fetched };
            return {
              ...state.views,
              [table]: { ...view, rows: nextRows },
            };
          })()
        : state.views;

      return {
        views: updatedViews,
        recordCacheByTable: nextRecordCache,
        recordEditor: { ...editor, stack: nextStack },
      };
    });
  } catch (cause) {
    const message =
      cause instanceof ApiClientError
        ? cause.message
        : cause instanceof Error
          ? cause.message
          : "Unable to load record.";
    set((state) => {
      const editor = state.recordEditor;
      if (!editor) return state;
      const target = editor.stack[expectedIndex];
      if (!target || target.table !== table || target.rowId !== rowIdString) return state;
      const nextStack = editor.stack.slice();
      nextStack[expectedIndex] = { ...target, status: "error", error: message };
      return { recordEditor: { ...editor, stack: nextStack } };
    });
  }
}

// ── Store ─────────────────────────────────────────────────────────────

const initialState = {
  activeTable: null as string | null,
  views: {} as Record<string, TableViewState>,
  recordCacheByTable: {} as RecordCacheByTable,
  defaultViewIdByTable: {} as Record<string, number | null>,
  undoStackByTable: {} as Record<string, UndoEntry[]>,
  recordEditor: null as RecordEditorState | null,
};

export const useExplorerStore = create<ExplorerStoreState>((set, get) => ({
  ...initialState,

  setActiveTable: (table) => {
    set((state) => {
      const views = { ...state.views };
      if (state.activeTable && state.activeTable !== table && views[state.activeTable]) {
        views[state.activeTable] = dropPendingNewRows(views[state.activeTable]!);
      }
      if (state.activeTable === table && views[table]) {
        return { activeTable: table, views };
      }
      if (!views[table]) {
        views[table] = freshTableView();
      }
      return { activeTable: table, views };
    });
  },

  fetchRows: async () => {
    const { activeTable, views } = get();
    if (!activeTable) return;

    const view = dropPendingNewRows(views[activeTable] ?? freshTableView());
    const query = getExplorerQueryForView(view);

    // Mark loading
    set((state) => ({
      views: {
        ...state.views,
        [activeTable]: { ...view, status: "loading", error: null }
      }
    }));

    try {
      const result =
        view.source.kind === "related"
          ? await fetchRelatedRows(
              view.source.parentTable,
              view.source.parentRowId,
              view.source.accessor,
              query
            )
          : await fetchTableRows(activeTable, query);
      set((state) => ({
        views: {
          ...state.views,
          [activeTable]: {
            ...state.views[activeTable]!,
            status: "ready",
            rows: result.data,
            meta: result.meta,
            error: null
          }
        }
      }));
    } catch (cause) {
      const message =
        cause instanceof ApiClientError
          ? cause.message
          : "Unable to load rows.";
      set((state) => ({
        views: {
          ...state.views,
          [activeTable]: {
            ...state.views[activeTable]!,
            status: "error",
            error: message
          }
        }
      }));
    }
  },

  setQuery: (patch) => {
    const { activeTable, views, fetchRows } = get();
    if (!activeTable) return;
    const view = dropPendingNewRows(views[activeTable] ?? freshTableView());
    set((state) => ({
      views: {
        ...state.views,
        [activeTable]: {
          ...view,
          config: patchExplorerViewConfig(view.config, patch),
          page: patch.page ?? 1,
          perPage: patch.perPage ?? view.perPage,
          selectedRowIds: []
        }
      }
    }));
    void fetchRows();
  },

  applyRouteQuery: (table, patch) => {
    const { fetchRows } = get();
    set((state) => {
      const currentView = dropPendingNewRows(state.views[table] ?? freshTableView());
      return {
        activeTable: table,
        views: {
          ...state.views,
          [table]: {
            ...currentView,
            status: "idle",
            config: patchExplorerViewConfig(DEFAULT_EXPLORER_VIEW_CONFIG, patch),
            activeViewId: null,
            page: patch.page ?? 1,
            perPage: patch.perPage ?? DEFAULT_QUERY.perPage,
            rows: [],
            meta: null,
            error: null,
            source: { kind: "table" },
            selectedRowIds: [],
            context: null,
          },
        },
      };
    });
    void fetchRows();
  },

  setPage: (page) => {
    const { activeTable, views, fetchRows } = get();
    if (!activeTable) return;
    const view = dropPendingNewRows(views[activeTable] ?? freshTableView());
    set((state) => ({
      views: {
        ...state.views,
        [activeTable]: {
          ...view,
          page,
          selectedRowIds: []
        }
      }
    }));
    void fetchRows();
  },

  setSort: (sort) => {
    get().setQuery({ sort, page: 1 });
    scheduleAutoSave();
  },

  setSearch: (search) => {
    get().setQuery({ search, page: 1 });
    scheduleAutoSave();
  },

  setFilters: (filters) => {
    get().setQuery({ filters, page: 1 });
    scheduleAutoSave();
  },

  openRecordContext: (table, rowId, context) => {
    set((state) => ({
      activeTable: table,
      views: {
        ...state.views,
        [table]: {
          ...dropPendingNewRows(state.views[table] ?? freshTableView()),
          status: "idle",
          rows: [],
          meta: null,
          error: null,
          source: { kind: "table" },
          selectedRowIds: [],
          context: {
            kind: "reference",
            rowId,
            sourceTable: context.sourceTable,
            sourceRowId: context.sourceRowId,
            sourceColumn: context.sourceColumn
          },
          config: patchExplorerViewConfig(
            state.views[table]?.config ?? DEFAULT_EXPLORER_VIEW_CONFIG,
            {
              search: DEFAULT_QUERY.search,
              sort: DEFAULT_QUERY.sort,
              filters: [{ column: "id", op: "eq", value: String(rowId) }]
            }
          ),
          page: DEFAULT_QUERY.page,
          perPage: DEFAULT_QUERY.perPage
        }
      }
    }));
  },

  toggleRowSelection: (table, rowId) => {
    set((state) => {
      const view = state.views[table] ?? freshTableView();
      const selectedRowIds = view.selectedRowIds.includes(rowId)
        ? view.selectedRowIds.filter((value) => value !== rowId)
        : [...view.selectedRowIds, rowId];

      return {
        activeTable: table,
        views: {
          ...state.views,
          [table]: {
            ...view,
            selectedRowIds
          }
        }
      };
    });
  },

  toggleSelectAllVisible: (table, rowIds) => {
    set((state) => {
      const view = state.views[table] ?? freshTableView();
      const normalizedRowIds = Array.from(new Set(rowIds.filter(Boolean)));
      const allSelected =
        normalizedRowIds.length > 0 &&
        normalizedRowIds.every((rowId) => view.selectedRowIds.includes(rowId));

      return {
        activeTable: table,
        views: {
          ...state.views,
          [table]: {
            ...view,
            selectedRowIds: allSelected ? [] : normalizedRowIds
          }
        }
      };
    });
  },

  setSelectedRows: (table, rowIds) => {
    set((state) => {
      const view = state.views[table] ?? freshTableView();

      return {
        activeTable: table,
        views: {
          ...state.views,
          [table]: {
            ...view,
            selectedRowIds: Array.from(new Set(rowIds.filter(Boolean)))
          }
        }
      };
    });
  },

  setHiddenColumns: (table, columnNames) => {
    set((state) => {
      const view = state.views[table] ?? freshTableView();

      return {
        activeTable: table,
        views: {
          ...state.views,
          [table]: {
            ...view,
            config: patchExplorerViewConfig(view.config, {
              hiddenColumnNames: Array.from(new Set(columnNames.filter(Boolean)))
            })
          }
        }
      };
    });
    // Only schedule auto-save if this is the active table
    if (get().activeTable === table) scheduleAutoSave();
  },

  clearSelection: (table) => {
    const targetTable = table ?? get().activeTable;
    if (!targetTable) return;

    set((state) => {
      const view = state.views[targetTable] ?? freshTableView();
      if (view.selectedRowIds.length === 0) return state;

      return {
        activeTable: targetTable,
        views: {
          ...state.views,
          [targetTable]: {
            ...view,
            selectedRowIds: []
          }
        }
      };
    });
  },

  startCellEdit: (table, rowId, columnName) => {
    set((state) => {
      const view = state.views[table];
      if (!view) return state;
      const nextView =
        view.editingCell && view.editingCell.rowId !== rowId
          ? dropPendingNewRows(view, { preserveRowIds: [rowId] })
          : view;
      return {
        views: {
          ...state.views,
          [table]: { ...nextView, editingCell: { rowId, columnName } }
        }
      };
    });
  },

  cancelCellEdit: (table) => {
    const targetTable = table ?? get().activeTable;
    if (!targetTable) return;
    set((state) => {
      const view = state.views[targetTable];
      if (!view || view.editingCell === null) return state;
      const nextView = view.pendingNewRowIds.includes(view.editingCell.rowId)
        ? dropPendingNewRows(view)
        : view;
      return {
        views: {
          ...state.views,
          [targetTable]: { ...nextView, editingCell: null }
        }
      };
    });
  },

  cellEditCommitted: (table, rowId, columnName, _nextValue) => {
    set((state) => {
      const view = state.views[table];
      if (!view) return state;
      const rowErrors = view.cellErrors[rowId];
      let nextErrors = view.cellErrors;
      if (rowErrors && rowErrors[columnName] !== undefined) {
        const { [columnName]: _removed, ...remainingForRow } = rowErrors;
        nextErrors = { ...view.cellErrors };
        if (Object.keys(remainingForRow).length === 0) {
          delete nextErrors[rowId];
        } else {
          nextErrors[rowId] = remainingForRow;
        }
      }
      return {
        views: {
          ...state.views,
          [table]: { ...view, editingCell: null, cellErrors: nextErrors }
        }
      };
    });
  },

  setCellError: (table, rowId, columnName, message) => {
    set((state) => {
      const view = state.views[table];
      if (!view) return state;
      const nextErrors = { ...view.cellErrors };
      const rowErrors = { ...(nextErrors[rowId] ?? {}) };
      if (message === null) {
        delete rowErrors[columnName];
        if (Object.keys(rowErrors).length === 0) {
          delete nextErrors[rowId];
        } else {
          nextErrors[rowId] = rowErrors;
        }
      } else {
        rowErrors[columnName] = message;
        nextErrors[rowId] = rowErrors;
      }
      return {
        views: {
          ...state.views,
          [table]: { ...view, cellErrors: nextErrors }
        }
      };
    });
  },

  commitCellEdit: async (table, rowId, columnName, nextValue) => {
    const stateBefore = get();
    const view = stateBefore.views[table];
    if (!view) return;
    const rowIndex = view.rows.findIndex((r) => String(r.id ?? "") === rowId);
    const cachedRow = stateBefore.recordCacheByTable[table]?.[rowId] ?? null;
    const row = rowIndex === -1 ? cachedRow : view.rows[rowIndex]!;
    if (!row) return;
    const prevValue = row[columnName];
    const isPendingNewRow = view.pendingNewRowIds.includes(rowId);
    const tableModel = isPendingNewRow ? getTableModel(table) : null;
    if (isPendingNewRow && !tableModel) return;
    const createPayload = isPendingNewRow
      ? buildCreatePayload(tableModel!, row, columnName, nextValue)
      : null;

    if (Object.is(prevValue, nextValue)) {
      // No-op: just exit edit mode without hitting the API.
      get().cellEditCommitted(table, rowId, columnName, nextValue);
      return;
    }

    if (isPendingNewRow && createPayload && Object.keys(createPayload).length === 0) {
      set((s) => {
        const v = s.views[table];
        if (!v) return s;
        const idx = v.rows.findIndex((candidate) => String(candidate.id ?? "") === rowId);
        const nextRows = v.rows.slice();
        if (idx !== -1) {
          nextRows[idx] = { ...nextRows[idx]!, [columnName]: nextValue };
        }
        const cacheRow =
          idx !== -1 ? nextRows[idx]! : (s.recordCacheByTable[table]?.[rowId] ?? null);
        const nextCache =
          cacheRow === null
            ? s.recordCacheByTable
            : upsertRecordCacheEntry(s.recordCacheByTable, table, rowId, cacheRow);
        return {
          views: {
            ...s.views,
            [table]: {
              ...v,
              rows: nextRows,
              editingCell: null,
              cellErrors: clearCellError(v.cellErrors, rowId, columnName),
            },
          },
          recordCacheByTable: nextCache,
        };
      });
      return;
    }

    const pendingKey = `${rowId}:${columnName}`;

    // Optimistic apply: write the new value into the row, exit edit mode,
    // clear any prior error on this cell, and mark the cell as pending.
    set((s) => {
      const v = s.views[table];
      if (!v) return s;
      const idx = v.rows.findIndex((candidate) => String(candidate.id ?? "") === rowId);
      const nextRows = v.rows.slice();
      const currentCacheRow = s.recordCacheByTable[table]?.[rowId] ?? null;
      const baseRow = idx !== -1 ? nextRows[idx]! : currentCacheRow;
      if (!baseRow) return s;
      const nextRow = { ...baseRow, [columnName]: nextValue };
      if (idx !== -1) {
        nextRows[idx] = nextRow;
      }
      const nextErrors = clearCellError(v.cellErrors, rowId, columnName);
      return {
        views: {
          ...s.views,
          [table]: {
            ...v,
            rows: nextRows,
            editingCell: null,
            cellErrors: nextErrors,
            pendingCellSaves: v.pendingCellSaves.includes(pendingKey)
              ? v.pendingCellSaves
              : [...v.pendingCellSaves, pendingKey],
          },
        },
        recordCacheByTable: upsertRecordCacheEntry(
          s.recordCacheByTable,
          table,
          rowId,
          nextRow,
        ),
      };
    });

    try {
      const updated = isPendingNewRow
        ? await createRow(table, createPayload ?? {})
        : await patchRow(table, rowId, { [columnName]: nextValue });
      set((s) => {
        const v = s.views[table];
        if (!v) return s;
        const idx = v.rows.findIndex((r) => String(r.id ?? "") === rowId);
        const nextRows = v.rows.slice();
        if (idx !== -1) {
          if (isPendingNewRow) {
            nextRows[idx] = updated;
          } else {
            // Reconcile from server response so columns like updated_at are fresh.
            nextRows[idx] = { ...nextRows[idx]!, ...updated };
          }
        }
        const nextCellErrors = { ...v.cellErrors };
        const nextRowErrors = { ...v.rowErrors };
        if (isPendingNewRow) {
          delete nextCellErrors[rowId];
          delete nextRowErrors[rowId];
        }
        const updatedRowId = String(updated.id ?? rowId);
        let nextRecordCache = removeRecordCacheEntry(s.recordCacheByTable, table, rowId);
        nextRecordCache = upsertRecordCacheEntry(
          nextRecordCache,
          table,
          updatedRowId,
          updated,
        );
        return {
          views: {
            ...s.views,
            [table]: {
              ...v,
              rows: nextRows,
              pendingCellSaves: v.pendingCellSaves.filter((k) => k !== pendingKey),
              pendingNewRowIds: isPendingNewRow
                ? v.pendingNewRowIds.filter((id) => id !== rowId)
                : v.pendingNewRowIds,
              editingCell:
                isPendingNewRow &&
                v.editingCell !== null &&
                v.editingCell.rowId === rowId
                  ? { ...v.editingCell, rowId: updatedRowId }
                  : v.editingCell,
              cellErrors: nextCellErrors,
              rowErrors: nextRowErrors,
            },
          },
          recordCacheByTable: nextRecordCache,
        };
      });
    } catch (err) {
      const fieldErrors = extractFieldErrors(err);
      const fallback = err instanceof Error ? err.message : "Failed to save.";

      set((s) => {
        const v = s.views[table];
        if (!v) return s;
        const idx = v.rows.findIndex((r) => String(r.id ?? "") === rowId);
        const nextRows = v.rows.slice();
        if (idx !== -1) {
          nextRows[idx] = { ...nextRows[idx]!, [columnName]: prevValue };
        }
        const currentCacheRow = s.recordCacheByTable[table]?.[rowId] ?? null;
        let nextRecordCache = s.recordCacheByTable;
        if (currentCacheRow) {
          nextRecordCache = upsertRecordCacheEntry(
            s.recordCacheByTable,
            table,
            rowId,
            { ...currentCacheRow, [columnName]: prevValue },
          );
        }
        let nextErrors = v.cellErrors;
        if (fieldErrors && fieldErrors.length > 0) {
          for (const fe of fieldErrors) {
            nextErrors = setCellErrorIn(nextErrors, rowId, fe.field, fe.message);
          }
          // If the backend didn't flag the column we just edited but the save
          // still failed, surface the generic message there too so the cell
          // shows its red underline.
          if (!fieldErrors.some((fe) => fe.field === columnName)) {
            nextErrors = setCellErrorIn(nextErrors, rowId, columnName, fallback);
          }
        } else {
          nextErrors = setCellErrorIn(nextErrors, rowId, columnName, fallback);
        }
        return {
          views: {
            ...s.views,
            [table]: {
              ...v,
              rows: nextRows,
              cellErrors: nextErrors,
              pendingCellSaves: v.pendingCellSaves.filter((k) => k !== pendingKey),
            },
          },
          recordCacheByTable: nextRecordCache,
        };
      });
    }
  },

  addBlankRow: (table) => {
    const tableModel = getTableModel(table);
    if (!tableModel) return;
    const firstWritableColumn = getWritableColumns(tableModel)[0];
    if (!firstWritableColumn) return;

    const localRowId = `__new__:${Date.now()}:${Math.random().toString(36).slice(2, 8)}`;
    const blankRow = buildLocalBlankRow(tableModel, localRowId);

    set((state) => {
      const currentView = state.views[table] ?? freshTableView();
      const view = dropPendingNewRows(currentView);
      return {
        activeTable: table,
        views: {
          ...state.views,
          [table]: {
            ...view,
            rows: [...view.rows, blankRow],
            meta: adjustMetaTotal(view.meta, view.perPage, 1),
            selectedRowIds: [],
            editingCell: { rowId: localRowId, columnName: firstWritableColumn.name },
            pendingNewRowIds: [...view.pendingNewRowIds, localRowId],
          },
        },
      };
    });
  },

  deleteRow: async (table, rowId) => {
    const stateBefore = get();
    const view = stateBefore.views[table];
    if (!view) return false;
    const rowIndex = view.rows.findIndex((row) => String(row.id ?? "") === rowId);
    if (rowIndex === -1) return false;
    const row = view.rows[rowIndex]!;
    const wasSelected = view.selectedRowIds.includes(rowId);

    if (view.pendingNewRowIds.includes(rowId)) {
      set((state) => {
        const currentView = state.views[table];
        if (!currentView) return state;
        const nextCellErrors = { ...currentView.cellErrors };
        const nextRowErrors = { ...currentView.rowErrors };
        delete nextCellErrors[rowId];
        delete nextRowErrors[rowId];
        return {
          views: {
            ...state.views,
            [table]: {
              ...currentView,
              rows: currentView.rows.filter((candidate) => String(candidate.id ?? "") !== rowId),
              meta: adjustMetaTotal(currentView.meta, currentView.perPage, -1),
              pendingNewRowIds: currentView.pendingNewRowIds.filter((id) => id !== rowId),
              pendingCellSaves: currentView.pendingCellSaves.filter(
                (key) => (key.split(":")[0] ?? "") !== rowId,
              ),
              selectedRowIds: currentView.selectedRowIds.filter((id) => id !== rowId),
              editingCell:
                currentView.editingCell?.rowId === rowId ? null : currentView.editingCell,
              cellErrors: nextCellErrors,
              rowErrors: nextRowErrors,
            },
          },
          recordCacheByTable: removeRecordCacheEntry(state.recordCacheByTable, table, rowId),
        };
      });
      return true;
    }

    set((state) => {
      const currentView = state.views[table];
      if (!currentView) return state;
      const nextRowErrors = { ...currentView.rowErrors };
      delete nextRowErrors[rowId];
      return {
        views: {
          ...state.views,
          [table]: {
            ...currentView,
            rows: currentView.rows.filter((candidate) => String(candidate.id ?? "") !== rowId),
            meta: adjustMetaTotal(currentView.meta, currentView.perPage, -1),
            selectedRowIds: currentView.selectedRowIds.filter((id) => id !== rowId),
            editingCell:
              currentView.editingCell?.rowId === rowId ? null : currentView.editingCell,
            rowErrors: nextRowErrors,
          },
        },
        recordCacheByTable: removeRecordCacheEntry(state.recordCacheByTable, table, rowId),
      };
    });

    try {
      await deleteRowApi(table, rowId);
      return true;
    } catch (err) {
      const message = err instanceof Error ? err.message : "Failed to delete row.";
      set((state) => {
        const currentView = state.views[table];
        if (!currentView) return state;
        const nextRows = currentView.rows.slice();
        nextRows.splice(rowIndex, 0, row);
        return {
          views: {
            ...state.views,
            [table]: {
              ...currentView,
              rows: nextRows,
              meta: adjustMetaTotal(currentView.meta, currentView.perPage, 1),
              selectedRowIds: wasSelected
                ? Array.from(new Set([...currentView.selectedRowIds, rowId]))
                : currentView.selectedRowIds,
              rowErrors: {
                ...currentView.rowErrors,
                [rowId]: message,
              },
            },
          },
          recordCacheByTable: upsertRecordCacheEntry(
            state.recordCacheByTable,
            table,
            rowId,
            row,
          ),
        };
      });
      return false;
    }
  },

  duplicateRow: async (table, rowId) => {
    const view = get().views[table];
    const tableModel = getTableModel(table);
    if (!view || !tableModel) return;
    const rowIndex = view.rows.findIndex((row) => String(row.id ?? "") === rowId);
    if (rowIndex === -1) return;
    const row = view.rows[rowIndex]!;

    try {
      const duplicated = await createRow(table, buildDuplicatePayload(tableModel, row));
      set((state) => {
        const currentView = state.views[table];
        if (!currentView) return state;
        const insertAfter = currentView.rows.findIndex(
          (candidate) => String(candidate.id ?? "") === rowId,
        );
        if (insertAfter === -1) return state;
        const nextRows = currentView.rows.slice();
        nextRows.splice(insertAfter + 1, 0, duplicated);
        const nextRowErrors = { ...currentView.rowErrors };
        delete nextRowErrors[rowId];
        return {
          views: {
            ...state.views,
            [table]: {
              ...currentView,
              rows: nextRows,
              meta: adjustMetaTotal(currentView.meta, currentView.perPage, 1),
              rowErrors: nextRowErrors,
            },
          },
        };
      });
    } catch (err) {
      const message = err instanceof Error ? err.message : "Failed to duplicate row.";
      set((state) => {
        const currentView = state.views[table];
        if (!currentView) return state;
        return {
          views: {
            ...state.views,
            [table]: {
              ...currentView,
              rowErrors: {
                ...currentView.rowErrors,
                [rowId]: message,
              },
            },
          },
        };
      });
    }
  },

  pushUndoDeleteEntry: (table, rows) => {
    const snapshots = rows
      .filter((item) => item.row)
      .map((item) => ({ index: item.index, row: { ...item.row } }));
    if (snapshots.length === 0) return;

    set((state) => {
      const current = state.undoStackByTable[table] ?? [];
      const nextEntry: UndoEntry = {
        kind: "delete",
        table,
        rows: snapshots,
        at: Date.now(),
      };
      return {
        undoStackByTable: {
          ...state.undoStackByTable,
          [table]: trimUndoStack([...current, nextEntry]),
        },
      };
    });
  },

  undoLast: async (table) => {
    const stack = get().undoStackByTable[table] ?? [];
    const entry = stack[stack.length - 1];
    if (!entry) {
      return { successCount: 0, failureCount: 0, total: 0 };
    }

    set((state) => ({
      undoStackByTable: {
        ...state.undoStackByTable,
        [table]: (state.undoStackByTable[table] ?? []).slice(0, -1),
      },
    }));

    const tableModel = getTableModel(table);
    if (!tableModel) {
      return {
        successCount: 0,
        failureCount: entry.rows.length,
        total: entry.rows.length,
      };
    }

    const results = await runInConcurrentBatches(
      entry.rows,
      UNDO_RESTORE_CONCURRENCY,
      async (snapshot) => {
        try {
          const recreated = await createRow(
            table,
            buildDuplicatePayload(tableModel, snapshot.row),
          );
          return { ok: true as const, snapshot, recreated };
        } catch (error) {
          return { ok: false as const, snapshot, error };
        }
      },
    );

    const successfulRestores = results
      .filter((result): result is { ok: true; snapshot: UndoDeletedRowSnapshot; recreated: Record<string, unknown> } => result.ok)
      .sort((left, right) => left.snapshot.index - right.snapshot.index);

    if (successfulRestores.length > 0) {
      set((state) => {
        const currentView = state.views[table];
        if (!currentView) return state;

        const nextRows = currentView.rows.slice();
        let inserted = 0;
        for (const restored of successfulRestores) {
          const insertAt = Math.min(restored.snapshot.index + inserted, nextRows.length);
          nextRows.splice(insertAt, 0, restored.recreated);
          inserted += 1;
        }

        const nextRowErrors = { ...currentView.rowErrors };
        for (const restored of successfulRestores) {
          delete nextRowErrors[String(restored.snapshot.row.id ?? "")];
        }

        return {
          views: {
            ...state.views,
            [table]: {
              ...currentView,
              rows: nextRows,
              meta: adjustMetaTotal(
                currentView.meta,
                currentView.perPage,
                successfulRestores.length,
              ),
              rowErrors: nextRowErrors,
            },
          },
        };
      });
    }

    return {
      successCount: successfulRestores.length,
      failureCount: entry.rows.length - successfulRestores.length,
      total: entry.rows.length,
    };
  },

  openRelatedContext: (table, parentTable, parentRowId, accessor) => {
    set((state) => ({
      activeTable: table,
      views: {
        ...state.views,
        [table]: {
          ...dropPendingNewRows(state.views[table] ?? freshTableView()),
          status: "idle",
          rows: [],
          meta: null,
          error: null,
          source: {
            kind: "related",
            parentTable,
            parentRowId,
            accessor,
          },
          selectedRowIds: [],
          context: {
            kind: "related",
            parentTable,
            parentRowIds: [parentRowId],
            accessor,
            via: "endpoint"
          },
          config: patchExplorerViewConfig(
            state.views[table]?.config ?? DEFAULT_EXPLORER_VIEW_CONFIG,
            {
              search: DEFAULT_QUERY.search,
              sort: DEFAULT_QUERY.sort,
              filters: DEFAULT_QUERY.filters
            }
          ),
          page: DEFAULT_QUERY.page,
          perPage: DEFAULT_QUERY.perPage
        }
      }
    }));
  },

  openFilteredRelatedContext: (
    table,
    parentTable,
    parentRowIds,
    accessor,
    filterColumn,
    filters
  ) => {
    set((state) => ({
      activeTable: table,
      views: {
        ...state.views,
        [table]: {
          ...dropPendingNewRows(state.views[table] ?? freshTableView()),
          status: "idle",
          rows: [],
          meta: null,
          error: null,
          source: { kind: "table" },
          selectedRowIds: [],
          context: {
            kind: "related",
            parentTable,
            parentRowIds,
            accessor,
            via: "filter",
            filterColumn
          },
          config: patchExplorerViewConfig(
            state.views[table]?.config ?? DEFAULT_EXPLORER_VIEW_CONFIG,
            {
              search: DEFAULT_QUERY.search,
              sort: DEFAULT_QUERY.sort,
              filters
            }
          ),
          page: DEFAULT_QUERY.page,
          perPage: DEFAULT_QUERY.perPage
        }
      }
    }));
  },

  clearContext: (table, nextQuery = {}) => {
    const targetTable = table ?? get().activeTable;
    if (!targetTable) return;

    set((state) => ({
      activeTable: targetTable,
      views: {
        ...state.views,
        [targetTable]: {
          ...dropPendingNewRows(state.views[targetTable] ?? freshTableView()),
          status: "idle",
          rows: [],
          meta: null,
          error: null,
          source: { kind: "table" },
          selectedRowIds: [],
          context: null,
          config: patchExplorerViewConfig(
            state.views[targetTable]?.config ?? DEFAULT_EXPLORER_VIEW_CONFIG,
            {
              search: nextQuery.search ?? DEFAULT_QUERY.search,
              sort: nextQuery.sort ?? DEFAULT_QUERY.sort,
              filters: nextQuery.filters ?? DEFAULT_QUERY.filters
            }
          ),
          page: nextQuery.page ?? DEFAULT_QUERY.page,
          perPage: nextQuery.perPage ?? DEFAULT_QUERY.perPage
        }
      }
    }));
  },

  // ── Saved-views actions ───────────────────────────────────────────────────

  loadSavedViews: async (tableId) => {
    set((state) => {
      const view = state.views[tableId] ?? freshTableView();
      return {
        views: {
          ...state.views,
          [tableId]: { ...view, savedViewsStatus: "loading" }
        }
      };
    });

    try {
      const result = await listViews(tableId);
      const views = result.data ?? [];
      const prefs = result.preferences ?? { default_view_id: null, auto_save: false };
      set((state) => {
        const view = state.views[tableId] ?? freshTableView();
        return {
          defaultViewIdByTable: { ...state.defaultViewIdByTable, [tableId]: prefs.default_view_id },
          views: {
            ...state.views,
            [tableId]: { ...view, savedViews: views, savedViewsStatus: "ready", autoSave: prefs.auto_save }
          }
        };
      });
    } catch {
      set((state) => {
        const view = state.views[tableId] ?? freshTableView();
        return {
          views: {
            ...state.views,
            [tableId]: { ...view, savedViewsStatus: "error" }
          }
        };
      });
    }
  },

  switchView: (viewId) => {
    cancelAutoSave(getAutoSaveTarget());

    const { activeTable, views, fetchRows } = get();
    if (!activeTable) return;

    const view = views[activeTable] ?? freshTableView();

    let newConfig: ExplorerViewConfig;
    if (viewId === null) {
      // "All Records" — reset to defaults
      newConfig = cloneExplorerViewConfig(DEFAULT_EXPLORER_VIEW_CONFIG);
    } else {
      const saved = view.savedViews.find((v) => v.id === viewId);
      if (!saved) return; // unknown id — ignore
      // Clone the saved config and force config.type to match the
      // saved view's top-level type — they can drift if the config was
      // saved while on a different view type.
      newConfig = cloneExplorerViewConfig(saved.config);
      if (newConfig.type !== saved.type) {
        newConfig = rebuildConfigForType(newConfig, saved.type);
      }
    }

    set((state) => ({
      views: {
        ...state.views,
        [activeTable]: {
          ...view,
          config: newConfig,
          activeViewId: viewId,
          page: DEFAULT_QUERY.page,
          selectedRowIds: [],
          context: null,
          rows: [],
          meta: null,
          error: null,
          status: "idle"
        }
      }
    }));

    void fetchRows();
  },

  saveCurrentAsView: async (name, type, isShared) => {
    const { activeTable, views } = get();
    if (!activeTable) return;

    const view = views[activeTable] ?? freshTableView();
    let config = extractExplorerViewConfig(view);
    // Ensure config.type matches the requested type so the
    // persisted config JSON and the saved-view type agree.
    if (config.type !== type) {
      config = rebuildConfigForType(config, type);
    }

    const created = await createView({
      name,
      table_name: activeTable,
      type,
      config,
      is_shared: isShared
    });

    set((state) => {
      const v = state.views[activeTable] ?? freshTableView();
      return {
        views: {
          ...state.views,
          [activeTable]: {
            ...v,
            savedViews: [...v.savedViews, created],
            activeViewId: created.id,
            // If the user created a view with a different type than the
            // current runtime config (now possible via the create modal),
            // re-hydrate the runtime config to match the created view.
            config: cloneExplorerViewConfig(created.config),
          }
        }
      };
    });
  },

  updateActiveViewConfig: async () => {
    const { activeTable, views } = get();
    if (!activeTable) return;

    const view = views[activeTable];
    if (!view || view.activeViewId === null) return;

    await get().updateViewConfigFor(activeTable, view.activeViewId);
  },

  updateViewConfigFor: async (tableId, viewId) => {
    const { views } = get();
    const view = views[tableId];
    if (!view) return;
    if (view.activeViewId !== viewId) return;

    const target = view.savedViews.find((sv) => sv.id === viewId);
    if (!currentUserOwns(target)) return;

    const config = extractExplorerViewConfig(view);
    const updated = await updateView(viewId, { config });

    set((state) => {
      const v = state.views[tableId];
      if (!v) return state;
      return {
        views: {
          ...state.views,
          [tableId]: {
            ...v,
            savedViews: v.savedViews.map((sv) =>
              sv.id === updated.id ? updated : sv
            )
          }
        }
      };
    });
  },

  renameView: async (id, name) => {
    const { activeTable, views } = get();
    if (!activeTable) return;

    const target = views[activeTable]?.savedViews.find((sv) => sv.id === id);
    if (!currentUserOwns(target)) return;

    const updated = await updateView(id, { name });

    set((state) => {
      const v = state.views[activeTable];
      if (!v) return state;
      return {
        views: {
          ...state.views,
          [activeTable]: {
            ...v,
            savedViews: v.savedViews.map((sv) =>
              sv.id === updated.id ? updated : sv
            )
          }
        }
      };
    });
  },

  toggleViewShared: async (id) => {
    const { activeTable, views } = get();
    if (!activeTable) return;

    const view = views[activeTable];
    const target = view?.savedViews.find((sv) => sv.id === id);
    if (!target) return;
    if (!currentUserOwns(target)) return;

    const updated = await updateView(id, { is_shared: !target.is_shared });

    set((state) => {
      const v = state.views[activeTable];
      if (!v) return state;
      return {
        views: {
          ...state.views,
          [activeTable]: {
            ...v,
            savedViews: v.savedViews.map((sv) =>
              sv.id === updated.id ? updated : sv
            )
          }
        }
      };
    });
  },

  deleteView: async (id) => {
    const { activeTable, views, switchView } = get();
    if (!activeTable) return;

    const target = views[activeTable]?.savedViews.find((sv) => sv.id === id);
    if (!currentUserOwns(target)) return;

    await deleteViewApi(id);

    const wasActive = views[activeTable]?.activeViewId === id;

    set((state) => {
      const v = state.views[activeTable];
      if (!v) return state;
      return {
        views: {
          ...state.views,
          [activeTable]: {
            ...v,
            savedViews: v.savedViews.filter((sv) => sv.id !== id)
          }
        }
      };
    });

    if (wasActive) {
      switchView(null);
    }
  },

  changeViewType: async (id, type) => {
    const { activeTable, views } = get();
    if (!activeTable) return;

    const view = views[activeTable];
    const target = view?.savedViews.find((sv) => sv.id === id);
    if (!target) return;
    if (!currentUserOwns(target)) return;

    // Re-shape the config for the new type. patchExplorerViewConfig can't
    // change the type itself, so we build the new config by hand.
    const base = {
      search: target.config.search,
      sort: target.config.sort.map((s) => ({ ...s })),
      filters: target.config.filters.map((f) => ({ ...f })),
      hiddenColumnNames: [...target.config.hiddenColumnNames],
      columnOrder: [...target.config.columnOrder]
    };
    const nextConfig: ExplorerViewConfig =
      type === "kanban"
        ? { ...base, type: "kanban", kanbanColumn: null, cardFieldNames: [], hiddenLaneValues: [] }
        : { ...base, type: "table" };

    const updated = await updateView(id, { type, config: nextConfig });

    set((state) => {
      const v = state.views[activeTable];
      if (!v) return state;
      const nextSavedViews = v.savedViews.map((sv) =>
        sv.id === updated.id ? updated : sv
      );
      // If this is the active view, re-hydrate its config so the UI reflects
      // the new type without a full re-switch.
      const nextView =
        v.activeViewId === id
          ? { ...v, savedViews: nextSavedViews, config: cloneExplorerViewConfig(updated.config) }
          : { ...v, savedViews: nextSavedViews };
      return {
        views: {
          ...state.views,
          [activeTable]: nextView
        }
      };
    });
  },

  duplicateView: async (id, name) => {
    const { activeTable, views } = get();
    if (!activeTable) return;

    const view = views[activeTable];
    const source = view?.savedViews.find((sv) => sv.id === id);
    if (!source) return;

    // Clone the source config so edits to the copy don't bleed back.
    const configCopy = cloneExplorerViewConfig(source.config);

    const created = await createView({
      name,
      table_name: activeTable,
      type: source.type,
      config: configCopy,
      is_shared: false
    });

    set((state) => {
      const v = state.views[activeTable] ?? freshTableView();
      return {
        views: {
          ...state.views,
          [activeTable]: {
            ...v,
            savedViews: [...v.savedViews, created],
            config: cloneExplorerViewConfig(created.config),
            activeViewId: created.id
          }
        }
      };
    });
  },

  setDefaultView: async (id) => {
    const { activeTable } = get();
    if (!activeTable) return;
    set((state) => {
      const next = { ...state.defaultViewIdByTable, [activeTable]: id };
      return { defaultViewIdByTable: next };
    });

    // Persist per-user so the default follows the account across browsers.
    try {
      await updateExplorerTablePreferences(activeTable, { default_view_id: id });
    } catch {
      // If the backend can't be reached, keep the in-memory preference.
    }
  },

  setAutoSave: (enabled) => {
    const { activeTable, views } = get();
    if (!activeTable) return;

    // Only allow enabling auto-save on views the current user owns. Turning
    // it off is always allowed (safety release valve).
    if (enabled) {
      const v = views[activeTable];
      const target = v?.savedViews.find((sv) => sv.id === v.activeViewId);
      if (!currentUserOwns(target)) return;
    }

    set((state) => {
      const v = state.views[activeTable] ?? freshTableView();
      return {
        views: {
          ...state.views,
          [activeTable]: { ...v, autoSave: enabled }
        }
      };
    });

    // Persist per-user so the toggle survives refreshes and other browsers.
    void updateExplorerTablePreferences(activeTable, { auto_save: enabled }).catch(() => {
      /* ignore */
    });

    // Immediately persist if turning on with an active saved view
    if (enabled) {
      const view = views[activeTable];
      if (view?.activeViewId !== null) {
        void get().updateActiveViewConfig();
      }
    }
  },

  setKanbanConfig: (patch) => {
    const { activeTable, views } = get();
    if (!activeTable) return;
    const view = views[activeTable];
    if (!view) return;
    if (view.config.type !== "kanban") return;

    const nextConfig: ExplorerViewConfig = {
      ...view.config,
      kanbanColumn:
        patch.kanbanColumn !== undefined
          ? patch.kanbanColumn
          : view.config.kanbanColumn,
      cardFieldNames: patch.cardFieldNames
        ? [...patch.cardFieldNames].slice(0, 3)
        : [...view.config.cardFieldNames],
      hiddenLaneValues: patch.hiddenLaneValues
        ? [...patch.hiddenLaneValues]
        : [...(view.config.hiddenLaneValues ?? [])]
    };

    set((state) => ({
      views: {
        ...state.views,
        [activeTable]: { ...view, config: nextConfig }
      }
    }));

    scheduleAutoSave();
  },

  openRecordEditor: async ({ table, rowId }) => {
    const rowIdString = String(rowId);
    set((state) => {
      const view = state.views[table] ?? freshTableView();
      const existing = view.rows.find((row) => String(row.id ?? "") === rowIdString) ?? null;
      const entry: RecordEditorEntry = {
        table,
        rowId: rowIdString,
        status: "loading",
        row: existing,
        error: null,
      };
      return {
        views: state.views[table] ? state.views : { ...state.views, [table]: view },
        recordCacheByTable:
          existing === null
            ? state.recordCacheByTable
            : upsertRecordCacheEntry(state.recordCacheByTable, table, rowIdString, existing),
        recordEditor: { open: true, stack: [entry] },
      };
    });

    await loadRecordEditorEntry(set, get, table, rowIdString, 0);
  },

  pushRecordEditor: async ({ table, rowId }) => {
    const current = get().recordEditor;
    if (!current || !current.open) return;

    const rowIdString = String(rowId);
    // No-op if the top entry already matches (prevent double-push on re-click).
    const top = current.stack[current.stack.length - 1];
    if (top && top.table === table && top.rowId === rowIdString) return;

    let pushedIndex = -1;
    set((state) => {
      const editor = state.recordEditor;
      if (!editor || !editor.open) return state;
      const view = state.views[table] ?? freshTableView();
      const existing = view.rows.find((row) => String(row.id ?? "") === rowIdString) ?? null;
      const entry: RecordEditorEntry = {
        table,
        rowId: rowIdString,
        status: "loading",
        row: existing,
        error: null,
      };
      const nextStack = [...editor.stack, entry];
      pushedIndex = nextStack.length - 1;
      return {
        views: state.views[table] ? state.views : { ...state.views, [table]: view },
        recordCacheByTable:
          existing === null
            ? state.recordCacheByTable
            : upsertRecordCacheEntry(state.recordCacheByTable, table, rowIdString, existing),
        recordEditor: { ...editor, stack: nextStack },
      };
    });

    if (pushedIndex === -1) return;
    await loadRecordEditorEntry(set, get, table, rowIdString, pushedIndex);
  },

  popRecordEditor: () => {
    set((state) => {
      const editor = state.recordEditor;
      if (!editor) return state;
      if (editor.stack.length <= 1) {
        return { recordEditor: null };
      }
      return {
        recordEditor: { ...editor, stack: editor.stack.slice(0, -1) },
      };
    });
  },

  closeRecordEditor: () => {
    set({ recordEditor: null });
  },

  retryRecordEditorLoad: async () => {
    const editor = get().recordEditor;
    if (!editor || editor.stack.length === 0) return;
    const topIndex = editor.stack.length - 1;
    const top = editor.stack[topIndex]!;
    set((state) => {
      const current = state.recordEditor;
      if (!current) return state;
      const nextStack = current.stack.slice();
      const target = nextStack[topIndex];
      if (!target) return state;
      nextStack[topIndex] = { ...target, status: "loading", error: null };
      return { recordEditor: { ...current, stack: nextStack } };
    });
    await loadRecordEditorEntry(set, get, top.table, top.rowId, topIndex);
  },

  reset: () => {
    cancelAutoSave();
    set({ ...initialState });
  }
}));

// Reset on logout so the next user never sees stale rows.
registerLogoutHook(() => {
  useExplorerStore.getState().reset();
});

/** Test-only helper. */
export function resetExplorerStoreForTest(): void {
  cancelAutoSave();
  useExplorerStore.setState({ ...initialState });
}
