import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Dashboard, DashboardDocument } from "../api/dashboards";
import type { AggregationQuery, QueryResult } from "../api/query";
import { asOfPeriod, runnableQuery, variableRequests } from "../features/dashboards/formulaQueries";
import { isRunningMeasure, toChartModel } from "../features/dashboards/widgetData";
import { seedSetupInitializedForTest } from "../features/setup";
import { createTestRouter } from "../router";
import { resetAppStoreForTest } from "../stores/appStore";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import { resetSchemaStoreForTest, useSchemaStore } from "../stores/schemaStore";
import type { ModelSnapshot } from "../types/api";

const dashboardsApi = vi.hoisted(() => ({ getDashboard: vi.fn(), updateDashboard: vi.fn(), listDashboards: vi.fn() }));
const queryApi = vi.hoisted(() => ({ runQuery: vi.fn(), listSavedQueries: vi.fn(), evaluateVariables: vi.fn() }));
const formulasApi = vi.hoisted(() => ({ validateFormula: vi.fn(), loadFormulaFunctions: vi.fn() }));
vi.mock("../api/dashboards", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/dashboards")>()), ...dashboardsApi }));
vi.mock("../api/query", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/query")>()), ...queryApi }));
vi.mock("../api/formulas", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/formulas")>()), ...formulasApi }));
vi.mock("../features/dashboards/charts/EChart", () => ({
  EChart: ({ option, label }: { option: { series?: { name?: string; data?: unknown[] }[] }; label: string }) => (
    <div role="img" aria-label={label} data-series={JSON.stringify(option.series?.map((s) => ({ name: s.name, data: s.data })))} />
  ),
}));

const snapshot = {
  version: 1,
  tables: [
    {
      name: "transactions",
      columns: [
        { name: "id", type: "integer", primaryKey: true },
        { name: "booked_on", type: "date" },
        { name: "amount", type: "currency" },
      ],
    },
  ],
  relations: [],
} as unknown as ModelSnapshot;

const monthly: AggregationQuery = {
  source: "transactions",
  groupBy: [{ column: "booked_on", bucket: "month", as: "month" }],
  measures: [{ name: "rate", formula: "(SUM(amount WHERE amount > 0) + SUM(amount WHERE amount < 0)) / SUM(amount WHERE amount > 0)" }],
};

describe("formula queries", () => {
  const range = { from: "2026-01-01", to: "2026-04-01" };
  const today = new Date(2026, 9, 4);

  it("filter the range on the date column and pass variables to formulas", () => {
    expect(runnableQuery(monthly, { query: monthly }, range, { months: 3 }, today)).toEqual({
      ...monthly,
      filter: { booked_on: { gte: "2026-01-01", lt: "2026-04-01" } },
      variables: { months: 3 },
    });
    const plain = { source: "transactions", measures: [{ name: "n", fn: "count" as const }] };
    expect(runnableQuery(plain, { query: plain }, { from: null, to: null }, { months: 3 }, today)).toEqual(plain);
  });

  it("turn the date group into periods by end date", () => {
    const query = runnableQuery(monthly, { query: monthly, asOf: true }, range, {}, today);
    expect(query.groupBy).toEqual([]);
    expect(query.asOf).toEqual({ bucket: "month", from: "2026-01-01", to: "2026-03-31", as: "month" });
    expect(query.filter).toBeUndefined();
    expect(asOfPeriod({ from: null, to: null }, today)).toEqual({ from: "2025-10-01", to: "2026-10-04" });
    expect(asOfPeriod({ from: "2026-10-01", to: "2027-01-01" }, today)).toEqual({ from: "2026-10-01", to: "2026-10-04" });
  });

  it("recognize running totals written as RUNNING_SUM", () => {
    expect(isRunningMeasure({ name: "b", formula: "= RUNNING_SUM(SUM(amount))" })).toBe(true);
    expect(isRunningMeasure({ name: "b", formula: "SUM(amount)" })).toBe(false);
    const result: QueryResult = {
      data: [
        { month: "2026-01-01", balance: 100 },
        { month: "2026-03-01", balance: 250 },
      ],
      meta: { truncated: false, columns: [{ name: "month", type: "date", role: "group" }, { name: "balance", type: "currency", role: "measure" }] },
    };
    const query = { ...monthly, measures: [{ name: "balance", formula: "RUNNING_SUM(SUM(amount))" }] };
    // February has no rows: a running total holds its level.
    expect(toChartModel(result, query, range, undefined, today).series[0].values).toEqual([100, 100, 250]);
  });

  it("send the range only to variables with a date column", () => {
    expect(
      variableRequests(
        [
          { name: "spent", source: "transactions", formula: "SUM(amount)", dateColumn: "booked_on" },
          { name: "runway", formula: "cash / spent" },
        ],
        range,
      ),
    ).toEqual([
      { name: "spent", source: "transactions", formula: "SUM(amount)", filter: { booked_on: { gte: "2026-01-01", lt: "2026-04-01" } } },
      { name: "runway", formula: "cash / spent" },
    ]);
  });
});

function dashboard(document: DashboardDocument): Dashboard {
  return { id: 7, name: "Finances", isShared: false, isOwner: true, dashboard: document, createdAt: "", updatedAt: "" };
}

describe("dashboards with formulas", () => {
  beforeEach(() => {
    resetAppStoreForTest();
    resetAuthStoreForTest();
    seedSetupInitializedForTest();
    resetSchemaStoreForTest();
    useAuthStore.setState({
      status: "authenticated",
      user: { id: 1, username: "admin", role: "admin" },
      accessToken: "token",
      expiresAt: Date.now() + 60_000,
      loginInFlight: false,
      error: null,
    });
    useSchemaStore.setState({ status: "ready", snapshot, error: null, lastLoadedAt: Date.now(), inFlight: null });
    queryApi.listSavedQueries.mockResolvedValue([]);
    queryApi.runQuery.mockResolvedValue({
      data: [{ month: "2026-09-01", rate: 0.5 }, { month: "2026-10-01", rate: 0.6 }],
      meta: { truncated: false, columns: [{ name: "month", type: "date", role: "group" }, { name: "rate", type: "float", role: "measure" }] },
    } satisfies QueryResult);
    queryApi.evaluateVariables.mockResolvedValue([
      { name: "runway", type: "float", value: 5.3013697178, error: null },
      { name: "broken", type: null, value: null, error: "Column 'nothing' not found in the dashboard variables." },
    ]);
    formulasApi.loadFormulaFunctions.mockResolvedValue([]);
    formulasApi.validateFormula.mockResolvedValue({ valid: true, type: "currency", errors: [], columns: [] });
    vi.stubGlobal("fetch", vi.fn(() => Promise.reject(new Error("fetch must be mocked in tests"))));
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
    vi.clearAllMocks();
  });

  const document: DashboardDocument = {
    dateRange: { preset: "last-3-months" },
    variables: [
      { name: "runway", formula: "cash / burn_rate" },
      { name: "broken", formula: "nothing + 1" },
    ],
    widgets: [
      { id: "a", type: "kpi", title: "Runway", position: { x: 0, y: 0, w: 3, h: 2 }, data: { variable: "runway" }, display: { format: "number" } },
      { id: "b", type: "kpi", title: "Broken", position: { x: 3, y: 0, w: 3, h: 2 }, data: { variable: "broken" } },
      { id: "c", type: "kpi", title: "Savings rate", position: { x: 6, y: 0, w: 3, h: 2 }, data: { query: monthly }, display: { format: "percent", compare: true } },
    ],
  };

  it("shows variables in key figures and passes them to widget formulas", async () => {
    dashboardsApi.getDashboard.mockResolvedValue(dashboard(document));
    render(<RouterProvider router={createTestRouter(["/dashboards/7"])} />);

    const runway = await screen.findByRole("article", { name: "Runway" });
    await waitFor(() => expect(runway.textContent).toMatch(/5[.,]3/));
    expect(within(await screen.findByRole("article", { name: "Broken" })).getByRole("alert").textContent).toContain("Column 'nothing' not found");

    const rate = await screen.findByRole("article", { name: "Savings rate" });
    await waitFor(() => expect(rate.textContent).toMatch(/60/));
    expect(rate.textContent).toMatch(/vs\./);
    const query = queryApi.runQuery.mock.calls.at(-1)![0] as AggregationQuery;
    expect(query.variables).toEqual({ runway: 5.3013697178 });
    expect(queryApi.evaluateVariables).toHaveBeenCalledWith([
      { name: "runway", formula: "cash / burn_rate" },
      { name: "broken", formula: "nothing + 1" },
    ]);
  });

  it("edits a value as a formula and by period end", async () => {
    dashboardsApi.getDashboard.mockResolvedValue(dashboard({ ...document, variables: undefined, widgets: [document.widgets[2]] }));
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/dashboards/7"])} />);

    await user.click(await screen.findByRole("button", { name: "Edit" }));
    await user.click(await screen.findByRole("button", { name: "Configure Savings rate" }));
    const editor = await screen.findByRole("dialog", { name: "Edit widget" });
    const settings = within(editor).getByRole("complementary", { name: "Widget settings" });
    await user.click(within(settings).getByRole("button", { name: "Edit Value: rate" }));
    expect(within(settings).getByLabelText("Formula of rate")).toBeInTheDocument();
    await waitFor(() => expect(formulasApi.validateFormula).toHaveBeenCalledWith(expect.objectContaining({ mode: "measure", table: "transactions" })));
    expect(await within(settings).findByText("Gives an amount")).toBeInTheDocument();
    await user.click(within(settings).getByRole("button", { name: "Done" }));

    // As a line chart, the values can be taken at the end of each month.
    await user.click(within(editor).getByRole("radio", { name: "Line chart" }));
    await user.click(within(editor).getByLabelText("Value at the end of each period"));
    await waitFor(() => expect(queryApi.runQuery.mock.calls.at(-1)![0].asOf).toMatchObject({ bucket: "month", as: "month" }));
    await user.click(within(editor).getByRole("button", { name: "Done" }));

    // Variables panel: add one and see its value.
    queryApi.evaluateVariables.mockResolvedValue([{ name: "value", type: "integer", value: 9, error: null }]);
    await user.click(screen.getByRole("button", { name: /Variables/ }));
    const variables = await screen.findByRole("complementary", { name: "Dashboard variables" });
    await user.click(within(variables).getByRole("button", { name: "Add variable" }));
    await waitFor(() => expect(within(variables).getByText("= 9")).toBeInTheDocument());
    expect(queryApi.evaluateVariables).toHaveBeenLastCalledWith([{ name: "value", source: "transactions", formula: "COUNT()" }]);
  });
});

describe("widget editor guards", () => {
  it("drops running totals when the date group goes", async () => {
    const { withoutTimeSeries } = await import("../features/dashboards/lens/model");
    expect(withoutTimeSeries({ name: "balance", fn: "sum", column: "amount", running: true, carryIn: true })).toEqual({ name: "balance", fn: "sum", column: "amount" });
    expect(withoutTimeSeries({ name: "balance", formula: "= RUNNING_SUM(SUM(amount WHERE amount > 0))" })).toEqual({ name: "balance", formula: "SUM(amount WHERE amount > 0)" });
    expect(withoutTimeSeries({ name: "n", formula: "COUNT()" })).toEqual({ name: "n", formula: "COUNT()" });
  });
});
