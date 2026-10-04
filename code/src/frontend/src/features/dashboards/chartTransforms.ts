import type { AnnotationColor, DashboardAnnotation, ReferenceLine, TimeShift } from "../../api/dashboards";
import type { QueryMeasure, VariableValue } from "../../api/query";
import type { ResolvedRange } from "./dateRange";
import { previousRange } from "./useExtraQueries";
import { bucketStart, type ChartModel } from "./widgetData";

// Step 3.6c: what a chart computes from its result in the browser — the
// change from the period before, moving averages, an earlier period to
// compare with, the total of a stack and reference lines.

export const DEFAULT_WINDOW = 3;

/** value − the value of the period before; the first period has nothing to compare with. */
export function changes(values: (number | null)[]): (number | null)[] {
  return values.map((value, i) => {
    const before = i > 0 ? values[i - 1] : null;
    return value === null || before === null ? null : value - before;
  });
}

/**
 * The average of the last `window` periods, this one included; empty until
 * there are that many (an average of fewer would mislead at the start).
 */
export function movingAverage(values: (number | null)[], window: number): (number | null)[] {
  const size = Math.max(1, Math.round(window));
  return values.map((_, i) => {
    if (i + 1 < size) return null;
    const slice = values.slice(i + 1 - size, i + 1);
    if (slice.some((value) => value === null)) return null;
    return (slice as number[]).reduce((sum, value) => sum + value, 0) / size;
  });
}

/** Applies each measure's "show as" to its series — every series of the first measure when split by a breakdown. */
export function applyMeasureShows(model: ChartModel, measures: QueryMeasure[], hasBreakdown: boolean): ChartModel {
  if (!model.bucket) return model;
  const transform = (measure: QueryMeasure | undefined, values: (number | null)[]) =>
    measure?.show === "change" ? changes(values) : measure?.show === "moving_average" ? movingAverage(values, measure.window ?? DEFAULT_WINDOW) : values;
  return {
    ...model,
    series: model.series.map((series) => {
      const measure = hasBreakdown ? measures[0] : measures.find((m) => m.name === series.name);
      return measure?.show ? { ...series, values: transform(measure, series.values) } : series;
    }),
  };
}

function addMonths(isoDate: string, months: number): string {
  const [y, m, d] = isoDate.split("-").map(Number);
  const first = new Date(Date.UTC(y, m - 1 + months, 1));
  const lastDay = new Date(Date.UTC(first.getUTCFullYear(), first.getUTCMonth() + 1, 0)).getUTCDate();
  return new Date(Date.UTC(first.getUTCFullYear(), first.getUTCMonth(), Math.min(d, lastDay))).toISOString().slice(0, 10);
}

/** Whole months between two first-of-month dates; null for other ranges. */
function wholeMonths(from: string, to: string): number | null {
  if (!from.endsWith("-01") || !to.endsWith("-01")) return null;
  const [fy, fm] = from.split("-").map(Number);
  const [ty, tm] = to.split("-").map(Number);
  return (ty - fy) * 12 + (tm - fm);
}

/** The range to compare with: the same period a year earlier, or the one just before (as long, ending where it starts). */
export function shiftedRange(range: ResolvedRange, shift: TimeShift): ResolvedRange | null {
  if (!range.from || !range.to) return null;
  if (shift === "year") return { from: addMonths(range.from, -12), to: addMonths(range.to, -12) };
  const months = wholeMonths(range.from, range.to);
  return months !== null && months > 0 ? { from: addMonths(range.from, -months), to: range.from } : previousRange(range);
}

export const TIME_SHIFT_LABELS: Record<TimeShift, string> = { previous: "period before", year: "a year earlier" };

/** The earlier period's series beside the current ones, matched period by period from the start. */
export function withShifted(model: ChartModel, shifted: ChartModel, shift: TimeShift): ChartModel {
  const extra = shifted.series
    .filter((series) => !series.kind)
    .map((series) => ({
      name: `${series.name} (${TIME_SHIFT_LABELS[shift]})`,
      values: model.categories.map((_, i) => series.values[i] ?? null),
      kind: "shifted" as const,
    }));
  return { ...model, series: [...model.series, ...extra] };
}

/** The total of a stack per period (empty when every value is), drawn as a line on top. */
export function withTotal(model: ChartModel): ChartModel {
  const stacked = model.series.filter((series) => !series.kind);
  if (stacked.length < 2) return model;
  const values = model.categories.map((_, i) => {
    const present = stacked.map((series) => series.values[i]).filter((value): value is number => value !== null);
    return present.length === 0 ? null : present.reduce((sum, value) => sum + value, 0);
  });
  return { ...model, series: [...model.series, { name: "Total", values, kind: "total" }] };
}

/** Reference lines with their numbers: a variable's current value, or skipped while it has none. */
export function resolveReferenceLines(lines: ReferenceLine[] | undefined, variables: Record<string, VariableValue>): { value: number; label: string }[] {
  return (lines ?? []).flatMap((line) => {
    const value = typeof line.value === "number" ? line.value : variables[line.value];
    if (typeof value !== "number" || !Number.isFinite(value)) return [];
    const label = line.label?.trim() || (typeof line.value === "string" ? line.value : "");
    return [{ value, label }];
  });
}

export type PlacedAnnotations = {
  /** One line per period; several events in the same period share it. */
  points: { key: string; label: string; color?: AnnotationColor }[];
  /** Shaded spans, cut to the periods shown. */
  ranges: { from: string; to: string; label: string; color?: AnnotationColor }[];
};

const ISO_DATE = /^\d{4}-\d{2}-\d{2}$/;

/** The annotations on a date axis: each date in its period, spans cut to the axis, the rest left out. */
export function placeAnnotations(annotations: DashboardAnnotation[] | undefined, categories: string[], bucket: ChartModel["bucket"]): PlacedAnnotations {
  const placed: PlacedAnnotations = { points: [], ranges: [] };
  if (!bucket || categories.length === 0) return placed;
  const first = categories[0];
  const last = categories[categories.length - 1];
  for (const annotation of annotations ?? []) {
    if (!ISO_DATE.test(annotation.date) || !annotation.label.trim()) continue;
    const start = bucketStart(annotation.date, bucket);
    const label = annotation.label.trim();
    if (annotation.to && ISO_DATE.test(annotation.to) && annotation.to > annotation.date) {
      const end = bucketStart(annotation.to, bucket);
      if (end < first || start > last) continue;
      placed.ranges.push({ from: start < first ? first : start, to: end > last ? last : end, label, color: annotation.color });
      continue;
    }
    if (!categories.includes(start)) continue;
    const same = placed.points.find((point) => point.key === start);
    if (same) same.label = `${same.label} · ${label}`;
    else placed.points.push({ key: start, label, color: annotation.color });
  }
  return placed;
}

/** The events of one period, for the tooltip. */
export function annotationsAt(placed: PlacedAnnotations, key: string): string[] {
  return [
    ...placed.points.filter((point) => point.key === key).map((point) => point.label),
    ...placed.ranges.filter((range) => range.from <= key && key <= range.to).map((range) => range.label),
  ];
}
