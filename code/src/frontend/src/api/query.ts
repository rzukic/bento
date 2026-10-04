import { apiClient } from "./client";

/** Filter conditions: `{ column: value }` or `{ column: { op: value } }` (Part 6 R6.9). */
export type QueryFilter = Record<string, unknown>;

export type QueryBucket = "day" | "week" | "month" | "quarter" | "year";

export type QueryGroupBy = { column: string; bucket?: QueryBucket; as?: string };

export type QueryFunction = "count" | "sum" | "avg" | "min" | "max" | "count_distinct";

export type QueryMeasure = {
  name: string;
  /** count, sum, …; absent when the measure is a formula. */
  fn?: QueryFunction;
  column?: string;
  /** A formula over the group's rows (Step 3.6): `SUM(amount WHERE amount > 0)`, `RUNNING_SUM(…)`. */
  formula?: string;
  running?: boolean;
  carryIn?: boolean;
};

/** Values at the end of each period: one row per period, measures with BUCKET_END(). */
export type QueryAsOf = { bucket: QueryBucket; from: string; to: string; as?: string };

export type VariableValue = number | string | boolean | null;

export type AggregationQuery = {
  source: string;
  filter?: QueryFilter;
  groupBy?: QueryGroupBy[];
  measures?: QueryMeasure[];
  sort?: string[];
  limit?: number;
  asOf?: QueryAsOf;
  /** Dashboard variables used in measure formulas. */
  variables?: Record<string, VariableValue>;
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

export type QueryVariable = { name: string; source?: string; formula: string; filter?: QueryFilter };

export type QueryVariableResult = { name: string; type: string | null; value: VariableValue; error: string | null };

/** POST /api/v1/query/variables — values in the order given; a broken one has an error. */
export async function evaluateVariables(variables: QueryVariable[]): Promise<QueryVariableResult[]> {
  return (await apiClient.post<DataEnvelope<QueryVariableResult[]>>("/query/variables", { variables })).data;
}
