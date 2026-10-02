import type { ExplorerDataSource } from "../../api/data";
import type {
  ExplorerViewConfig,
  ExplorerViewType,
  ExplorerQuery,
  FilterClause,
  ListMeta,
  SortClause
} from "../../types/explorer";
import type { SavedView } from "../../types/views";

// ── Per-table view state ──────────────────────────────────────────────

export type TableViewStatus = "idle" | "loading" | "ready" | "error";
export type SavedViewsStatus = "idle" | "loading" | "ready" | "error";

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

export type UndoDeletedRowSnapshot = {
  row: Record<string, unknown>;
  index: number;
};

export type UndoEntry = {
  kind: "delete";
  table: string;
  rows: UndoDeletedRowSnapshot[];
  at: number;
};

export type FieldError = { field: string; message: string };

export type AutoSaveTarget = { tableId: string; viewId: number };

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

export type RecordCacheByTable = Record<string, Record<string, Record<string, unknown>>>;

// ── Slice: query / rows / context ─────────────────────────────────────

export type QuerySlice = {
  /** The currently-active table name (from the route param). */
  activeTable: string | null;

  /** Cached view state per table name. Survives in-app navigation. */
  views: Record<string, TableViewState>;

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

  // ── Context actions ──

  /** Open a table focused on a single related/reference row. */
  openRecordContext: (table: string, rowId: number, context: {
    sourceTable: string;
    sourceRowId: number;
    sourceColumn: string;
  }) => void;

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
};

// ── Slice: inline cell editing (TASK-092 / TASK-093) ──────────────────

export type EditingSlice = {
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
};

// ── Slice: row operations, selection and undo ─────────────────────────

export type RowOpsSlice = {
  /** Session-only undo history for destructive row actions, keyed by table. */
  undoStackByTable: Record<string, UndoEntry[]>;

  /** Toggle a single selected record from the current table grid. */
  toggleRowSelection: (table: string, rowId: string) => void;

  /** Toggle all visible rows in the current table grid. */
  toggleSelectAllVisible: (table: string, rowIds: string[]) => void;

  /** Replace the current visible-row selection with an explicit set. */
  setSelectedRows: (table: string, rowIds: string[]) => void;

  /** Clear selection for one table or the current table. */
  clearSelection: (table?: string) => void;

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
};

// ── Slice: saved views, view config and auto-save ─────────────────────

export type ViewSlice = {
  /** Id of the default view per table (null = "All Records"). */
  defaultViewIdByTable: Record<string, number | null>;

  /** Replace the hidden columns for a table. */
  setHiddenColumns: (table: string, columnNames: string[]) => void;

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

  /** Enable or disable auto-save for the active table's active view. */
  setAutoSave: (enabled: boolean) => void;

  /**
   * Update the kanban-specific config (grouping column and secondary card
   * fields) on the current active view. No-op if the active view config is
   * not a kanban view. Triggers auto-save if enabled.
   */
  setKanbanConfig: (patch: { kanbanColumn?: string | null; cardFieldNames?: string[]; hiddenLaneValues?: string[] }) => void;
};

// ── Slice: record editor (TASK-098, TASK-099) ─────────────────────────

export type RecordEditorSlice = {
  /**
   * Single-record cache used by the full-screen editor. Unlike `views[table]`,
   * this cache is allowed to hold records that are not part of the table's
   * current query/page result set.
   */
  recordCacheByTable: RecordCacheByTable;

  /**
   * The currently-open full-screen record editor (TASK-098). Null when
   * dismissed. The editor reads from a dedicated single-record cache while
   * still reconciling visible grid rows in-place when they are part of the
   * current table result set.
   */
  recordEditor: RecordEditorState | null;

  /**
   * Open the full-screen record editor with a fresh stack containing the given
   * entry. Fetches the row from `GET /data/:table/:id` and writes it into the
   * editor's single-record cache, reconciling the current table view only when
   * that row is already part of the visible result set.
   */
  openRecordEditor: (params: {
    table: string;
    rowId: string | number;
    row?: Record<string, unknown> | null;
  }) => Promise<void>;

  /**
   * Push another record onto the in-modal navigation stack (TASK-099).
   * Typically invoked from a clicked FK value. Fetches the row; on error the
   * stack entry enters an error state without affecting earlier entries. No-op
   * when the editor is closed.
   */
  pushRecordEditor: (params: {
    table: string;
    rowId: string | number;
    row?: Record<string, unknown> | null;
  }) => Promise<void>;

  /**
   * Drop the top entry from the stack. If the stack becomes empty, closes the
   * editor entirely (TASK-099).
   */
  popRecordEditor: () => void;

  /** Close the record editor and clear the entire stack. */
  closeRecordEditor: () => void;

  /** Re-run the fetch for the top stack entry after a load error. */
  retryRecordEditorLoad: () => Promise<void>;
};

// ── Composed store ────────────────────────────────────────────────────

export type ExplorerStoreState = QuerySlice &
  EditingSlice &
  RowOpsSlice &
  ViewSlice &
  RecordEditorSlice & {
    /** Full reset (e.g. on logout). */
    reset: () => void;
  };
