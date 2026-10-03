import { apiClient } from "./client";
import type { AggregationQuery } from "./query";

export type WidgetType = "kpi" | "line" | "area" | "bar" | "donut" | "table" | "text";

export type WidgetPosition = { x: number; y: number; w: number; h: number };

/** Where a widget's numbers come from: its own query or a saved one. */
export type WidgetData = {
  query?: AggregationQuery;
  savedQueryId?: number;
  /** The date column the dashboard's date range filters; none = the widget ignores the range. */
  dateColumn?: string | null;
};

export type WidgetDisplay = {
  /** auto follows the result column's type (currency → amount). */
  format?: "auto" | "currency" | "number" | "integer" | "percent";
  stacked?: boolean;
  /** Text widget content (headings with #, lists with -). */
  text?: string;
  /** KPI: compare with the previous date bucket. */
  compare?: boolean;
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
  | "this-month"
  | "last-3-months"
  | "last-6-months"
  | "last-12-months"
  | "this-year"
  | "last-year"
  | "all";

export type DashboardDateRange = { preset: DateRangePreset } | { from: string; to: string };

/** Dashboard JSON (Part 9 R9.8). */
export type DashboardDocument = {
  description?: string;
  dateRange?: DashboardDateRange;
  /** Seconds between automatic refreshes; 0 or missing = off. */
  refreshSeconds?: number;
  widgets: DashboardWidget[];
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
