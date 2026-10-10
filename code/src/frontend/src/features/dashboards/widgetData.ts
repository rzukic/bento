import type { DashboardWidget } from "../../api/dashboards";
import type { AggregationQuery, QueryBucket, QueryFilter, QueryMeasure, QueryResult, QueryRow, SeriesColor } from "../../api/query";
import { formatKindForType, toNumber, type NumberFormatKind } from "../../lib/format";
import type { ResolvedRange } from "./dateRange";
import type { GroupPick } from "./drill";
import { mergeFilters } from "./dashboardFilters";

const RUNNING_SUM = /^\s*=?\s*RUNNING_SUM\s*\(/i;

/** A running total: the flag, or a formula wrapped in RUNNING_SUM(…). */
export function isRunningMeasure(measure: QueryMeasure): boolean {
  return Boolean(measure.running) || (typeof measure.formula === "string" && RUNNING_SUM.test(measure.formula));
}

const RETURN = /^\s*=?\s*(TWR|REBASE|XIRR|IRR)\s*\(/i;

/** A return (Step 4.3): unknown before its first value, not 0 — like a running total. */
export function isReturnMeasure(measure: QueryMeasure): boolean {
  return typeof measure.formula === "string" && RETURN.test(measure.formula);
}

/** Adds the dashboard's date range to a query's filter on `dateColumn`. */
export function rangeFilter(dateColumn: string | null | undefined, range: ResolvedRange): QueryFilter | undefined {
  if (!dateColumn || (!range.from && !range.to)) return undefined;
  const condition: Record<string, string> = {};
  if (range.from) condition.gte = range.from;
  if (range.to) condition.lt = range.to;
  return { [dateColumn]: condition };
}

export function withFilter(query: AggregationQuery, extra: QueryFilter | undefined): AggregationQuery {
  if (!extra) return query;
  return { ...query, filter: mergeFilters(query.filter, extra) };
}

/** The column a widget's date range applies to: set explicitly, else its date group. */
export function effectiveDateColumn(widget: DashboardWidget): string | null {
  const data = widget.data;
  if (!data) return null;
  if (data.dateColumn !== undefined) return data.dateColumn;
  return data.query?.groupBy?.find((g) => g.bucket)?.column ?? null;
}

// ── Date buckets ──────────────────────────────────────────────────────────

function parseIsoDate(value: string): Date {
  const [y, m, d] = value.slice(0, 10).split("-").map(Number);
  return new Date(Date.UTC(y, m - 1, d));
}

function toIso(date: Date): string {
  return date.toISOString().slice(0, 10);
}

/** The bucket a day falls into, as the backend returns it. */
export function bucketStart(value: string, bucket: QueryBucket): string {
  const date = parseIsoDate(value);
  const y = date.getUTCFullYear();
  const m = date.getUTCMonth();
  switch (bucket) {
    case "year":
      return toIso(new Date(Date.UTC(y, 0, 1)));
    case "quarter":
      return toIso(new Date(Date.UTC(y, Math.floor(m / 3) * 3, 1)));
    case "month":
      return toIso(new Date(Date.UTC(y, m, 1)));
    case "week": {
      const weekday = (date.getUTCDay() + 6) % 7;
      return toIso(new Date(Date.UTC(y, m, date.getUTCDate() - weekday)));
    }
    default:
      return toIso(date);
  }
}

export function nextBucket(value: string, bucket: QueryBucket): string {
  const date = parseIsoDate(value);
  const y = date.getUTCFullYear();
  const m = date.getUTCMonth();
  const d = date.getUTCDate();
  switch (bucket) {
    case "year":
      return toIso(new Date(Date.UTC(y + 1, 0, 1)));
    case "quarter":
      return toIso(new Date(Date.UTC(y, m + 3, 1)));
    case "month":
      return toIso(new Date(Date.UTC(y, m + 1, 1)));
    case "week":
      return toIso(new Date(Date.UTC(y, m, d + 7)));
    default:
      return toIso(new Date(Date.UTC(y, m, d + 1)));
  }
}

/** Every bucket from the one holding `first` up to the one holding `last`. */
export function bucketSequence(first: string, last: string, bucket: QueryBucket): string[] {
  const out: string[] = [];
  let current = bucketStart(first, bucket);
  const end = bucketStart(last, bucket);
  while (current <= end && out.length < 5000) {
    out.push(current);
    current = nextBucket(current, bucket);
  }
  return out;
}

// ── Chart model ───────────────────────────────────────────────────────────

export type ChartSeries = {
  name: string;
  values: (number | null)[];
  /** shifted: an earlier period (dashed, never stacked); total: the sum of the stack (a line on top). */
  kind?: "shifted" | "total";
  /** A breakdown's value, for drilling down. */
  pick?: GroupPick;
  /** The colour chosen for the measure behind it. */
  color?: SeriesColor;
  /** On the second value axis (right), written in its own format (Step 4.3). */
  axis?: "right";
  format?: NumberFormatKind;
};

export type ChartModel = {
  categories: string[];
  /** Date bucket of the x axis, for labels. */
  bucket?: QueryBucket;
  series: ChartSeries[];
  format: NumberFormatKind;
  /** Per category, the group value behind it (drill-down); null when none. */
  categoryPicks?: (GroupPick | null)[];
};

/** "Other" (the remaining top values) after the values it stands beside, in legends and on axes. */
function otherLast(names: string[], hasOther: boolean): string[] {
  return hasOther && names.includes("Other") ? [...names.filter((name) => name !== "Other"), "Other"] : names;
}

/** The name a group value shows as: its record label, "Other" for the remaining top values, "(none)" when empty. */
export function labelOf(row: QueryRow, key: string, labelKey: string | undefined, otherKey?: string): string {
  if (otherKey && row[otherKey] === true) return "Other";
  const label = labelKey ? row[labelKey] : null;
  const value = label ?? row[key];
  return value === null || value === undefined || value === "" ? "(none)" : String(value);
}

/** The flag column marking a group's "Other" row, when the query asked for one. */
export function otherKeyOf(result: QueryResult, group: string): string | undefined {
  return result.meta.columns.find((c) => c.role === "other" && c.labelOf === group)?.name;
}

/**
 * Turns a query result into categories and series for a chart. The first
 * group is the x axis; a second group splits the first measure into one
 * series per value (e.g. spending per category per month). Date axes get
 * every bucket of the range; a running total carries over empty buckets,
 * other measures count them as 0 — or, with `emptyPeriods` gap or connect,
 * leave them empty and drop the periods before the first value.
 * `stale`: the result is from before a change (1y → 3y) and the new one is
 * loading; periods it does not cover stay empty instead of showing 0.
 */
export function toChartModel(
  result: QueryResult,
  query: AggregationQuery | undefined,
  range: ResolvedRange,
  formatOverride?: NumberFormatKind,
  today: Date = new Date(),
  options: { emptyPeriods?: "zero" | "gap" | "connect"; stale?: boolean } = {},
): ChartModel {
  const empty = options.stale || (options.emptyPeriods ?? "zero") !== "zero" ? null : 0;
  const trim = !options.stale && (options.emptyPeriods ?? "zero") !== "zero";
  const groups = result.meta.columns.filter((c) => c.role === "group");
  const measures = result.meta.columns.filter((c) => c.role === "measure");
  const labelFor = (group: string) => result.meta.columns.find((c) => c.role === "label" && c.labelOf === group)?.name;
  const format = formatOverride ?? formatKindForType(measures[0]?.type);
  // Running totals and returns hold their last value over empty buckets; other measures count them as 0.
  const running = new Set((query?.measures ?? []).filter((m) => isRunningMeasure(m) || isReturnMeasure(m)).map((m) => m.name));

  if (groups.length === 0) {
    const row = result.data[0] ?? {};
    return {
      categories: measures.map((m) => m.name),
      series: [{ name: "Value", values: measures.map((m) => toNumber(row[m.name])) }],
      format,
    };
  }

  const x = groups[0];
  const xLabel = labelFor(x.name);
  const xOther = otherKeyOf(result, x.name);
  const bucket = query?.groupBy?.find((g) => (g.as ?? g.column) === x.name)?.bucket;

  let categories: string[];
  if (bucket) {
    const present = result.data.map((row) => String(row[x.name] ?? "")).filter(Boolean).sort();
    const first = range.from ?? present[0];
    // The range's last day, but not past today: "This year" in October
    // should not draw November and December — unless the range is a forecast.
    const todayIso = toIso(new Date(Date.UTC(today.getFullYear(), today.getMonth(), today.getDate())));
    const rangeEnd = range.to ? toIso(new Date(parseIsoDate(range.to).getTime() - 86_400_000)) : null;
    const lastDay = rangeEnd ? (rangeEnd < todayIso || range.future ? rangeEnd : todayIso) : present[present.length - 1];
    categories = first && lastDay && first <= lastDay ? bucketSequence(first, lastDay, bucket) : [];
    // Data outside the range (no date filter on the widget) still shows.
    for (const value of present) if (!categories.includes(value)) categories.push(value);
    categories.sort();
  } else {
    categories = [];
    for (const row of result.data) {
      const label = labelOf(row, x.name, xLabel, xOther);
      if (!categories.includes(label)) categories.push(label);
    }
    if (xOther) categories = otherLast(categories, result.data.some((row) => row[xOther] === true));
  }

  const keyOf = (row: QueryRow) => (bucket ? String(row[x.name] ?? "") : labelOf(row, x.name, xLabel, xOther));
  const columnOf = (output: string) => query?.groupBy?.find((g) => (g.as ?? g.column) === output)?.column ?? output;
  const categoryPicks: (GroupPick | null)[] = categories.map((category) => {
    if (bucket) return { column: columnOf(x.name), bucket, value: category };
    const row = result.data.find((r) => keyOf(r) === category);
    return row ? { column: columnOf(x.name), value: row[x.name] ?? null, other: xOther ? row[xOther] === true : false } : null;
  });

  const fill = (values: (number | null)[], isRunning: boolean): (number | null)[] => {
    if (!bucket) return values;
    let last: number | null = null;
    return values.map((value) => {
      if (value !== null) {
        last = value;
        return value;
      }
      // Before the first value a running total is unknown; afterwards it holds.
      return isRunning ? last : empty;
    });
  };

  if (groups.length >= 2 && measures.length >= 1) {
    const split = groups[1];
    const splitLabel = labelFor(split.name);
    const splitOther = otherKeyOf(result, split.name);
    const measure = measures[0];
    let names: string[] = [];
    for (const row of result.data) {
      const name = labelOf(row, split.name, splitLabel, splitOther);
      if (!names.includes(name)) names.push(name);
    }
    if (splitOther) names = otherLast(names, result.data.some((row) => row[splitOther] === true));
    const series = names.map((name) => {
      const values = categories.map((category) => {
        const row = result.data.find((r) => keyOf(r) === category && labelOf(r, split.name, splitLabel, splitOther) === name);
        return row ? toNumber(row[measure.name]) : null;
      });
      const first = result.data.find((r) => labelOf(r, split.name, splitLabel, splitOther) === name);
      const pick: GroupPick | undefined = first
        ? { column: columnOf(split.name), value: first[split.name] ?? null, other: splitOther ? first[splitOther] === true : false }
        : undefined;
      return { name, values: fill(values, running.has(measure.name)), pick };
    });
    return trimLeading({ categories, bucket, series, format, categoryPicks }, trim && !!bucket);
  }

  const series = measures.map((measure) => {
    const values = categories.map((category) => {
      const row = result.data.find((r) => keyOf(r) === category);
      return row ? toNumber(row[measure.name]) : null;
    });
    return { name: measure.name, values: fill(values, running.has(measure.name)) };
  });
  return trimLeading({ categories, bucket, series, format, categoryPicks }, trim && !!bucket);
}

/**
 * Drops the periods before the first value of any series, e.g. a depot over
 * three years that starts with the first purchase a year ago (its value there
 * is 0 or nothing) — instead of a line along 0.
 */
function trimLeading(model: ChartModel, trim: boolean): ChartModel {
  if (!trim) return model;
  const first = model.categories.findIndex((_, index) => model.series.some((s) => s.values[index] !== null && s.values[index] !== 0));
  if (first <= 0) return model;
  return {
    ...model,
    categories: model.categories.slice(first),
    categoryPicks: model.categoryPicks?.slice(first),
    series: model.series.map((s) => ({ ...s, values: s.values.slice(first) })),
  };
}

export type KpiModel = { value: number | null; previous: number | null; format: NumberFormatKind; label?: string };

/**
 * A KPI shows the first measure of the last row — the latest bucket when
 * grouped by date (e.g. the current balance), the only row otherwise — and
 * the row before it for the comparison.
 */
export function toKpiModel(result: QueryResult, formatOverride?: NumberFormatKind): KpiModel {
  const measure = result.meta.columns.find((c) => c.role === "measure");
  const format = formatOverride ?? formatKindForType(measure?.type);
  if (!measure || result.data.length === 0) return { value: null, previous: null, format };
  const group = result.meta.columns.find((c) => c.role === "group");
  const rows = group ? [...result.data].sort((a, b) => String(a[group.name]).localeCompare(String(b[group.name]))) : result.data;
  const last = rows[rows.length - 1];
  const previous = rows.length > 1 ? rows[rows.length - 2] : null;
  return {
    value: toNumber(last[measure.name]),
    previous: previous ? toNumber(previous[measure.name]) : null,
    format,
    label: group ? String(last[group.name] ?? "") : undefined,
  };
}
