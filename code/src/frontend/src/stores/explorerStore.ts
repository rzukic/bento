import { create } from "zustand";
import { fetchRelatedRows, fetchTableRows, type ExplorerDataSource } from "../api/data";
import { ApiClientError } from "../api/client";
import { registerLogoutHook } from "./authStore";
import type {
  ExplorerQuery,
  FilterClause,
  ListMeta,
  SortClause
} from "../types/explorer";
import { DEFAULT_QUERY } from "../types/explorer";

// ── Per-table view state ──────────────────────────────────────────────

export type TableViewStatus = "idle" | "loading" | "ready" | "error";

export type TableViewState = {
  status: TableViewStatus;
  query: ExplorerQuery;
  rows: Record<string, unknown>[];
  meta: ListMeta | null;
  error: string | null;
  source: ExplorerDataSource;
  hiddenColumnNames: string[];
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
        filterColumn: string;
      }
    | null;
};

function freshTableView(): TableViewState {
  return {
    status: "idle",
    query: { ...DEFAULT_QUERY },
    rows: [],
    meta: null,
    error: null,
    source: { kind: "table" },
    hiddenColumnNames: [],
    selectedRowIds: [],
    context: null
  };
}

// ── Store shape ───────────────────────────────────────────────────────

type ExplorerStoreState = {
  /** The currently-active table name (from the route param). */
  activeTable: string | null;

  /** Cached view state per table name. Survives in-app navigation. */
  views: Record<string, TableViewState>;

  // ── Actions ──

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
    parentRowIds: string[],
    accessor: string,
    filterColumn: string
  ) => void;

  /** Clear the current table back to its normal base-table browsing state. */
  clearContext: (table?: string, nextQuery?: Partial<ExplorerQuery>) => void;

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
    const query = view.query;

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
              activeTable,
              view.source.filterColumn,
              view.source.parentRowIds,
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
    const nextQuery: ExplorerQuery = {
      ...view.query,
      ...patch,
      // Any query change other than page alone resets to page 1.
      page: patch.page ?? 1
    };
    set((state) => ({
      views: {
        ...state.views,
        [activeTable]: { ...view, query: nextQuery, selectedRowIds: [] }
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
          query: { ...view.query, page },
          selectedRowIds: []
        }
      }
    }));
    void fetchRows();
  },

  setSort: (sort) => {
    get().setQuery({ sort, page: 1 });
  },

  setSearch: (search) => {
    get().setQuery({ search, page: 1 });
  },

  setFilters: (filters) => {
    get().setQuery({ filters, page: 1 });
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
          query: {
            ...DEFAULT_QUERY,
            filters: [{ column: "id", op: "eq", value: String(rowId) }]
          }
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
            hiddenColumnNames: Array.from(new Set(columnNames.filter(Boolean)))
          }
        }
      };
    });
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

  openRelatedContext: (table, parentTable, parentRowIds, accessor, filterColumn) => {
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
            parentRowIds,
            accessor,
            filterColumn
          },
          selectedRowIds: [],
          context: {
            kind: "related",
            parentTable,
            parentRowIds,
            accessor,
            filterColumn
          },
          query: {
            ...DEFAULT_QUERY
          }
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
          query: {
            ...DEFAULT_QUERY,
            ...nextQuery
          }
        }
      }
    }));
  },

  reset: () => {
    set({ ...initialState });
  }
}));

// Reset on logout so the next user never sees stale rows.
registerLogoutHook(() => {
  useExplorerStore.getState().reset();
});

/** Test-only helper. */
export function resetExplorerStoreForTest(): void {
  useExplorerStore.setState({ ...initialState });
}
