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

const CHARTS = new Set(["line", "area", "bar"]);
const PARAM = "interval.";

/** The intervals a widget can switch between: its own and month, quarter, year — none for widgets without a date group. */
export function intervalOptions(widget: DashboardWidget): QueryBucket[] {
  const bucket = widget.data?.query?.groupBy?.find((g) => g.bucket)?.bucket;
  if (!CHARTS.has(widget.type) || !bucket) return [];
  const options: QueryBucket[] = bucket === "day" || bucket === "week" ? [bucket] : [];
  return [...options, "month", "quarter", "year"];
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
