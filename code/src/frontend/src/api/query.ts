import { apiClient } from "./client";

/** Filter conditions: `{ column: value }` or `{ column: { op: value } }` (Part 6 R6.9). */
export type QueryFilter = Record<string, unknown>;

export type QueryBucket = "day" | "week" | "month" | "quarter" | "year";

export type QueryGroupBy = { column: string; bucket?: QueryBucket; as?: string };

export type QueryFunction = "count" | "sum" | "avg" | "min" | "max" | "count_distinct";

export type QueryMeasure = {
  name: string;
  fn: QueryFunction;
  column?: string;
  running?: boolean;
  carryIn?: boolean;
};

export type AggregationQuery = {
  source: string;
  filter?: QueryFilter;
  groupBy?: QueryGroupBy[];
  measures?: QueryMeasure[];
  sort?: string[];
  limit?: number;
};

export type QueryResultColumn = {
  name: string;
  type: string;
  role: "group" | "label" | "measure";
  labelOf?: string | null;
};

export type QueryRow = Record<string, string | number | boolean | null>;

export type QueryResult = {
  data: QueryRow[];
  meta: { columns: QueryResultColumn[]; truncated: boolean };
};

export type SavedQuery = {
  id: number;
  name: string;
  source: string;
  isShared: boolean;
  isOwner: boolean;
  query: AggregationQuery;
  createdAt: string;
  updatedAt: string;
};

type DataEnvelope<T> = { data: T };

/** POST /api/v1/query */
export function runQuery(query: AggregationQuery): Promise<QueryResult> {
  return apiClient.post<QueryResult>("/query", query);
}

/** POST /api/v1/queries/{id}/run — extra filters are added to the saved ones. */
export function runSavedQuery(id: number, filter?: QueryFilter): Promise<QueryResult> {
  return apiClient.post<QueryResult>(`/queries/${id}/run`, filter ? { filter } : {});
}

export async function listSavedQueries(source?: string): Promise<SavedQuery[]> {
  const suffix = source ? `?source=${encodeURIComponent(source)}` : "";
  return (await apiClient.get<DataEnvelope<SavedQuery[]>>(`/queries${suffix}`)).data;
}

export async function createSavedQuery(name: string, query: AggregationQuery, isShared = false): Promise<SavedQuery> {
  return (await apiClient.post<DataEnvelope<SavedQuery>>("/queries", { name, query, isShared })).data;
}

export async function getSavedQuery(id: number): Promise<SavedQuery> {
  return (await apiClient.get<DataEnvelope<SavedQuery>>(`/queries/${id}`)).data;
}
