/**
 * Explorer query model — aligned 1:1 with the backend's
 * `GET /api/v1/data/{table}` query contract.
 */

/** Sort direction for a single column. */
export type SortDir = "asc" | "desc";

/** One sort clause: column name + direction. */
export type SortClause = {
  column: string;
  dir: SortDir;
};

/**
 * Filter operators supported by the backend's `filter[col][op]` syntax.
 */
export type FilterOp =
  | "eq"
  | "neq"
  | "gt"
  | "gte"
  | "lt"
  | "lte"
  | "like"
  | "in"
  | "null"
  | "notnull";

/** One filter clause: column, operator, optional value. */
export type FilterClause = {
  column: string;
  op: FilterOp;
  value?: string;
};

export type ExplorerViewType = "table" | "kanban";

type ExplorerBaseViewConfig = {
  type: ExplorerViewType;
  search: string;
  sort: SortClause[];
  filters: FilterClause[];
  hiddenColumnNames: string[];
  columnOrder: string[];
};

export type TableExplorerViewConfig = ExplorerBaseViewConfig & {
  type: "table";
};

export type KanbanExplorerViewConfig = ExplorerBaseViewConfig & {
  type: "kanban";
  kanbanColumn: string | null;
  cardFieldNames: string[];
  hiddenLaneValues: string[];
};

export type ExplorerViewConfig =
  | TableExplorerViewConfig
  | KanbanExplorerViewConfig;

/**
 * The complete query state for a single table view. Every field maps
 * directly to a backend query parameter.
 */
export type ExplorerQuery = {
  page: number;
  perPage: number;
  sort: SortClause[];
  search: string;
  filters: FilterClause[];
};

/** Pagination metadata returned in the `meta` envelope. */
export type ListMeta = {
  page: number;
  per_page: number;
  total: number;
  total_pages: number;
};

/** The list response envelope: `{ data, meta }`. */
export type ListResponse<T = Record<string, unknown>> = {
  data: T[];
  meta: ListMeta;
};

/** Default query state for a fresh table view. */
export const DEFAULT_QUERY: ExplorerQuery = {
  page: 1,
  perPage: 25,
  sort: [],
  search: "",
  filters: []
};

export const DEFAULT_EXPLORER_VIEW_CONFIG: TableExplorerViewConfig = {
  type: "table",
  search: "",
  sort: [],
  filters: [],
  hiddenColumnNames: [],
  columnOrder: []
};

export function buildExplorerQuery(
  config: ExplorerViewConfig,
  options?: { page?: number; perPage?: number }
): ExplorerQuery {
  return {
    page: options?.page ?? DEFAULT_QUERY.page,
    perPage: options?.perPage ?? DEFAULT_QUERY.perPage,
    search: config.search,
    sort: [...config.sort],
    filters: config.filters.map((filter) => ({ ...filter }))
  };
}

export function patchExplorerViewConfig(
  config: ExplorerViewConfig,
  patch: Partial<Pick<ExplorerQuery, "search" | "sort" | "filters">> &
    Partial<Pick<ExplorerBaseViewConfig, "hiddenColumnNames" | "columnOrder">>
): ExplorerViewConfig {
  const nextBase: ExplorerBaseViewConfig = {
    ...config,
    search: patch.search ?? config.search,
    sort: patch.sort ? patch.sort.map((sort) => ({ ...sort })) : [...config.sort],
    filters: patch.filters
      ? patch.filters.map((filter) => ({ ...filter }))
      : config.filters.map((filter) => ({ ...filter })),
    hiddenColumnNames: patch.hiddenColumnNames
      ? [...patch.hiddenColumnNames]
      : [...config.hiddenColumnNames],
    columnOrder: patch.columnOrder ? [...patch.columnOrder] : [...config.columnOrder]
  };

  if (config.type === "kanban") {
    return {
      ...nextBase,
      type: "kanban",
      kanbanColumn: config.kanbanColumn,
      cardFieldNames: [...config.cardFieldNames],
      hiddenLaneValues: [...(config.hiddenLaneValues ?? [])]
    };
  }

  return {
    ...nextBase,
    type: "table"
  };
}
