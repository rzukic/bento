import { apiClient } from "./client";
import type { ExplorerQuery, ListResponse } from "../types/explorer";

/**
 * Serialise an {@link ExplorerQuery} into the URL search-parameter string
 * the backend expects. The encoding matches the `QueryParameterParser` on
 * the .NET side:
 *
 *   page=1&per_page=25&sort=name,-created_at&search=foo
 *   &filter[status]=active&filter[amount][gte]=100
 */
export function buildQueryString(query: ExplorerQuery): string {
  const params = new URLSearchParams();

  params.set("page", String(query.page));
  params.set("per_page", String(query.perPage));

  if (query.sort.length > 0) {
    const sortValue = query.sort
      .map((s) => (s.dir === "desc" ? `-${s.column}` : s.column))
      .join(",");
    params.set("sort", sortValue);
  }

  if (query.search) {
    params.set("search", query.search);
  }

  for (const f of query.filters) {
    if (f.op === "null" || f.op === "notnull") {
      // Nullary operators — the value is the operator itself.
      params.set(`filter[${f.column}][${f.op}]`, "");
    } else if (f.op === "eq") {
      // Shorthand for equals — `filter[col]=value`.
      params.set(`filter[${f.column}]`, f.value ?? "");
    } else {
      params.set(`filter[${f.column}][${f.op}]`, f.value ?? "");
    }
  }

  return params.toString();
}

/**
 * Fetch a paginated list of rows for a table.
 *
 * ```ts
 * const result = await fetchTableRows("orders", { page: 1, perPage: 25, sort: [], search: "", filters: [] });
 * result.data   // row objects
 * result.meta   // { page, per_page, total, total_pages }
 * ```
 */
export function fetchTableRows(
  table: string,
  query: ExplorerQuery
): Promise<ListResponse> {
  const qs = buildQueryString(query);
  return apiClient.get<ListResponse>(`/data/${encodeURIComponent(table)}?${qs}`);
}
