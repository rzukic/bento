import { create } from "zustand";
import { fetchTableRows } from "../api/data";
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
};

function freshTableView(): TableViewState {
  return {
    status: "idle",
    query: { ...DEFAULT_QUERY },
    rows: [],
    meta: null,
    error: null
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
      const result = await fetchTableRows(activeTable, query);
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
        [activeTable]: { ...view, query: nextQuery }
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
          query: { ...view.query, page }
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
