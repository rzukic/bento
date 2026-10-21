import type { TargetConfig } from "../../api/dashboards";
import type { AggregationQuery, QueryResult } from "../../api/query";
import { nextPeriodStart, periodStart, quarterName, todayIso, yearName } from "../../lib/calendar";
import { valueTypeOf } from "../../lib/columns";
import { toNumber } from "../../lib/format";
import type { ModelSnapshot, ModelTable } from "../../types/api";
import { getReferenceTableName } from "../data-explorer/components/editors/referenceMetadata";
import type { ResolvedRange } from "./dateRange";
import { globalFilterFor, mergeFilters } from "./dashboardFilters";
import { rangeFilter } from "./widgetData";

// Targets: a value per group (any formula over any table's rows) against each
// group's target, a number column of the table the group links to — hours per
// project, orders per region, spending per category. A limit is to stay under,
// a goal to reach.

const NUMERIC = new Set(["currency", "float", "integer", "decimal", "double"]);
const TARGET = /target|goal|limit|quota|plan|budget|cap|soll|ziel/i;

export type TargetPer = NonNullable<TargetConfig["per"]>;

/** The table the group column links to: it holds the targets. */
export function targetTable(config: Pick<TargetConfig, "source" | "group">, snapshot: ModelSnapshot | null): ModelTable | undefined {
  const source = snapshot?.tables.find((t) => t.name === config.source);
  const column = source?.columns.find((c) => c.name === config.group);
  const linked = column ? getReferenceTableName(column) : null;
  return snapshot?.tables.find((t) => t.name === linked);
}

/** Number columns of the linked table that can hold a target; one named like a target first. */
export function targetColumns(table: ModelTable | undefined) {
  const numbers = (table?.columns ?? []).filter((c) => !c.primaryKey && NUMERIC.has(valueTypeOf(c)));
  const named = (c: { name: string; displayName?: string | null }) => TARGET.test(`${c.name} ${c.displayName ?? ""}`);
  return [...numbers.filter(named), ...numbers.filter((c) => !named(c))];
}

/** A first value formula for a table: the sum of its first number column, else the count of rows. */
export function defaultValue(table: ModelTable | undefined): string {
  const number = (table?.columns ?? []).find((c) => !c.primaryKey && NUMERIC.has(valueTypeOf(c)) && c.type !== "reference");
  return number ? `SUM(${number.name})` : "COUNT()";
}

/**
 * A first configuration from the tables: one with a date and a link to a
 * table that has a number column for the targets.
 */
export function defaultTarget(tables: ModelTable[], snapshot: ModelSnapshot | null): TargetConfig | undefined {
  for (const table of tables) {
    const date = table.columns.find((c) => ["date", "datetime"].includes(valueTypeOf(c)));
    if (!date) continue;
    for (const link of table.columns.filter((c) => c.type === "reference")) {
      const config = { source: table.name, group: link.name };
      const column = targetColumns(targetTable(config, snapshot))[0];
      if (!column) continue;
      return { ...config, value: defaultValue(table), date: date.name, target: column.name, per: "month", period: "current", kind: "goal" };
    }
  }
  return undefined;
}

/** "October 2026", "Week of 5 Oct 2026", "Q3 2026", "2026/27". */
export function periodName(start: string, per: TargetPer, locale?: string): string {
  const [y, m, d] = start.split("-").map(Number);
  const date = new Date(Date.UTC(y!, m! - 1, d));
  switch (per) {
    case "week":
      return `Week of ${new Intl.DateTimeFormat(locale, { day: "numeric", month: "short", year: "numeric", timeZone: "UTC" }).format(date)}`;
    case "quarter":
      return quarterName(start);
    case "year":
      return yearName(start);
    default:
      return new Intl.DateTimeFormat(locale, { month: "long", year: "numeric", timeZone: "UTC" }).format(date);
  }
}

/**
 * The period a target widget compares: the week, month, quarter or year at
 * the end of the dashboard's range (default; today's for an open range), or
 * the whole range with the number of those periods it touches (to is exclusive).
 */
export function targetPeriod(
  range: ResolvedRange,
  per: TargetPer = "month",
  period: TargetConfig["period"] = "current",
  today = new Date(),
): { from: string; to: string; count: number; start: string | null } {
  // Ranges stop at today unless they reach ahead on purpose: in a range of whole months, "current" is this week, not the month's last.
  const end = range.to ? isoBefore(range.to) : todayIso(today);
  const lastDay = !range.future && end > todayIso(today) ? todayIso(today) : end;
  if (period !== "range" || !range.from || !range.to) {
    const start = periodStart(lastDay, per);
    return { from: start, to: nextPeriodStart(start, per), count: 1, start };
  }
  let count = 0;
  for (let start = periodStart(range.from, per); start < range.to && count < 1000; start = nextPeriodStart(start, per)) count++;
  return { from: range.from, to: range.to, count: Math.max(1, count), start: null };
}

function isoBefore(value: string): string {
  const [y, m, d] = value.split("-").map(Number);
  return new Date(Date.UTC(y!, m! - 1, d! - 1)).toISOString().slice(0, 10);
}

/** The value per group in the period. */
export function valueQuery(config: TargetConfig, range: ResolvedRange): AggregationQuery {
  const { from, to } = targetPeriod(range, config.per, config.period);
  return {
    source: config.source,
    filter: mergeFilters(rangeFilter(config.date, { ...range, from, to }), globalFilterFor(config.source, range)),
    groupBy: [{ column: config.group }],
    measures: [{ name: "value", formula: config.value }],
  };
}

export type TargetRow = { id: unknown; label: string; value: number; target: number; share: number };

/**
 * One row per group with a target (times the periods of the range), the
 * furthest along first; groups with a value but no target are summed apart.
 */
export function targetRows(
  result: QueryResult | null,
  groups: Record<string, unknown>[],
  config: TargetConfig,
  displayColumn: string,
  keyColumn: string,
  count: number,
): { rows: TargetRow[]; untargeted: { value: number; count: number }; total: { value: number; target: number } } {
  const valueBy = new Map<string, number>();
  for (const row of result?.data ?? []) valueBy.set(String(row[config.group] ?? ""), toNumber(row.value) ?? 0);
  const rows: TargetRow[] = [];
  const seen = new Set<string>();
  for (const group of groups) {
    const key = String(group[keyColumn]);
    const each = toNumber(group[config.target] as never);
    if (each === null || each <= 0) continue;
    seen.add(key);
    const target = each * count;
    const value = valueBy.get(key) ?? 0;
    rows.push({ id: group[keyColumn], label: String(group[displayColumn] ?? key), value, target, share: value / target });
  }
  rows.sort((a, b) => b.share - a.share || a.label.localeCompare(b.label));
  let untargetedValue = 0;
  let untargetedCount = 0;
  for (const [key, value] of valueBy) {
    if (seen.has(key) || value === 0) continue;
    untargetedValue += value;
    untargetedCount++;
  }
  const total = rows.reduce((sum, row) => ({ value: sum.value + row.value, target: sum.target + row.target }), { value: 0, target: 0 });
  return { rows, untargeted: { value: untargetedValue, count: untargetedCount }, total };
}

/** A limit: under 80 % fine, up to 100 % close, above over. A goal: in progress until reached. */
export function targetState(share: number, kind: TargetConfig["kind"] = "limit"): "ok" | "close" | "over" | "done" {
  if (kind === "goal") return share >= 1 ? "done" : "ok";
  if (share > 1) return "over";
  return share >= 0.8 ? "close" : "ok";
}
