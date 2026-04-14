import { create } from "zustand";
import { fetchRelatedRows, fetchTableRows, type ExplorerDataSource } from "../api/data";
import { ApiClientError } from "../api/client";
import {
  listViews,
  createView,
  updateView,
  deleteView as deleteViewApi
} from "../api/views";
import { registerLogoutHook } from "./authStore";
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

function cloneExplorerViewConfig(config: ExplorerViewConfig): ExplorerViewConfig {
  return patchExplorerViewConfig(config, {});
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

export function getExplorerQueryForView(view: TableViewState): ExplorerQuery {
  return buildExplorerQuery(view.config, {
    page: view.page,
    perPage: view.perPage
  });
}

// ── Auto-save debounce ─────────────────────────────────────────────────────
// Module-level timer so it survives store re-renders without polluting state.

let _autoSaveTimer: ReturnType<typeof setTimeout> | null = null;

function scheduleAutoSave(): void {
  if (_autoSaveTimer !== null) clearTimeout(_autoSaveTimer);
  _autoSaveTimer = setTimeout(() => {
    _autoSaveTimer = null;
    const store = useExplorerStore.getState();
    const { activeTable } = store;
    if (!activeTable) return;
    const view = store.views[activeTable];
    if (!view?.autoSave || view.activeViewId === null) return;
    void store.updateActiveViewConfig();
  }, 500);
}

function cancelAutoSave(): void {
  if (_autoSaveTimer !== null) {
    clearTimeout(_autoSaveTimer);
    _autoSaveTimer = null;
  }
}

// ── Store shape ───────────────────────────────────────────────────────

type ExplorerStoreState = {
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

  /** Rename a saved view (owner only). */
  renameView: (id: number, name: string) => Promise<void>;

  /** Toggle the is_shared flag on a saved view (owner only). */
  toggleViewShared: (id: number) => Promise<void>;

  /** Delete a saved view. Falls back to "All Records" if it was active. */
  deleteView: (id: number) => Promise<void>;

  /** Enable or disable auto-save for the active table's active view. */
  setAutoSave: (enabled: boolean) => void;

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

// ── Store ─────────────────────────────────────────────────────────────

const initialState = {
  activeTable: null as string | null,
  views: {} as Record<string, TableViewState>
};

export const useExplorerStore = create<ExplorerStoreState>((set, get) => ({
  ...initialState,

  setActiveTable: (table) => {
    set((state) => {
      if (state.activeTable === table && state.views[table]) return state;
      const views = { ...state.views };
      if (!views[table]) {
        views[table] = freshTableView();
      }
      return { activeTable: table, views };
    });
  },

  fetchRows: async () => {
    const { activeTable, views } = get();
    if (!activeTable) return;

    const view = views[activeTable] ?? freshTableView();
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
    const view = views[activeTable] ?? freshTableView();
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

  setPage: (page) => {
    const { activeTable, views, fetchRows } = get();
    if (!activeTable) return;
    const view = views[activeTable] ?? freshTableView();
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
          ...(state.views[table] ?? freshTableView()),
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

  openRelatedContext: (table, parentTable, parentRowId, accessor) => {
    set((state) => ({
      activeTable: table,
      views: {
        ...state.views,
        [table]: {
          ...(state.views[table] ?? freshTableView()),
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
          ...(state.views[table] ?? freshTableView()),
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
          ...(state.views[targetTable] ?? freshTableView()),
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
      const views = await listViews(tableId);
      set((state) => {
        const view = state.views[tableId] ?? freshTableView();
        return {
          views: {
            ...state.views,
            [tableId]: { ...view, savedViews: views, savedViewsStatus: "ready" }
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
    cancelAutoSave();

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
      newConfig = cloneExplorerViewConfig(saved.config);
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
    const config = extractExplorerViewConfig(view);

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
            activeViewId: created.id
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

    const config = extractExplorerViewConfig(view);
    const updated = await updateView(view.activeViewId, { config });

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

  renameView: async (id, name) => {
    const { activeTable, views } = get();
    if (!activeTable) return;

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

  setAutoSave: (enabled) => {
    const { activeTable, views } = get();
    if (!activeTable) return;

    set((state) => {
      const v = state.views[activeTable] ?? freshTableView();
      return {
        views: {
          ...state.views,
          [activeTable]: { ...v, autoSave: enabled }
        }
      };
    });

    // Immediately persist if turning on with an active saved view
    if (enabled) {
      const view = views[activeTable];
      if (view?.activeViewId !== null) {
        void get().updateActiveViewConfig();
      }
    }
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
