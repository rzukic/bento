import { apiClient } from "./client";
import type { AggregationQuery, QueryBucket } from "./query";

export type WidgetType = "kpi" | "line" | "area" | "bar" | "donut" | "sankey" | "table" | "text" | "budget";

/**
 * A budget widget (Step 4.5): spending rows (e.g. transactions) linked to
 * categories, and a column there with each category's monthly limit.
 */
export type BudgetConfig = {
  source: string;
  amount: string;
  date: string;
  /** The link column to the categories table. */
  category: string;
  /** The categories table's column with the monthly limit. */
  limit: string;
  /** How the source stores spending: as negative amounts (a bank account) or positive ones (an expenses table). */
  expenses?: "negative" | "positive";
  /** "month" (default): the last month of the dashboard's range, e.g. this month; "range": the whole range, limits times its months. */
  period?: "month" | "range";
};

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
  /** Budget widgets. */
  budget?: BudgetConfig;
  /** The widget's own date range instead of the dashboard's, e.g. a forecast to 2045 on a dashboard of the last 12 months. */
  range?: DashboardDateRange;
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
  /**
   * Viewers change it on the dashboard (an assumption: a return, a savings rate);
   * `formula` is then the default value, e.g. `0.07` or `"base"`.
   */
  input?: VariableInput;
};

/** A control for a variable: a number field, a slider, or a choice of values. */
export type VariableInput = {
  kind: "number" | "slider" | "select";
  min?: number;
  max?: number;
  step?: number;
  /** select: the values to choose from. */
  options?: (string | number)[];
};

/** How a table writes one column (Step: beta feedback): a return beside amounts, +/− with colours. */
export type ColumnDisplay = {
  format?: "auto" | "currency" | "number" | "integer" | "percent";
  /** A + in front of positive values: +10 %, +1.250 €. */
  signed?: boolean;
  /** Positive values green, negative ones red. */
  colorBySign?: boolean;
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
  /** Donut (Step 4.5): a column of the same table; a slice then shows its rows grouped by it, e.g. main category → category. */
  drill?: string;
  /**
   * Charts and tables by date: the intervals viewers can switch to (M Q Y …);
   * false hides the switch. Missing: month, quarter and year (plus day or week
   * when the widget itself is by day or week).
   */
  intervals?: false | QueryBucket[];
  /**
   * Tables: a click on a row opens another dashboard, filtered by the row's value
   * of `column` (as the filter `filter` there, by default the same column name).
   */
  link?: { dashboard: number; column: string; filter?: string };
  /** Tables: per column (a measure or a row column), how it is written. */
  columns?: Record<string, ColumnDisplay>;
  /** Charts: where the value axis starts — at 0 (default), near the data (auto), or at a number. */
  yAxis?: { min?: "auto" | "zero" | number };
  /**
   * Charts by date: periods without a value count as 0 (default), leave a gap,
   * or are skipped by the line (connect). With gap and connect, periods before
   * the first value (a range reaching back before the data) are left out.
   */
  emptyPeriods?: "zero" | "gap" | "connect";
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
  | "this-week"
  | "last-week"
  | "this-month"
  | "last-month"
  | "this-quarter"
  | "last-quarter"
  | "last-3-months"
  | "last-6-months"
  | "last-12-months"
  | "this-year"
  | "last-year"
  | "last-3-years"
  | "last-5-years"
  | "next-12-months"
  | "next-5-years"
  | "next-10-years"
  | "next-20-years"
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
  /** Filters in the bar above the widgets (Part 9 R9.6): each narrows every widget whose table has the column. */
  filters?: DashboardFilter[];
  /**
   * Form buttons in the header (Step 4.5): missing or "auto" — the forms in
   * Quick add whose table the dashboard shows; a list — these forms, by id; "none" — no buttons.
   */
  quickAdd?: "auto" | "none" | number[];
};

/** A dashboard filter on a column name, e.g. `konto_id`; the chosen values live in the address. */
export type DashboardFilter = { column: string; label?: string };

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
  changes: { name?: string; isShared?: boolean; dashboard?: DashboardDocument; note?: string },
): Promise<Dashboard> {
  return (await apiClient.put<DataEnvelope<Dashboard>>(`/dashboards/${id}`, changes)).data;
}

/** An earlier state of a dashboard (Step 4.9): kept before every change, with who made it and through which app. */
export type DashboardVersion = {
  id: number;
  dashboardId: number;
  name: string;
  widgets: number;
  action: "update" | "delete" | "restore";
  changedBy: string | null;
  /** The app or key that made the change (e.g. Claude over MCP); null for a change in Bento itself. */
  via: string | null;
  note: string | null;
  createdAt: string;
};

export async function listDashboardVersions(id: number): Promise<DashboardVersion[]> {
  return (await apiClient.get<DataEnvelope<DashboardVersion[]>>(`/dashboards/${id}/versions`)).data;
}

/** Back to an earlier state; the state before is kept, so this can be undone as well. */
export async function restoreDashboardVersion(id: number, versionId: number): Promise<Dashboard> {
  return (await apiClient.post<DataEnvelope<Dashboard>>(`/dashboards/${id}/versions/${versionId}/restore`, {})).data;
}

export async function listDeletedDashboards(): Promise<DashboardVersion[]> {
  return (await apiClient.get<DataEnvelope<DashboardVersion[]>>("/dashboards/deleted")).data;
}

export async function restoreDeletedDashboard(versionId: number): Promise<Dashboard> {
  return (await apiClient.post<DataEnvelope<Dashboard>>(`/dashboards/deleted/${versionId}/restore`, {})).data;
}

/** Recent changes to the user's dashboards made by apps and keys (Claude, ChatGPT, scripts). */
export async function listDashboardChanges(limit = 30): Promise<DashboardVersion[]> {
  return (await apiClient.get<DataEnvelope<DashboardVersion[]>>(`/dashboards/changes?limit=${limit}`)).data;
}

export async function deleteDashboard(id: number): Promise<void> {
  await apiClient.delete<void>(`/dashboards/${id}`);
}
