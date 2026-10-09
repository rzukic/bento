import type { DashboardWidget } from "../../api/dashboards";
import type { QueryBucket } from "../../api/query";

// Step 4.5: viewers switch a chart by date between months, quarters and
// years without editing it. The choice lives in the address
// (`?interval.<widget>=quarter`), like the range and the filters.

export const INTERVAL_LABELS: Record<QueryBucket, { short: string; long: string }> = {
  day: { short: "D", long: "Day" },
  week: { short: "W", long: "Week" },
  month: { short: "M", long: "Month" },
  quarter: { short: "Q", long: "Quarter" },
  year: { short: "Y", long: "Year" },
};

const SWITCHABLE = new Set(["line", "area", "bar", "table"]);
const PARAM = "interval.";
export const ALL_INTERVALS: QueryBucket[] = ["day", "week", "month", "quarter", "year"];

/** The widget's own interval (its date group's bucket), if it can have a switch at all: charts and summarized tables by date. */
export function ownInterval(widget: DashboardWidget): QueryBucket | undefined {
  if (!SWITCHABLE.has(widget.type) || widget.data?.rows) return undefined;
  return widget.data?.query?.groupBy?.find((g) => g.bucket)?.bucket;
}

/** Without a setting: its own interval and month, quarter, year. */
export function defaultIntervals(bucket: QueryBucket): QueryBucket[] {
  const options: QueryBucket[] = bucket === "day" || bucket === "week" ? [bucket] : [];
  return [...options, "month", "quarter", "year"];
}

/**
 * The intervals viewers can switch the widget between — none for widgets
 * without a date group or with the switch turned off (display.intervals: false);
 * a chosen list always keeps the widget's own interval.
 */
export function intervalOptions(widget: DashboardWidget): QueryBucket[] {
  const bucket = ownInterval(widget);
  const setting = widget.display?.intervals;
  if (!bucket || setting === false) return [];
  if (!setting) return defaultIntervals(bucket);
  const chosen = ALL_INTERVALS.filter((b) => b === bucket || setting.includes(b));
  return chosen.length > 1 ? chosen : [];
}

/** The widget with its date group in another interval. */
export function withInterval(widget: DashboardWidget, bucket: QueryBucket | undefined): DashboardWidget {
  const query = widget.data?.query;
  if (!bucket || !query?.groupBy || !intervalOptions(widget).includes(bucket)) return widget;
  return { ...widget, data: { ...widget.data, query: { ...query, groupBy: query.groupBy.map((g) => (g.bucket ? { ...g, bucket } : g)) } } };
}

export function intervalsFromSearch(params: URLSearchParams): Record<string, QueryBucket> {
  const result: Record<string, QueryBucket> = {};
  params.forEach((value, key) => {
    if (key.startsWith(PARAM) && value in INTERVAL_LABELS) result[key.slice(PARAM.length)] = value as QueryBucket;
  });
  return result;
}

/** The address with this widget's interval; the widget's own interval removes the parameter. */
export function intervalToSearch(widget: DashboardWidget, bucket: QueryBucket, params: URLSearchParams): URLSearchParams {
  const next = new URLSearchParams(params);
  const own = widget.data?.query?.groupBy?.find((g) => g.bucket)?.bucket;
  if (bucket === own) next.delete(PARAM + widget.id);
  else next.set(PARAM + widget.id, bucket);
  return next;
}
