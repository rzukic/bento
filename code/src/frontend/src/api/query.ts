import { apiClient } from "./client";

/** Filter conditions: `{ column: value }` or `{ column: { op: value } }` (Part 6 R6.9). */
export type QueryFilter = Record<string, unknown>;

export type QueryBucket = "day" | "week" | "month" | "quarter" | "year";

export type QueryGroupBy = {
  column: string;
  bucket?: QueryBucket;
  as?: string;
  /** Only the N values ranked first by the first measure ("top values"). */
  top?: number;
  /** With top: the remaining values as one "Other" group (flagged in `<as>_other`). */
  other?: boolean;
  /** With top: "desc" largest first (default), "asc" smallest first, "abs" the largest amounts of either sign (money flows). */
  rank?: "asc" | "desc" | "abs";
};

export type QueryFunction = "count" | "sum" | "avg" | "min" | "max" | "count_distinct" | "median" | "percentile";

/** A named chart colour; green and red follow the theme's success and danger colours. */
export type SeriesColor = "blue" | "teal" | "amber" | "violet" | "rose" | "green" | "red" | "gray";

/** How a chart shows a measure over its date axis (Step 3.6c), computed in the browser. */
export type MeasureShow = "change" | "moving_average";

export type QueryMeasure = {
  name: string;
  /** count, sum, …; absent when the measure is a formula. */
  fn?: QueryFunction;
  column?: string;
  /** With fn percentile: 0–100. */
  percentile?: number;
  /** Charts only, never sent: the change from the period before, or a moving average over `window` periods. */
  show?: MeasureShow;
  /** With show moving_average: how many periods (default 3). */
  window?: number;
  /** Charts only, never sent: the series colour (default: the palette in order). */
  color?: SeriesColor;
  /** Charts only, never sent (Step 4.3): on a second value axis on the right, e.g. a return beside a value. */
  axis?: "right";
  /** Charts only, never sent: how the right axis and the tooltip write this measure, e.g. percent. */
  format?: "currency" | "number" | "integer" | "percent";
  /** A formula over the group's rows (Step 3.6): `SUM(amount WHERE amount > 0)`, `RUNNING_SUM(…)`. */
  formula?: string;
  running?: boolean;
  carryIn?: boolean;
};

/** Values at the end of each period: one row per period, measures with BUCKET_END(). */
/** Without `from` ("all time") the periods begin where the data does, e.g. with the first trade. */
export type QueryAsOf = { bucket: QueryBucket; from?: string; to: string; as?: string };

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
  role: "group" | "label" | "measure" | "other";
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

export type QueryVariableResult = {
  name: string;
  type: string | null;
  value: VariableValue;
  error: string | null;
  /** The variable's label from the dashboard (added by the page, not the server): shown under a key figure. */
  label?: string;
};

/** POST /api/v1/query/variables — values in the order given; a broken one has an error. */
export async function evaluateVariables(variables: QueryVariable[]): Promise<QueryVariableResult[]> {
  return (await apiClient.post<DataEnvelope<QueryVariableResult[]>>("/query/variables", { variables })).data;
}

// ── Saved queries as tables (Step 5.8) ────────────────────────────────────

export type QueryTableStatus = { table: string; queryId: number; refreshedAt: string | null; rows: number; current: boolean; error: string | null };

/** The saved query's result as a read-only table of the model (admins). */
export async function saveQueryAsTable(id: number, name?: string): Promise<QueryTableStatus> {
  return (await apiClient.post<DataEnvelope<QueryTableStatus>>(`/queries/${id}/table`, name ? { name } : {})).data;
}

export async function getQueryTable(table: string): Promise<QueryTableStatus> {
  return (await apiClient.get<DataEnvelope<QueryTableStatus>>(`/query-tables/${encodeURIComponent(table)}`)).data;
}

export async function refreshQueryTable(table: string): Promise<QueryTableStatus> {
  return (await apiClient.post<DataEnvelope<QueryTableStatus>>(`/query-tables/${encodeURIComponent(table)}/refresh`)).data;
}
