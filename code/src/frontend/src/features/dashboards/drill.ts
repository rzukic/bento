import type { WidgetData } from "../../api/dashboards";
import { buildQueryString } from "../../api/data";
import type { AggregationQuery, QueryBucket } from "../../api/query";
import { DEFAULT_QUERY, type FilterClause } from "../../types/explorer";
import type { ResolvedRange } from "./dateRange";
import { globalFilterClauses } from "./dashboardFilters";
import { toFilterClauses } from "./useExtraQueries";
import { isRunningMeasure, nextBucket } from "./widgetData";

// Step 3.7 (Part 9 R9.11): clicking a bar, a point, a slice, a key figure or a
// Sankey flow opens the Data Explorer on the widget's table with the rows
// behind it — the widget's conditions, the dashboard's range and the
// clicked group values.

/** One clicked group value: a date period (with its bucket) or a value of a column. */
export type GroupPick = { column: string; bucket?: QueryBucket; value: unknown; other?: boolean };

export type DrillTarget = { table: string; filters: FilterClause[] };

/**
 * The explorer filters for a click, or null when the rows cannot be listed
 * exactly ("Other" is every value outside the top values).
 */
export function drillTarget(
  query: AggregationQuery | null | undefined,
  data: WidgetData | undefined,
  range: ResolvedRange,
  picks: GroupPick[],
  extra: FilterClause[] = [],
): DrillTarget | null {
  if (!query || picks.some((p) => p.other)) return null;
  const clauses: FilterClause[] = [...toFilterClauses(query.filter)];
  const dateGroup = query.groupBy?.find((g) => g.bucket);
  const dateColumn = data?.dateColumn !== undefined ? data.dateColumn : (dateGroup?.column ?? null);
  // Values at the end of each period and running totals count every row up to
  // the period's end — from the range's start, or from the beginning with carry-in.
  const measures = query.measures ?? [];
  const cumulative = Boolean(data?.asOf) || measures.some(isRunningMeasure);
  const fromStart = Boolean(data?.asOf) || measures.some((m) => m.carryIn);
  if (dateColumn) {
    if (range.from && !fromStart) clauses.push({ column: dateColumn, op: "gte", value: range.from });
    if (range.to && !data?.asOf) clauses.push({ column: dateColumn, op: "lt", value: range.to });
  }
  for (const p of picks) {
    if (p.bucket && typeof p.value === "string") {
      // The period, kept inside the dashboard's range (a range may end mid-month).
      const from = range.from && range.from > p.value ? range.from : p.value;
      const next = nextBucket(p.value, p.bucket);
      const to = range.to && !data?.asOf && range.to < next ? range.to : next;
      if (!cumulative) clauses.push({ column: p.column, op: "gte", value: from });
      clauses.push({ column: p.column, op: "lt", value: to });
    } else if (p.value === null || p.value === undefined || p.value === "") {
      clauses.push({ column: p.column, op: "null" });
    } else {
      clauses.push({ column: p.column, op: "eq", value: String(p.value) });
    }
  }
  clauses.push(...globalFilterClauses(query.source, range), ...extra);
  // One condition per column and operator (the explorer's URL holds one): the narrower, later one wins.
  const filters = clauses.filter((c, i) => !clauses.slice(i + 1).some((later) => later.column === c.column && later.op === c.op));
  return { table: query.source, filters };
}

/** The explorer URL for a drill target. */
export function drillUrl(target: DrillTarget): string {
  return `/data/${encodeURIComponent(target.table)}?${buildQueryString({ ...DEFAULT_QUERY, filters: target.filters })}`;
}

/** Cash flow Sankeys: income rows are the measure's column above 0, expenses below. */
export function signFilter(query: AggregationQuery | null | undefined, sign: "positive" | "negative" | undefined): FilterClause[] {
  const measure = query?.measures?.[0];
  if (!sign || !measure?.column || measure.formula !== undefined || (measure.fn !== "sum" && measure.fn !== undefined)) return [];
  return [{ column: measure.column, op: sign === "positive" ? "gt" : "lt", value: "0" }];
}
