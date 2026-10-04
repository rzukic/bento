import type { DashboardVariable, WidgetData } from "../../api/dashboards";
import type { AggregationQuery, QueryVariableResult, VariableValue } from "../../api/query";
import { globalFilterFor, mergeFilters } from "./dashboardFilters";
import type { ResolvedRange } from "./dateRange";
import { rangeFilter, withFilter } from "./widgetData";

/** Variable results by name, as the dashboard evaluated them. */
export type VariableResults = Record<string, QueryVariableResult>;

function isoDay(date: Date): string {
  return date.toISOString().slice(0, 10);
}

/**
 * The periods of a "value at the end of each period" widget: the dashboard's
 * range (its last day, not past today); an open range starts a year back.
 */
export function asOfPeriod(range: ResolvedRange, today = new Date()): { from: string; to: string } {
  const todayIso = isoDay(new Date(Date.UTC(today.getFullYear(), today.getMonth(), today.getDate())));
  const lastDay = range.to ? isoDay(new Date(Date.parse(`${range.to}T00:00:00Z`) - 86_400_000)) : todayIso;
  const to = lastDay < todayIso ? lastDay : todayIso;
  const from = range.from ?? `${Number(to.slice(0, 4)) - 1}${to.slice(4, 8)}01`;
  return { from: from <= to ? from : to, to };
}

/** Values of the variables that have one, for formula measures. */
export function variableValues(results: VariableResults | undefined): Record<string, VariableValue> {
  const values: Record<string, VariableValue> = {};
  for (const result of Object.values(results ?? {})) {
    if (result.error === null) values[result.name] = result.value;
  }
  return values;
}

/** A measure's chart-only options ("show as", colour) never reach the API. */
export function withoutChartOptions(query: AggregationQuery): AggregationQuery {
  if (!(query.measures ?? []).some((m) => m.show !== undefined || m.window !== undefined || m.color !== undefined)) return query;
  return { ...query, measures: query.measures?.map(({ show: _show, window: _window, color: _color, ...measure }) => measure) };
}

/**
 * The query a widget runs: the dashboard's range as a filter on its date
 * column — or, for values at the end of each period, as the calendar of
 * periods instead — and the dashboard's variables for its formulas.
 */
export function runnableQuery(
  query: AggregationQuery,
  data: WidgetData,
  range: ResolvedRange,
  variables: Record<string, VariableValue>,
  today = new Date(),
): AggregationQuery {
  const withVariables = (next: AggregationQuery): AggregationQuery =>
    withoutChartOptions((next.measures ?? []).some((m) => m.formula) && Object.keys(variables).length > 0 ? { ...next, variables } : next);

  const dateGroup = query.groupBy?.find((g) => g.bucket);
  if (data.asOf && dateGroup?.bucket) {
    return withVariables({
      ...withFilter(query, globalFilterFor(query.source, range)),
      groupBy: query.groupBy?.filter((g) => g !== dateGroup),
      asOf: { bucket: dateGroup.bucket, ...asOfPeriod(range, today), as: dateGroup.as ?? dateGroup.column },
    });
  }

  const dateColumn = data.dateColumn !== undefined ? data.dateColumn : (dateGroup?.column ?? null);
  return withVariables(withFilter(withFilter(query, rangeFilter(dateColumn, range)), globalFilterFor(query.source, range)));
}

/** The variables with the dashboard's range on their date column. */
export function variableRequests(variables: DashboardVariable[], range: ResolvedRange) {
  return variables.map((variable) => ({
    name: variable.name,
    formula: variable.formula,
    ...(variable.source ? { source: variable.source } : {}),
    // The range on its date column, and the dashboard's filters on its table.
    ...(variable.source && (variable.dateColumn || globalFilterFor(variable.source, range))
      ? { filter: mergeFilters(variable.dateColumn ? rangeFilter(variable.dateColumn, range) : undefined, globalFilterFor(variable.source, range)) }
      : {}),
  }));
}
