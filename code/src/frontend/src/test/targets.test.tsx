import { act, cleanup, render, screen, waitFor, within } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Dashboard, DashboardWidget, TargetConfig } from "../api/dashboards";
import type { AggregationQuery, QueryResult } from "../api/query";
import type { ChartClick } from "../features/dashboards/charts/EChart";
import type { ChartTokens } from "../features/dashboards/charts/chartOptions";
import { defaultTarget, targetPeriod, targetRows, targetState, valueQuery } from "../features/dashboards/target";
import { setWorkspaceCalendar } from "../lib/calendar";
import { intervalOptions, intervalToSearch, intervalsFromSearch, withInterval } from "../features/dashboards/interval";
import { WidgetContent, drilledWidget } from "../features/dashboards/widgets/WidgetContent";
import { seedSetupInitializedForTest } from "../features/setup";
import { createTestRouter } from "../router";
import { resetAppStoreForTest } from "../stores/appStore";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import { resetSchemaStoreForTest, useSchemaStore } from "../stores/schemaStore";
import type { ModelSnapshot } from "../types/api";

const queryApi = vi.hoisted(() => ({ runQuery: vi.fn(), listSavedQueries: vi.fn() }));
const dataApi = vi.hoisted(() => ({ fetchTableRows: vi.fn() }));
const dashboardsApi = vi.hoisted(() => ({ getDashboard: vi.fn(), listDashboards: vi.fn(), updateDashboard: vi.fn() }));
vi.mock("../api/query", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/query")>()), ...queryApi }));
vi.mock("../api/data", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/data")>()), ...dataApi }));
vi.mock("../api/dashboards", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/dashboards")>()), ...dashboardsApi }));

// The chart stand-in hands its click handler to the test.
const charts = vi.hoisted(() => ({ click: undefined as undefined | ((click: ChartClick) => void) }));
vi.mock("../features/dashboards/charts/EChart", () => ({
  EChart: ({ label, option, onItemClick }: { label: string; option: { xAxis?: { data?: unknown[] } }; onItemClick?: (click: ChartClick) => void }) => {
    charts.click = onItemClick;
    return <div role="img" aria-label={label} data-categories={JSON.stringify(option.xAxis?.data ?? null)} />;
  },
}));

const tokens: ChartTokens = { text: "#eee", muted: "#999", line: "#333", surface: "#111", popover: "#222", palette: ["#3b82f6", "#14b8a6"], positive: "#4ade80", negative: "#f87171" };

const snapshot = {
  version: 1,
  tables: [
    {
      name: "categories",
      columns: [
        { name: "id", type: "integer", primaryKey: true },
        { name: "name", type: "text" },
        { name: "budget", displayName: "Monthly budget", type: "currency" },
      ],
    },
    {
      name: "transactions",
      columns: [
        { name: "id", type: "integer", primaryKey: true },
        { name: "date", type: "date" },
        { name: "amount", type: "currency" },
        { name: "category_id", type: "reference", references: "categories" },
        { name: "main_category", type: "computed", resultType: "text", formula: "COALESCE(category_id.parent_id.name, category_id.name)" },
      ],
    },
  ],
  relations: [],
} as unknown as ModelSnapshot;

// A budget is a limit target: spending (negative amounts) per category against its monthly limit.
const config: TargetConfig = { source: "transactions", value: "-SUM(amount WHERE amount < 0)", date: "date", group: "category_id", target: "budget", per: "month", period: "current", kind: "limit" };
const categories = [
  { id: 1, name: "Rent", budget: 1000 },
  { id: 2, name: "Groceries", budget: 250 },
  { id: 3, name: "Leisure", budget: 150 },
  { id: 4, name: "Salary", budget: null },
];
const spent: QueryResult = {
  data: [
    { category_id: 1, category_id_label: "Rent", value: 1000 },
    { category_id: 2, category_id_label: "Groceries", value: 205 },
    { category_id: 3, category_id_label: "Leisure", value: 180 },
    { category_id: 9, category_id_label: "Gifts", value: 40 },
  ],
  meta: { truncated: false, columns: [{ name: "category_id", type: "integer", role: "group" }, { name: "category_id_label", type: "text", role: "label", labelOf: "category_id" }, { name: "value", type: "currency", role: "measure" }] },
};

beforeEach(() => {
  resetAppStoreForTest();
  resetAuthStoreForTest();
  seedSetupInitializedForTest();
  resetSchemaStoreForTest();
  useAuthStore.setState({ status: "authenticated", user: { id: 2, username: "viewer", role: "viewer" }, accessToken: "token", expiresAt: Date.now() + 60_000, loginInFlight: false, error: null });
  useSchemaStore.setState({ status: "ready", snapshot, error: null, lastLoadedAt: Date.now(), inFlight: null });
  dataApi.fetchTableRows.mockResolvedValue({ data: categories, meta: { page: 1, per_page: 200, total: 4, total_pages: 1 } });
  queryApi.listSavedQueries.mockResolvedValue([]);
  vi.stubGlobal("fetch", vi.fn(() => Promise.reject(new Error("fetch must be mocked in tests"))));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.clearAllMocks();
  charts.click = undefined;
});

const range = { from: "2025-11-01", to: "2026-11-01" };

describe("targets", () => {
  it("compares the range's last period, or the whole range with its periods", () => {
    expect(targetPeriod(range)).toEqual({ from: "2026-10-01", to: "2026-11-01", count: 1, start: "2026-10-01" });
    expect(targetPeriod({ from: "2026-08-01", to: "2026-10-08" }, "month", "range")).toEqual({ from: "2026-08-01", to: "2026-10-08", count: 3, start: null });
    expect(targetPeriod({ from: null, to: null }, "month", "current", new Date(2026, 9, 8, 12))).toMatchObject({ from: "2026-10-01", to: "2026-11-01" });
    // Weeks and quarters as the workspace starts them.
    expect(targetPeriod({ from: "2026-10-01", to: "2026-10-11" }, "week")).toMatchObject({ from: "2026-10-05", to: "2026-10-12" });
    // A range of whole months stops at today: this week, not the month's last; one reaching ahead keeps its end.
    const thursday = new Date(2026, 9, 22, 12);
    expect(targetPeriod({ from: "2026-08-01", to: "2026-11-01" }, "week", "current", thursday)).toMatchObject({ from: "2026-10-19", to: "2026-10-26" });
    expect(targetPeriod({ from: "2026-10-01", to: "2026-11-01", future: true }, "week", "current", thursday)).toMatchObject({ from: "2026-10-26" });
    setWorkspaceCalendar({ weekStart: "sunday", yearStart: 4 });
    expect(targetPeriod({ from: "2026-10-01", to: "2026-10-11" }, "week")).toMatchObject({ from: "2026-10-04", to: "2026-10-11" });
    expect(targetPeriod({ from: "2026-01-01", to: "2026-03-01" }, "quarter")).toMatchObject({ from: "2026-01-01", to: "2026-04-01" });
    setWorkspaceCalendar(null);
    expect(valueQuery(config, range)).toMatchObject({
      source: "transactions",
      filter: { date: { gte: "2026-10-01", lt: "2026-11-01" } },
      groupBy: [{ column: "category_id" }],
      measures: [{ name: "value", formula: "-SUM(amount WHERE amount < 0)" }],
    });
  });

  it("puts each group with a target against its value, furthest along first", () => {
    const { rows, untargeted, total } = targetRows(spent, categories, config, "name", "id", 1);
    expect(rows.map((r) => `${r.label} ${r.value}/${r.target} ${Math.round(r.share * 100)}`)).toEqual(["Leisure 180/150 120", "Rent 1000/1000 100", "Groceries 205/250 82"]);
    expect(untargeted).toEqual({ value: 40, count: 1 });
    expect(total).toEqual({ value: 1385, target: 1400 });
    expect(targetRows(spent, categories, config, "name", "id", 3).rows.find((r) => r.label === "Groceries")?.target).toBe(750);
    expect([0.5, 0.82, 1, 1.2].map((share) => targetState(share, "limit"))).toEqual(["ok", "close", "close", "over"]);
    expect([0.5, 0.99, 1, 1.2].map((share) => targetState(share, "goal"))).toEqual(["ok", "ok", "done", "done"]);
  });

  it("finds a first target in any table that links to one with a number column", () => {
    const hours = {
      version: 1,
      tables: [
        { name: "projects", columns: [{ name: "id", type: "integer", primaryKey: true }, { name: "name", type: "text" }, { name: "weekly_hours", type: "float" }] },
        { name: "worklog", columns: [{ name: "id", type: "integer", primaryKey: true }, { name: "day", type: "date" }, { name: "hours", type: "float" }, { name: "project_id", type: "reference", references: "projects" }] },
      ],
      relations: [],
    } as unknown as ModelSnapshot;
    expect(defaultTarget(hours.tables, hours)).toEqual({
      source: "worklog",
      value: "SUM(hours)",
      date: "day",
      group: "project_id",
      target: "weekly_hours",
      per: "month",
      period: "current",
      kind: "goal",
    });
  });

  it("shows bars with the share, warns from 80 % and opens a group's rows", async () => {
    queryApi.runQuery.mockResolvedValue(spent);
    const onDrill = vi.fn();
    const widget: DashboardWidget = { id: "b", type: "target", title: "Budgets", position: { x: 0, y: 0, w: 6, h: 5 }, data: { target: config } };
    render(<WidgetContent widget={widget} range={range} refreshToken={0} tokens={tokens} onDrill={onDrill} />);

    const groceries = await screen.findByRole("progressbar", { name: "Groceries: 82 % of the limit" });
    expect(groceries).toHaveAttribute("aria-valuenow", "82");
    expect(screen.getByRole("progressbar", { name: "Leisure: 120 % of the limit" }).firstElementChild).toHaveClass("target__fill--over");
    expect(screen.getByText("120 %")).toHaveClass("target__share--over");
    expect(screen.getByText("82 %")).toHaveClass("target__share--close");
    expect(screen.getByText(/in 1 without a limit/)).toBeInTheDocument();

    await userEvent.setup().click(screen.getByRole("button", { name: /Groceries/ }));
    expect(onDrill).toHaveBeenCalledWith({
      table: "transactions",
      filters: [
        { column: "category_id", op: "eq", value: "2" },
        { column: "date", op: "gte", value: "2026-10-01" },
        { column: "date", op: "lt", value: "2026-11-01" },
      ],
    });
  });

  it("a goal turns green once reached", async () => {
    queryApi.runQuery.mockResolvedValue(spent);
    const widget: DashboardWidget = { id: "g", type: "target", title: "Goals", position: { x: 0, y: 0, w: 6, h: 5 }, data: { target: { ...config, kind: "goal", format: "integer" } } };
    render(<WidgetContent widget={widget} range={range} refreshToken={0} tokens={tokens} />);
    const rent = await screen.findByRole("progressbar", { name: "Rent: 100 % of the target" });
    expect(rent.firstElementChild).toHaveClass("target__fill--done");
    expect(screen.getByRole("progressbar", { name: "Groceries: 82 % of the target" }).firstElementChild).toHaveClass("target__fill--ok");
  });

  it("says what is missing when the linked table has no target column", async () => {
    const widget: DashboardWidget = { id: "b", type: "target", title: "Budgets", position: { x: 0, y: 0, w: 6, h: 5 }, data: { target: { ...config, target: "" } } };
    render(<WidgetContent widget={widget} range={range} refreshToken={0} tokens={tokens} />);
    expect(screen.getByText(/Add a number column for the targets to categories/)).toBeInTheDocument();
  });
});

describe("donut drill", () => {
  const donut: DashboardWidget = {
    id: "d",
    type: "donut",
    title: "Spending by category",
    position: { x: 0, y: 0, w: 4, h: 4 },
    data: { query: { source: "transactions", filter: { amount: { lt: 0 } }, groupBy: [{ column: "main_category" }], measures: [{ name: "spent", fn: "sum", column: "amount" }] }, dateColumn: "date" },
    display: { drill: "category_id" },
  };
  const answer = (query: AggregationQuery): QueryResult => {
    const group = query.groupBy![0]!.column;
    return group === "main_category"
      ? { data: [{ main_category: "Food", spent: -235 }, { main_category: "Rent", spent: -1000 }], meta: { truncated: false, columns: [{ name: "main_category", type: "text", role: "group" }, { name: "spent", type: "currency", role: "measure" }] } }
      : { data: [{ category_id: 2, category_id_label: "Groceries", spent: -200 }, { category_id: 5, category_id_label: "Restaurants", spent: -35 }], meta: { truncated: false, columns: [{ name: "category_id", type: "integer", role: "group" }, { name: "category_id_label", type: "text", role: "label", labelOf: "category_id" }, { name: "spent", type: "currency", role: "measure" }] } };
  };

  it("narrows a slice to its next level inside the donut, then opens its rows", async () => {
    queryApi.runQuery.mockImplementation(async (query: AggregationQuery) => answer(query));
    const onDrill = vi.fn();
    const user = userEvent.setup();
    render(<WidgetContent widget={donut} range={range} refreshToken={0} tokens={tokens} onDrill={onDrill} />);
    await screen.findByRole("img", { name: "Spending by category" });
    expect(screen.getByText("Click a slice to see what is in it")).toBeInTheDocument();

    act(() => charts.click!({ componentType: "series", name: "Food", dataIndex: 0 }));
    await waitFor(() =>
      expect(queryApi.runQuery).toHaveBeenLastCalledWith(
        expect.objectContaining({ groupBy: [{ column: "category_id" }], filter: expect.objectContaining({ main_category: "Food", amount: { lt: 0 } }) }),
      ),
    );
    const path = screen.getByRole("navigation", { name: "Level" });
    expect(within(path).getByText("Food")).toHaveAttribute("aria-current", "true");
    expect(onDrill).not.toHaveBeenCalled();

    // One level down, a slice opens its rows: the parent's value stays as a condition.
    await waitFor(() => expect(charts.click).toBeDefined());
    act(() => charts.click!({ componentType: "series", name: "Groceries", dataIndex: 0 }));
    expect(onDrill).toHaveBeenCalledWith({
      table: "transactions",
      filters: expect.arrayContaining([
        { column: "main_category", op: "eq", value: "Food" },
        { column: "category_id", op: "eq", value: "2" },
      ]),
    });

    // Back to the top: its numbers came a moment ago, so they show again without asking the server.
    const calls = queryApi.runQuery.mock.calls.length;
    await user.click(within(path).getByRole("button", { name: "All" }));
    await waitFor(() => expect(within(path).queryByText("Food")).not.toBeInTheDocument());
    expect(queryApi.runQuery).toHaveBeenCalledTimes(calls);
  });

  it("keeps an empty parent value as 'no value'", () => {
    expect(drilledWidget(donut, null).data?.query?.filter).toEqual({ amount: { lt: 0 }, main_category: null });
  });
});

describe("interval switch", () => {
  const chart: DashboardWidget = {
    id: "net",
    type: "bar",
    title: "Net per month",
    position: { x: 0, y: 0, w: 6, h: 4 },
    data: { query: { source: "transactions", groupBy: [{ column: "date", bucket: "month", as: "month" }], measures: [{ name: "net", fn: "sum", column: "amount" }] } },
  };

  it("offers month, quarter and year on charts by date and keeps the choice in the address", () => {
    expect(intervalOptions(chart)).toEqual(["month", "quarter", "year"]);
    expect(intervalOptions({ ...chart, type: "donut" })).toEqual([]);
    expect(intervalOptions({ ...chart, data: { query: { ...chart.data!.query!, groupBy: [{ column: "date", bucket: "week" }] } } })).toEqual(["week", "month", "quarter", "year"]);
    expect(withInterval(chart, "quarter").data?.query?.groupBy).toEqual([{ column: "date", bucket: "quarter", as: "month" }]);
    const params = intervalToSearch(chart, "year", new URLSearchParams("range=this-year"));
    expect(params.toString()).toBe("range=this-year&interval.net=year");
    expect(intervalsFromSearch(params)).toEqual({ net: "year" });
    expect(intervalToSearch(chart, "month", params).toString()).toBe("range=this-year");
  });

  it("offers it on summarized tables by date too, and follows the widget's setting", () => {
    const table: DashboardWidget = { ...chart, type: "table" };
    expect(intervalOptions(table)).toEqual(["month", "quarter", "year"]);
    // Individual rows have no interval.
    expect(intervalOptions({ ...table, data: { ...table.data, rows: { columns: ["date", "amount"] } } })).toEqual([]);
    // Switched off.
    expect(intervalOptions({ ...chart, display: { intervals: false } })).toEqual([]);
    expect(withInterval({ ...chart, display: { intervals: false } }, "year")).toEqual({ ...chart, display: { intervals: false } });
    // Only quarters and years — the widget's own month always stays.
    const limited: DashboardWidget = { ...chart, display: { intervals: ["quarter", "year"] } };
    expect(intervalOptions(limited)).toEqual(["month", "quarter", "year"]);
    expect(intervalOptions({ ...chart, display: { intervals: ["year"] } })).toEqual(["month", "year"]);
    expect(intervalOptions({ ...chart, display: { intervals: ["day", "week", "year"] } })).toEqual(["day", "week", "month", "year"]);
    expect(withInterval({ ...chart, display: { intervals: ["year"] } }, "quarter")).toEqual({ ...chart, display: { intervals: ["year"] } });
    // Nothing left to switch between: no switch.
    expect(intervalOptions({ ...chart, display: { intervals: [] } })).toEqual([]);
  });

  it("lets the owner limit or hide the switch under Style", async () => {
    queryApi.runQuery.mockResolvedValue({ data: [], meta: { truncated: false, columns: [] } });
    queryApi.listSavedQueries.mockResolvedValue([]);
    const owned: Dashboard = { id: 7, name: "Cash flow", isShared: false, isOwner: true, dashboard: { widgets: [chart] }, createdAt: "", updatedAt: "" };
    dashboardsApi.getDashboard.mockResolvedValue(owned);
    dashboardsApi.updateDashboard.mockImplementation(async (_id: number, changes: { dashboard: Dashboard["dashboard"] }) => ({ ...owned, dashboard: changes.dashboard }));
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/dashboards/7"])} />);

    await user.click(await screen.findByRole("button", { name: "Edit" }));
    await user.click(screen.getByRole("button", { name: "Configure Net per month" }));
    const editor = await screen.findByRole("dialog", { name: "Edit widget" });
    await user.click(within(editor).getByRole("tab", { name: "Style" }));
    const intervals = within(editor).getByRole("group", { name: "Intervals to switch between" });
    // The widget's own interval stays; quarters go.
    expect(within(intervals).getByRole("button", { name: "Month" })).toBeDisabled();
    await user.click(within(intervals).getByRole("button", { name: "Quarter" }));
    expect(within(intervals).getByRole("button", { name: "Quarter" })).toHaveAttribute("aria-pressed", "false");
    await user.click(within(editor).getByRole("button", { name: "Done" }));
    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(dashboardsApi.updateDashboard).toHaveBeenCalled());
    expect(dashboardsApi.updateDashboard.mock.calls[0][1].dashboard.widgets[0].display).toEqual({ intervals: ["month", "year"] });
    const group = await screen.findByRole("group", { name: "Net per month: interval" });
    expect(within(group).getAllByRole("button").map((b) => b.textContent)).toEqual(["M", "Y"]);
  });

  it("lets a viewer switch a chart to quarters", async () => {
    queryApi.runQuery.mockImplementation(async () => ({
      data: [{ month: "2026-07-01", net: 100 }],
      meta: { truncated: false, columns: [{ name: "month", type: "date", role: "group" }, { name: "net", type: "currency", role: "measure" }] },
    }));
    const dashboard: Dashboard = { id: 7, name: "Cash flow", isShared: true, isOwner: false, dashboard: { widgets: [chart] }, createdAt: "", updatedAt: "" };
    dashboardsApi.getDashboard.mockResolvedValue(dashboard);
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/dashboards/7"])} />);

    const switcher = await screen.findByRole("group", { name: "Net per month: interval" });
    expect(within(switcher).getByRole("button", { name: "Month" })).toHaveAttribute("aria-pressed", "true");
    await waitFor(() => expect(switcher).not.toHaveAttribute("aria-busy"));

    // The quarters take a moment: the old chart stays, the switch shows a spinner on Q.
    let answer: (result: QueryResult) => void = () => undefined;
    queryApi.runQuery.mockImplementation(() => new Promise<QueryResult>((resolve) => (answer = resolve)));
    await user.click(within(switcher).getByRole("button", { name: "Quarter" }));
    await waitFor(() => expect(queryApi.runQuery).toHaveBeenLastCalledWith(expect.objectContaining({ groupBy: [{ column: "date", bucket: "quarter", as: "month" }] })));
    const quarter = within(switcher).getByRole("button", { name: "Quarter" });
    expect(quarter).toHaveAttribute("aria-pressed", "true");
    await waitFor(() => expect(switcher).toHaveAttribute("aria-busy", "true"));
    expect(quarter.querySelector(".widget-interval__spinner")).not.toBeNull();
    expect(screen.getByRole("img", { name: /Net per month/ })).toBeInTheDocument();

    await act(async () => answer({ data: [{ month: "2026-07-01", net: 300 }], meta: { truncated: false, columns: [{ name: "month", type: "date", role: "group" }, { name: "net", type: "currency", role: "measure" }] } }));
    await waitFor(() => expect(switcher).not.toHaveAttribute("aria-busy"));
    expect(quarter.querySelector(".widget-interval__spinner")).toBeNull();
  });
});
