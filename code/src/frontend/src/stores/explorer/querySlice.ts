import type { StateCreator } from "zustand";
import { fetchRelatedRows, fetchTableRows } from "../../api/data";
import { ApiClientError } from "../../api/client";
import {
  DEFAULT_EXPLORER_VIEW_CONFIG,
  DEFAULT_QUERY,
  patchExplorerViewConfig
} from "../../types/explorer";
import { scheduleAutoSave } from "./autoSave";
import {
  dropPendingNewRows,
  freshTableView,
  getExplorerQueryForView,
  getTableModel,
  normalizeRowId
} from "./helpers";
import type { ExplorerStoreState, QuerySlice, TableViewState } from "./types";

export const initialQueryState = {
  activeTable: null as string | null,
  views: {} as Record<string, TableViewState>,
};

export const createQuerySlice: StateCreator<ExplorerStoreState, [], [], QuerySlice> = (
  set,
  get
) => ({
  ...initialQueryState,

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
      const tableModel = getTableModel(activeTable);
      const normalizedRows = tableModel
        ? result.data.map((row) => normalizeRowId(tableModel, row))
        : result.data;
      set((state) => ({
        views: {
          ...state.views,
          [activeTable]: {
            ...state.views[activeTable]!,
            status: "ready",
            rows: normalizedRows,
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
    scheduleAutoSave(get);
  },

  setSearch: (search) => {
    get().setQuery({ search, page: 1 });
    scheduleAutoSave(get);
  },

  setFilters: (filters) => {
    get().setQuery({ filters, page: 1 });
    scheduleAutoSave(get);
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
});
