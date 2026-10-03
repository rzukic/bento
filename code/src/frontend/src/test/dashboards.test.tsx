import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Dashboard, DashboardDocument } from "../api/dashboards";
import type { AggregationQuery, QueryResult } from "../api/query";
import { seedSetupInitializedForTest } from "../features/setup";
import { formatAmount } from "../lib/format";
import { createTestRouter } from "../router";
import { resetAppStoreForTest } from "../stores/appStore";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import { resetSchemaStoreForTest, useSchemaStore } from "../stores/schemaStore";
import type { ModelSnapshot } from "../types/api";

const dashboardsApi = vi.hoisted(() => ({
  listDashboards: vi.fn(),
  getDashboard: vi.fn(),
  createDashboard: vi.fn(),
  updateDashboard: vi.fn(),
  deleteDashboard: vi.fn(),
}));
const queryApi = vi.hoisted(() => ({
  runQuery: vi.fn(),
  runSavedQuery: vi.fn(),
  getSavedQuery: vi.fn(),
  listSavedQueries: vi.fn(),
}));

vi.mock("../api/dashboards", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/dashboards")>()), ...dashboardsApi }));
vi.mock("../api/query", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/query")>()), ...queryApi }));
// ECharts needs a real layout engine; the tests check the data it is given.
vi.mock("../features/dashboards/charts/EChart", () => ({
  EChart: ({ option, label }: { option: { series?: { name?: string; data?: unknown[] }[] }; label: string }) => (
    <div role="img" aria-label={label} data-series={JSON.stringify(option.series?.map((s) => ({ name: s.name, data: s.data })))} />
  ),
}));

const snapshot = {
  version: 1,
  tables: [
    { name: "kategorie", displayName: "Kategorie", columns: [{ name: "id", type: "integer", primaryKey: true }, { name: "name", type: "text" }] },
    {
      name: "girokonto",
      displayName: "Girokonto",
      columns: [
        { name: "id", type: "integer", primaryKey: true },
        { name: "buchungstag", displayName: "Buchungstag", type: "date" },
        { name: "buchungstext", displayName: "Buchungstext", type: "text" },
        { name: "auftraggeber", displayName: "Auftraggeber / Begünstigter", type: "text" },
        { name: "betrag_eur", displayName: "Betrag (EUR)", type: "currency" },
        { name: "kategorie_id", displayName: "Kategorie", type: "reference", references: "kategorie" },
      ],
    },
  ],
  relations: [],
} as unknown as ModelSnapshot;

function dashboard(document: DashboardDocument, extra: Partial<Dashboard> = {}): Dashboard {
  return { id: 7, name: "Cash flow", isShared: false, isOwner: true, dashboard: document, createdAt: "", updatedAt: "", ...extra };
}

/** Answers each widget query with plausible numbers for its shape. */
function answer(query: AggregationQuery): QueryResult {
  const measure = query.measures?.[0]?.name ?? "value";
  const group = query.groupBy?.[0];
  if (!group) return { data: [{ [measure]: -1442.05 }], meta: { truncated: false, columns: [{ name: measure, type: "currency", role: "measure" }] } };
  const name = group.as ?? group.column;
  if (group.bucket) {
    return {
      data: [
        { [name]: "2026-08-01", [measure]: 1000 },
        { [name]: "2026-09-01", [measure]: 1807.95 },
      ],
      meta: { truncated: false, columns: [{ name, type: "date", role: "group" }, { name: measure, type: "currency", role: "measure" }] },
    };
  }
  return {
    data: [{ [name]: "REWE Markt", [measure]: -98 }],
    meta: { truncated: false, columns: [{ name, type: "text", role: "group" }, { name: measure, type: "currency", role: "measure" }] },
  };
}

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
  queryApi.runQuery.mockImplementation(async (query: AggregationQuery) => answer(query));
  queryApi.listSavedQueries.mockResolvedValue([]);
  dashboardsApi.listDashboards.mockResolvedValue([]);
  vi.stubGlobal("fetch", vi.fn(() => Promise.reject(new Error("fetch must be mocked in tests"))));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe("Dashboards", () => {
  it("creates a cash-flow dashboard from a table with dates and amounts", async () => {
    dashboardsApi.createDashboard.mockImplementation(async (name: string, document: DashboardDocument) => dashboard(document, { name }));
    dashboardsApi.getDashboard.mockImplementation(async () => {
      const [name, document] = dashboardsApi.createDashboard.mock.calls[0] as [string, DashboardDocument];
      return dashboard(document, { name });
    });
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/dashboards"])} />);

    await user.click(await screen.findByRole("button", { name: /Monthly cash flow.*Girokonto/ }));

    const [name, document] = dashboardsApi.createDashboard.mock.calls[0] as [string, DashboardDocument];
    expect(name).toBe("Cash flow · Girokonto");
    expect(document.widgets.map((w) => w.title)).toEqual([
      "Balance",
      "Net per month",
      "Spent in the range",
      "Balance over time",
      "Net per month",
      "Spending by Kategorie",
      "Top spending: Auftraggeber / Begünstigter",
    ]);
    const balance = document.widgets[0].data!.query!;
    expect(balance.measures![0]).toMatchObject({ fn: "sum", column: "betrag_eur", running: true, carryIn: true });
    expect(balance.groupBy![0]).toMatchObject({ column: "buchungstag", bucket: "month" });

    // The new dashboard opens with every widget asking for its numbers in the range.
    expect(await screen.findByRole("heading", { name: "Cash flow · Girokonto" })).toBeInTheDocument();
    await waitFor(() => expect(queryApi.runQuery).toHaveBeenCalledTimes(7));
    const filtered = queryApi.runQuery.mock.calls.map(([query]) => (query as AggregationQuery).filter?.buchungstag);
    expect(filtered.every((f) => f && typeof f === "object" && "gte" in f && "lt" in f)).toBe(true);
  });

  it("shows key figures as amounts and charts with every month of the range", async () => {
    dashboardsApi.getDashboard.mockResolvedValue(
      dashboard({
        dateRange: { preset: "all" },
        widgets: [
          {
            id: "kpi",
            type: "kpi",
            title: "Balance",
            position: { x: 0, y: 0, w: 4, h: 2 },
            data: { query: { source: "girokonto", groupBy: [{ column: "buchungstag", bucket: "month", as: "month" }], measures: [{ name: "balance", fn: "sum", column: "betrag_eur", running: true }] } },
          },
          {
            id: "chart",
            type: "bar",
            title: "Net",
            position: { x: 4, y: 0, w: 8, h: 4 },
            data: { query: { source: "girokonto", groupBy: [{ column: "buchungstag", bucket: "month", as: "month" }], measures: [{ name: "net", fn: "sum", column: "betrag_eur" }] } },
          },
        ],
      }),
    );
    render(<RouterProvider router={createTestRouter(["/dashboards/7"])} />);

    const kpi = await screen.findByRole("article", { name: "Balance" });
    expect(await within(kpi).findByText(formatAmount(1807.95))).toBeInTheDocument();
    const chart = await screen.findByRole("img", { name: "Net" });
    expect(JSON.parse(chart.getAttribute("data-series")!)).toEqual([{ name: "net", data: [1000, 1807.95] }]);
    // "All time" sends no date filter.
    expect((queryApi.runQuery.mock.calls[0][0] as AggregationQuery).filter).toBeUndefined();
  });

  it("puts the date range in the URL and asks again with it", async () => {
    dashboardsApi.getDashboard.mockResolvedValue(
      dashboard({
        dateRange: { preset: "all" },
        widgets: [
          {
            id: "kpi",
            type: "kpi",
            title: "Spent",
            position: { x: 0, y: 0, w: 4, h: 2 },
            data: { query: { source: "girokonto", measures: [{ name: "spent", fn: "sum", column: "betrag_eur" }] }, dateColumn: "buchungstag" },
          },
        ],
      }),
    );
    const user = userEvent.setup();
    const router = createTestRouter(["/dashboards/7"]);
    render(<RouterProvider router={router} />);
    await screen.findByRole("article", { name: "Spent" });

    await user.selectOptions(screen.getByLabelText("Date range"), "this-year");

    expect(router.state.location.search).toBe("?range=this-year");
    await waitFor(() => expect(queryApi.runQuery).toHaveBeenCalledTimes(2));
    const year = new Date().getFullYear();
    expect((queryApi.runQuery.mock.calls[1][0] as AggregationQuery).filter).toEqual({
      buchungstag: { gte: `${year}-01-01`, lt: `${year + 1}-01-01` },
    });
  });

  it("adds, configures and saves a widget in edit mode", async () => {
    dashboardsApi.getDashboard.mockResolvedValue(dashboard({ dateRange: { preset: "last-12-months" }, widgets: [] }));
    dashboardsApi.updateDashboard.mockImplementation(async (_id: number, changes: { name: string; dashboard: DashboardDocument }) =>
      dashboard(changes.dashboard, { name: changes.name }),
    );
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/dashboards/7"])} />);

    await user.click(await screen.findByRole("button", { name: "Add widgets" }));
    await user.click(screen.getByRole("button", { name: "Add widget" }));
    await user.click(screen.getByRole("menuitem", { name: "Key figure" }));

    const editor = await screen.findByRole("complementary", { name: "Widget settings" });
    const title = within(editor).getByLabelText("Title");
    await user.clear(title);
    await user.type(title, "Spent this year");
    // The generated key is never offered as a value.
    expect(within(within(editor).getByLabelText("Of")).queryByRole("option", { name: "id" })).not.toBeInTheDocument();
    await user.click(within(editor).getByRole("button", { name: "Done" }));
    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(dashboardsApi.updateDashboard).toHaveBeenCalledTimes(1));
    const saved = dashboardsApi.updateDashboard.mock.calls[0][1] as { dashboard: DashboardDocument };
    expect(saved.dashboard.widgets).toHaveLength(1);
    expect(saved.dashboard.widgets[0]).toMatchObject({
      type: "kpi",
      title: "Spent this year",
      position: { x: 0, y: 0, w: 3, h: 2 },
      data: { query: { source: "girokonto", measures: [{ fn: "sum", column: "betrag_eur" }] } },
    });
  });
});
