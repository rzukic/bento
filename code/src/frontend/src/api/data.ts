import { apiClient } from "./client";
import type { ExplorerQuery, ListResponse } from "../types/explorer";

export type ExplorerDataSource =
  | { kind: "table" }
  | {
      kind: "related";
      parentTable: string;
      parentRowIds: string[];
      accessor: string;
      filterColumn: string;
    };

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
    params.set("search", query.search.trim());
  }

  for (const f of query.filters) {
    if (f.op === "null" || f.op === "notnull") {
      params.set(`filter[${f.column}][${f.op}]`, "");
    } else if (f.op === "eq") {
      params.set(`filter[${f.column}]`, f.value ?? "");
    } else {
      params.set(`filter[${f.column}][${f.op}]`, f.value ?? "");
    }
  }

  return params.toString();
}

export function fetchTableRows(table: string, query: ExplorerQuery): Promise<ListResponse> {
  const qs = buildQueryString(query);
  return apiClient.get<ListResponse>(`/data/${encodeURIComponent(table)}?${qs}`);
}

export function fetchRelatedRows(
  table: string,
  filterColumn: string,
  parentRowIds: string[],
  query: ExplorerQuery
): Promise<ListResponse> {
  const relationFilterValue = parentRowIds.join(",");
  const relationQuery: ExplorerQuery = {
    ...query,
    filters: [
      {
        column: filterColumn,
        op: parentRowIds.length > 1 ? "in" : "eq",
        value: relationFilterValue
      },
      ...query.filters
    ]
  };
  const qs = buildQueryString(relationQuery);
  return apiClient.get<ListResponse>(`/data/${encodeURIComponent(table)}?${qs}`);
}
