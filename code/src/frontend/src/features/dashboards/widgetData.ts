import type { DashboardWidget } from "../../api/dashboards";
import type { AggregationQuery, QueryBucket, QueryFilter, QueryResult, QueryRow } from "../../api/query";
import { formatKindForType, toNumber, type NumberFormatKind } from "../../lib/format";
import type { ResolvedRange } from "./dateRange";

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
  const filter: QueryFilter = { ...(query.filter ?? {}) };
  for (const [column, condition] of Object.entries(extra)) {
    const existing = filter[column];
    filter[column] =
      existing && typeof existing === "object" && !Array.isArray(existing) && typeof condition === "object"
        ? { ...(existing as object), ...(condition as object) }
        : condition;
  }
  return { ...query, filter };
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

function nextBucket(value: string, bucket: QueryBucket): string {
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

export type ChartSeries = { name: string; values: (number | null)[] };

export type ChartModel = {
  categories: string[];
  /** Date bucket of the x axis, for labels. */
  bucket?: QueryBucket;
  series: ChartSeries[];
  format: NumberFormatKind;
};

function labelOf(row: QueryRow, key: string, labelKey: string | undefined): string {
  const label = labelKey ? row[labelKey] : null;
  const value = label ?? row[key];
  return value === null || value === undefined || value === "" ? "(none)" : String(value);
}

/**
 * Turns a query result into categories and series for a chart. The first
 * group is the x axis; a second group splits the first measure into one
 * series per value (e.g. spending per category per month). Date axes get
 * every bucket of the range; a running total carries over empty buckets,
 * other measures count them as 0.
 */
export function toChartModel(
  result: QueryResult,
  query: AggregationQuery | undefined,
  range: ResolvedRange,
  formatOverride?: NumberFormatKind,
  today: Date = new Date(),
): ChartModel {
  const groups = result.meta.columns.filter((c) => c.role === "group");
  const measures = result.meta.columns.filter((c) => c.role === "measure");
  const labelFor = (group: string) => result.meta.columns.find((c) => c.role === "label" && c.labelOf === group)?.name;
  const format = formatOverride ?? formatKindForType(measures[0]?.type);
  const running = new Set((query?.measures ?? []).filter((m) => m.running).map((m) => m.name));

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
  const bucket = query?.groupBy?.find((g) => (g.as ?? g.column) === x.name)?.bucket;

  let categories: string[];
  if (bucket) {
    const present = result.data.map((row) => String(row[x.name] ?? "")).filter(Boolean).sort();
    const first = range.from ?? present[0];
    // The range's last day, but not past today: "This year" in October
    // should not draw November and December.
    const todayIso = toIso(new Date(Date.UTC(today.getFullYear(), today.getMonth(), today.getDate())));
    const rangeEnd = range.to ? toIso(new Date(parseIsoDate(range.to).getTime() - 86_400_000)) : null;
    const lastDay = rangeEnd ? (rangeEnd < todayIso ? rangeEnd : todayIso) : present[present.length - 1];
    categories = first && lastDay && first <= lastDay ? bucketSequence(first, lastDay, bucket) : [];
    // Data outside the range (no date filter on the widget) still shows.
    for (const value of present) if (!categories.includes(value)) categories.push(value);
    categories.sort();
  } else {
    categories = [];
    for (const row of result.data) {
      const label = labelOf(row, x.name, xLabel);
      if (!categories.includes(label)) categories.push(label);
    }
  }

  const keyOf = (row: QueryRow) => (bucket ? String(row[x.name] ?? "") : labelOf(row, x.name, xLabel));

  const fill = (values: (number | null)[], isRunning: boolean): (number | null)[] => {
    if (!bucket) return values;
    let last: number | null = null;
    return values.map((value) => {
      if (value !== null) {
        last = value;
        return value;
      }
      // Before the first value a running total is unknown; afterwards it holds.
      return isRunning ? last : 0;
    });
  };

  if (groups.length >= 2 && measures.length >= 1) {
    const split = groups[1];
    const splitLabel = labelFor(split.name);
    const measure = measures[0];
    const names: string[] = [];
    for (const row of result.data) {
      const name = labelOf(row, split.name, splitLabel);
      if (!names.includes(name)) names.push(name);
    }
    const series = names.map((name) => {
      const values = categories.map((category) => {
        const row = result.data.find((r) => keyOf(r) === category && labelOf(r, split.name, splitLabel) === name);
        return row ? toNumber(row[measure.name]) : null;
      });
      return { name, values: fill(values, running.has(measure.name)) };
    });
    return { categories, bucket, series, format };
  }

  const series = measures.map((measure) => {
    const values = categories.map((category) => {
      const row = result.data.find((r) => keyOf(r) === category);
      return row ? toNumber(row[measure.name]) : null;
    });
    return { name: measure.name, values: fill(values, running.has(measure.name)) };
  });
  return { categories, bucket, series, format };
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
