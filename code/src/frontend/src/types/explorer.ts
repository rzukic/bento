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
