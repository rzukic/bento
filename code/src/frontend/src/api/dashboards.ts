import { apiClient } from "./client";
import type { AggregationQuery } from "./query";

export type WidgetType = "kpi" | "line" | "area" | "bar" | "donut" | "sankey" | "table" | "text";

/** levels: flows between consecutive groups; cashflow: income categories → income → expense categories (and saved). */
export type SankeyMode = "levels" | "cashflow";

export type WidgetPosition = { x: number; y: number; w: number; h: number };

/** Where a widget's numbers come from: its own query or a saved one. */
export type WidgetData = {
  query?: AggregationQuery;
  savedQueryId?: number;
  /** The date column the dashboard's date range filters; none = the widget ignores the range. */
  dateColumn?: string | null;
  /** The value at the end of each period of the date group (the range sets the periods). */
  asOf?: boolean;
  /** KPI: show a dashboard variable instead of a query. */
  variable?: string;
  /** Table: the rows as they are (no grouping) — these columns, sorted and limited. */
  rows?: WidgetRows;
  /** Charts by date: the same measures over the period before or the same period a year earlier, as dashed lines. */
  timeShift?: TimeShift;
};

export type TimeShift = "previous" | "year";

/** A horizontal line at a number or a dashboard variable's value. */
export type ReferenceLine = { value: number | string; label?: string };

export type WidgetRows = {
  columns: string[];
  /** A column name; "-" in front sorts descending. */
  sort?: string;
  limit?: number;
};

/**
 * A dashboard variable (Part 9 R9.10): a measure formula over a table's rows
 * (`SUM(amount WHERE amount < 0)`) or, without a table, over other variables
 * (`cash / burn_rate`). Usable in KPI cards and in widget formulas by name.
 */
export type DashboardVariable = {
  name: string;
  label?: string;
  source?: string;
  formula: string;
  /** The date column the dashboard's range filters; none = all rows. */
  dateColumn?: string;
};

export type WidgetDisplay = {
  /** auto follows the result column's type (currency → amount). */
  format?: "auto" | "currency" | "number" | "integer" | "percent";
  stacked?: boolean;
  /** Text widget content (headings with #, lists with -). */
  text?: string;
  /** KPI: compare with the previous date bucket (latest period) or the range before (whole range). */
  compare?: boolean;
  /** KPI: green when the value goes up (default); off for costs. */
  higherIsBetter?: boolean;
  /** KPI: a small trend line of the value per period. */
  trend?: boolean;
  /** KPI: a goal — a number or a dashboard variable's name — shown as progress. */
  goal?: number | string;
  /** Table: a total row. */
  summary?: boolean;
  /** Stacked charts: a line with the total (the net with mixed signs) on top. */
  totalLine?: boolean;
  /** Charts: horizontal lines, e.g. a target or a budget. */
  referenceLines?: ReferenceLine[];
  /** One-series charts: positive values in the success colour, negative ones in the danger colour. */
  colorBySign?: boolean;
  /** Charts by date: false hides the dashboard's annotations. */
  annotations?: boolean;
  /** Sankey: how the groups become flows (default: levels with two or more groups, else cash flow). */
  sankey?: SankeyMode;
  /** Sankey: node labels as shares (%) of the first column instead of amounts. */
  shares?: boolean;
};

export const ANNOTATION_COLORS = ["gray", "blue", "teal", "amber", "rose", "violet"] as const;
export type AnnotationColor = (typeof ANNOTATION_COLORS)[number];

/** An event on the dashboard's charts by date: a day ("Salary raise") or, with `to`, a period ("Vacation"). */
export type DashboardAnnotation = {
  date: string;
  /** Last day of a period, inclusive. */
  to?: string;
  label: string;
  color?: AnnotationColor;
};

export type DashboardWidget = {
  id: string;
  type: WidgetType;
  title: string;
  position: WidgetPosition;
  data?: WidgetData;
  display?: WidgetDisplay;
};

export type DateRangePreset =
  | "last-7-days"
  | "this-month"
  | "last-3-months"
  | "last-6-months"
  | "last-12-months"
  | "this-year"
  | "last-year"
  | "last-3-years"
  | "last-5-years"
  | "all";

export type DashboardDateRange = { preset: DateRangePreset } | { from: string; to: string };

/** Dashboard JSON (Part 9 R9.8). */
export type DashboardDocument = {
  description?: string;
  dateRange?: DashboardDateRange;
  /** Seconds between automatic refreshes; 0 or missing = off. */
  refreshSeconds?: number;
  widgets: DashboardWidget[];
  variables?: DashboardVariable[];
  annotations?: DashboardAnnotation[];
};

export type Dashboard = {
  id: number;
  name: string;
  isShared: boolean;
  isOwner: boolean;
  dashboard: DashboardDocument;
  createdAt: string;
  updatedAt: string;
};

type DataEnvelope<T> = { data: T };

export async function listDashboards(): Promise<Dashboard[]> {
  return (await apiClient.get<DataEnvelope<Dashboard[]>>("/dashboards")).data;
}

export async function getDashboard(id: number): Promise<Dashboard> {
  return (await apiClient.get<DataEnvelope<Dashboard>>(`/dashboards/${id}`)).data;
}

export async function createDashboard(name: string, dashboard: DashboardDocument): Promise<Dashboard> {
  return (await apiClient.post<DataEnvelope<Dashboard>>("/dashboards", { name, dashboard })).data;
}

export async function updateDashboard(
  id: number,
  changes: { name?: string; isShared?: boolean; dashboard?: DashboardDocument },
): Promise<Dashboard> {
  return (await apiClient.put<DataEnvelope<Dashboard>>(`/dashboards/${id}`, changes)).data;
}

export async function deleteDashboard(id: number): Promise<void> {
  await apiClient.delete<void>(`/dashboards/${id}`);
}
