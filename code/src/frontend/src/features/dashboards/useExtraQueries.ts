import { useEffect, useState } from "react";
import type { DashboardWidget } from "../../api/dashboards";
import { fetchTableRows } from "../../api/data";
import { runQuery, type AggregationQuery, type QueryFilter, type QueryResult, type VariableValue } from "../../api/query";
import type { FilterClause } from "../../types/explorer";
import type { ResolvedRange } from "./dateRange";
import { globalFilterClauses } from "./dashboardFilters";
import { runnableQuery, withoutChartOptions } from "./formulaQueries";

/** Runs an extra query for a widget (a total row, a trend, the range before); null = none. */
export function useQueryRun(query: AggregationQuery | null, refreshToken: number): QueryResult | null {
  const [result, setResult] = useState<QueryResult | null>(null);
  const key = JSON.stringify([query, refreshToken]);
  useEffect(() => {
    if (!query) {
      setResult(null);
      return;
    }
    let cancelled = false;
    runQuery(withoutChartOptions(query))
      .then((r) => !cancelled && setResult(r))
      .catch(() => !cancelled && setResult(null));
    return () => {
      cancelled = true;
    };
    // `key` covers the query and the refresh token.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);
  return result;
}

/** The range just before: as long as the given one, ending where it starts. */
export function previousRange(range: ResolvedRange): ResolvedRange | null {
  if (!range.from || !range.to) return null;
  const from = Date.parse(`${range.from}T00:00:00Z`);
  const to = Date.parse(`${range.to}T00:00:00Z`);
  const iso = (ms: number) => new Date(ms).toISOString().slice(0, 10);
  return { ...range, from: iso(from - (to - from)), to: range.from };
}

/** The column a key figure's range applies to: chosen, else its date group. */
export function kpiDateColumn(widget: DashboardWidget): string | null {
  const data = widget.data;
  if (!data) return null;
  if (data.dateColumn !== undefined) return data.dateColumn;
  return data.query?.groupBy?.find((g) => g.bucket)?.column ?? null;
}

/** The extra queries of a key figure: the range before (whole range) and its trend per month. */
export function kpiExtraQueries(
  widget: DashboardWidget,
  range: ResolvedRange,
  variables: Record<string, VariableValue>,
): { previous: AggregationQuery | null; trend: AggregationQuery | null } {
  const query = widget.data?.query;
  if (widget.type !== "kpi" || !query || !widget.data) return { previous: null, trend: null };
  const latestPeriod = (query.groupBy ?? []).some((g) => g.bucket);
  const dateColumn = kpiDateColumn(widget);
  const before = previousRange(range);
  const previous =
    !latestPeriod && widget.display?.compare !== false && dateColumn && before
      ? runnableQuery(query, { ...widget.data, dateColumn }, before, variables)
      : null;
  const trend =
    widget.display?.trend && !latestPeriod && dateColumn
      ? runnableQuery({ ...query, groupBy: [{ column: dateColumn, bucket: "month", as: "period" }] }, { ...widget.data, dateColumn }, range, variables)
      : null;
  return { previous, trend };
}

/** Query filters (`{ amount: { lt: 0 } }`) as the list endpoint's filter clauses. */
export function toFilterClauses(filter: QueryFilter | undefined): FilterClause[] {
  const clauses: FilterClause[] = [];
  for (const [column, condition] of Object.entries(filter ?? {})) {
    if (condition !== null && typeof condition === "object" && !Array.isArray(condition)) {
      for (const [op, value] of Object.entries(condition as Record<string, unknown>)) {
        clauses.push({ column, op: op as FilterClause["op"], value: Array.isArray(value) ? value.join(",") : value === true ? "" : String(value ?? "") });
      }
    } else {
      clauses.push(condition === null ? { column, op: "null", value: "" } : { column, op: "eq", value: String(condition) });
    }
  }
  return clauses;
}

export type RowsState = { status: "loading" | "ready" | "error"; rows: Record<string, unknown>[]; total: number; error: string | null };

/** A table of individual rows: the list endpoint with the widget's conditions and the dashboard range. */
export function useRows(widget: DashboardWidget, range: ResolvedRange, refreshToken: number): RowsState {
  const [state, setState] = useState<RowsState>({ status: "loading", rows: [], total: 0, error: null });
  const rows = widget.data?.rows;
  const source = widget.data?.query?.source;
  const key = JSON.stringify([rows, source, widget.data?.query?.filter, widget.data?.dateColumn, range, refreshToken]);
  useEffect(() => {
    if (!rows || !source) return;
    let cancelled = false;
    const filters = toFilterClauses(widget.data?.query?.filter);
    const dateColumn = widget.data?.dateColumn;
    if (dateColumn && range.from) filters.push({ column: dateColumn, op: "gte", value: range.from });
    if (dateColumn && range.to) filters.push({ column: dateColumn, op: "lt", value: range.to });
    filters.push(...globalFilterClauses(source, range));
    const sort = rows.sort ? [{ column: rows.sort.replace(/^-/, ""), dir: rows.sort.startsWith("-") ? ("desc" as const) : ("asc" as const) }] : [];
    fetchTableRows(source, { page: 1, perPage: Math.min(rows.limit ?? 25, 1000), sort, search: "", filters })
      .then((response) => !cancelled && setState({ status: "ready", rows: response.data, total: response.meta.total, error: null }))
      .catch((cause) => !cancelled && setState({ status: "error", rows: [], total: 0, error: cause instanceof Error ? cause.message : "The rows could not be loaded." }));
    return () => {
      cancelled = true;
    };
    // `key` covers the settings, the range and the refresh token.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);
  return state;
}
