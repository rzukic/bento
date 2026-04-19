import { apiClient } from "./client";
import type { ExplorerQuery, ListResponse } from "../types/explorer";

export type ExplorerDataSource =
  | { kind: "table" }
  | {
      kind: "related";
      parentTable: string;
      accessor: string;
      parentRowId: string;
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

export function parseQueryString(search: string): Partial<ExplorerQuery> {
  const trimmed = search.startsWith("?") ? search.slice(1) : search;
  const params = new URLSearchParams(trimmed);
  const parsed: Partial<ExplorerQuery> = {};

  const page = Number(params.get("page"));
  if (Number.isFinite(page) && page > 0) {
    parsed.page = page;
  }

  const perPage = Number(params.get("per_page"));
  if (Number.isFinite(perPage) && perPage > 0) {
    parsed.perPage = perPage;
  }

  const sort = params.get("sort");
  if (sort && sort.trim()) {
    parsed.sort = sort
      .split(",")
      .map((token) => token.trim())
      .filter(Boolean)
      .map((token) =>
        token.startsWith("-")
          ? { column: token.slice(1), dir: "desc" as const }
          : { column: token, dir: "asc" as const }
      )
      .filter((clause) => clause.column);
  }

  const searchTerm = params.get("search");
  if (searchTerm !== null) {
    parsed.search = searchTerm;
  }

  const filters = Array.from(params.entries())
    .map(([key, value]) => {
      const match = key.match(/^filter\[([^\]]+)\](?:\[([^\]]+)\])?$/);
      if (!match) return null;
      const column = match[1];
      const op = (match[2] ?? "eq") as ExplorerQuery["filters"][number]["op"];
      if (!column) return null;
      if (op === "null" || op === "notnull") {
        return { column, op };
      }
      return { column, op, value };
    })
    .filter(
      (
        clause
      ): clause is NonNullable<typeof clause> => clause !== null
    );

  if (filters.length > 0) {
    parsed.filters = filters;
  }

  return parsed;
}

export function fetchTableRows(table: string, query: ExplorerQuery): Promise<ListResponse> {
  const qs = buildQueryString(query);
  return apiClient.get<ListResponse>(`/data/${encodeURIComponent(table)}?${qs}`);
}

/**
 * PATCH a single row by primary key. The backend accepts a partial body of
 * column → value pairs and returns the full updated row (including any
 * server-applied side-effects like `updated_at`).
 */
export function patchRow(
  table: string,
  rowId: string | number,
  partial: Record<string, unknown>,
): Promise<Record<string, unknown>> {
  return apiClient
    .patch<{ data: Record<string, unknown> }>(
      `/data/${encodeURIComponent(table)}/${encodeURIComponent(String(rowId))}`,
      partial,
    )
    .then((res) => res.data);
}

export function fetchRow(
  table: string,
  rowId: string | number,
): Promise<Record<string, unknown>> {
  return apiClient
    .get<{ data: Record<string, unknown> }>(
      `/data/${encodeURIComponent(table)}/${encodeURIComponent(String(rowId))}`,
    )
    .then((res) => res.data);
}

export function createRow(
  table: string,
  partial: Record<string, unknown>,
): Promise<Record<string, unknown>> {
  return apiClient
    .post<{ data: Record<string, unknown> }>(
      `/data/${encodeURIComponent(table)}`,
      partial,
    )
    .then((res) => res.data);
}

export function deleteRow(
  table: string,
  rowId: string | number,
): Promise<void> {
  return apiClient.delete<void>(
    `/data/${encodeURIComponent(table)}/${encodeURIComponent(String(rowId))}`,
  );
}

export function fetchRelatedRows(
  parentTable: string,
  parentRowId: string,
  accessor: string,
  query: ExplorerQuery
): Promise<ListResponse> {
  const qs = buildQueryString(query);
  return apiClient.get<ListResponse>(
    `/data/${encodeURIComponent(parentTable)}/${encodeURIComponent(parentRowId)}/${encodeURIComponent(accessor)}?${qs}`
  );
}
