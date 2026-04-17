import { create } from "zustand";
import { fetchRelatedRows, fetchTableRows, type ExplorerDataSource } from "../api/data";
import { ApiClientError } from "../api/client";
import {
  listViews,
  createView,
  updateView,
  deleteView as deleteViewApi
} from "../api/views";
import { updateExplorerTablePreferences } from "../api/explorerPreferences";
import { registerLogoutHook, useAuthStore } from "./authStore";
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
let _autoSaveTarget: { tableId: string; viewId: number } | null = null;

function scheduleAutoSave(): void {
  if (_autoSaveTimer !== null) clearTimeout(_autoSaveTimer);
  const store = useExplorerStore.getState();
  const { activeTable } = store;
  if (!activeTable) return;
  const view = store.views[activeTable];
  if (!view || view.activeViewId === null) return;
  // Capture the table/view that scheduled the save. This prevents quick
  // navigation between tables from persisting the wrong view.
  _autoSaveTarget = { tableId: activeTable, viewId: view.activeViewId };
  _autoSaveTimer = setTimeout(() => {
    _autoSaveTimer = null;
    const target = _autoSaveTarget;
    _autoSaveTarget = null;
    if (!target) return;
    const latest = useExplorerStore.getState();
    const view = latest.views[target.tableId];
    if (!view?.autoSave) return;
    void latest.updateViewConfigFor(target.tableId, target.viewId);
  }, 500);
}

function cancelAutoSave(): void {
  if (_autoSaveTimer !== null) {
    clearTimeout(_autoSaveTimer);
    _autoSaveTimer = null;
  }
  _autoSaveTarget = null;
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

// ── Store ─────────────────────────────────────────────────────────────

const initialState = {
  activeTable: null as string | null,
  views: {} as Record<string, TableViewState>,
  defaultViewIdByTable: {} as Record<string, number | null>
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
