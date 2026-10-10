import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Dashboard, DashboardDocument } from "../api/dashboards";
import type { QueryResult } from "../api/query";
import { seedSetupInitializedForTest } from "../features/setup";
import { createTestRouter } from "../router";
import { resetAppStoreForTest } from "../stores/appStore";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import { resetSchemaStoreForTest, useSchemaStore } from "../stores/schemaStore";
import type { ModelSnapshot } from "../types/api";
import { inputToSearch, inputValuesFromSearch, literal, literalValue, withInputs } from "../features/dashboards/inputValues";

// Beta feedback, package 4b: assumptions viewers change, and a row that opens another dashboard.

const dashboardsApi = vi.hoisted(() => ({ getDashboard: vi.fn(), updateDashboard: vi.fn(), listDashboards: vi.fn() }));
const queryApi = vi.hoisted(() => ({ runQuery: vi.fn(), listSavedQueries: vi.fn(), evaluateVariables: vi.fn() }));
vi.mock("../api/dashboards", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/dashboards")>()), ...dashboardsApi }));
vi.mock("../api/query", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/query")>()), ...queryApi }));
vi.mock("../features/dashboards/charts/EChart", () => ({ EChart: ({ label }: { label: string }) => <div role="img" aria-label={label} /> }));

const snapshot = {
  version: 1,
  tables: [
    { name: "positions", columns: [{ name: "id", type: "integer", primaryKey: true }, { name: "isin", type: "text" }, { name: "value", type: "currency" }] },
    { name: "trades", columns: [{ name: "id", type: "integer", primaryKey: true }, { name: "isin", type: "text" }, { name: "date", type: "date" }, { name: "amount", type: "currency" }] },
  ],
  relations: [],
} as unknown as ModelSnapshot;

function dashboard(id: number, name: string, document: DashboardDocument): Dashboard {
  return { id, name, isShared: false, isOwner: true, dashboard: document, createdAt: "", updatedAt: "" };
}

describe("input values", () => {
  it("are literals in the address that replace a variable's default", () => {
    expect(literal("0.05")).toBe("0.05");
    expect(literal('say "hi"')).toBe('"say ""hi"""');
    expect(literalValue('"base"')).toBe("base");
    expect(literalValue("0.07")).toBe("0.07");
    const params = inputToSearch("rate", "0.05", new URLSearchParams("range=next-20-years"));
    expect(params.toString()).toBe("range=next-20-years&var.rate=0.05");
    expect(inputValuesFromSearch(params)).toEqual({ rate: "0.05" });
    expect(
      withInputs(
        [
          { name: "rate", formula: "0.07", input: { kind: "slider" } },
          { name: "spent", source: "trades", formula: "SUM(amount)" },
        ],
        { rate: "0.05", spent: "1" },
      ),
    ).toEqual([
      { name: "rate", formula: "0.05", input: { kind: "slider" }, source: undefined },
      // Only variables made for input take a value.
      { name: "spent", source: "trades", formula: "SUM(amount)" },
    ]);
  });
});

describe("dashboard interaction", () => {
  beforeEach(() => {
    resetAppStoreForTest();
    resetAuthStoreForTest();
    seedSetupInitializedForTest();
    resetSchemaStoreForTest();
    useAuthStore.setState({ status: "authenticated", user: { id: 1, username: "admin", role: "admin" }, accessToken: "token", expiresAt: Date.now() + 60_000, loginInFlight: false, error: null });
    useSchemaStore.setState({ status: "ready", snapshot, error: null, lastLoadedAt: Date.now(), inFlight: null });
    queryApi.listSavedQueries.mockResolvedValue([]);
    vi.stubGlobal("fetch", vi.fn(() => Promise.reject(new Error("fetch must be mocked in tests"))));
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
    vi.clearAllMocks();
  });

  it("lets viewers change an assumption, which the variables then compute with", async () => {
    queryApi.evaluateVariables.mockImplementation(async (requests: { name: string; formula: string }[]) =>
      requests.map((r) => ({ name: r.name, type: "float", value: Number(r.formula) || 0, error: null })),
    );
    dashboardsApi.getDashboard.mockResolvedValue(
      dashboard(8, "Plan", {
        variables: [{ name: "rate", label: "Return p.a.", formula: "0.07", input: { kind: "number", min: 0, max: 0.2, step: 0.01 } }],
        widgets: [{ id: "r", type: "kpi", title: "Return", position: { x: 0, y: 0, w: 3, h: 2 }, data: { variable: "rate" }, display: { format: "percent" } }],
      }),
    );
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/dashboards/8"])} />);

    const field = await screen.findByRole("spinbutton", { name: "Return p.a." });
    expect(field).toHaveValue(0.07);
    await waitFor(() => expect(queryApi.evaluateVariables).toHaveBeenCalledWith([{ name: "rate", formula: "0.07" }]));

    await user.clear(field);
    await user.type(field, "0.05");
    await waitFor(() => expect(queryApi.evaluateVariables).toHaveBeenLastCalledWith([{ name: "rate", formula: "0.05" }]));
    const card = screen.getByRole("article", { name: "Return" });
    await waitFor(() => expect(card.textContent).toMatch(/5\s?%/));

    // Back to the default: its value was computed before, so it comes from the browser's cache.
    await user.click(screen.getByRole("button", { name: "Reset to defaults" }));
    await waitFor(() => expect(screen.getByRole("spinbutton", { name: "Return p.a." })).toHaveValue(0.07));
    await waitFor(() => expect(card.textContent).toMatch(/7\s?%/));
    expect(screen.queryByRole("button", { name: "Reset to defaults" })).toBeNull();
  });

  it("opens the linked dashboard from a table row, filtered by the row and with the range", async () => {
    queryApi.evaluateVariables.mockResolvedValue([]);
    queryApi.runQuery.mockResolvedValue({
      data: [
        { isin: "IE00B4L5Y983", value: 12500 },
        { isin: "IE00BK5BQT80", value: 800 },
      ],
      meta: { truncated: false, columns: [{ name: "isin", type: "text", role: "group" }, { name: "value", type: "currency", role: "measure" }] },
    } satisfies QueryResult);
    const portfolio = dashboard(6, "Portfolio", {
      widgets: [
        {
          id: "pos",
          type: "table",
          title: "Positions",
          position: { x: 0, y: 0, w: 6, h: 4 },
          data: { query: { source: "positions", groupBy: [{ column: "isin" }], measures: [{ name: "value", fn: "sum", column: "value" }] }, dateColumn: null },
          display: { link: { dashboard: 10, column: "isin" } },
        },
      ],
    });
    // The asset dashboard has no menu for isin: the filter from the link applies anyway.
    const asset = dashboard(10, "Asset detail", {
      widgets: [{ id: "t", type: "table", title: "Trades", position: { x: 0, y: 0, w: 6, h: 4 }, data: { query: { source: "trades", groupBy: [{ column: "date" }], measures: [{ name: "n", fn: "count" }] }, dateColumn: null } }],
    });
    dashboardsApi.getDashboard.mockImplementation(async (id: number) => (id === 6 ? portfolio : asset));
    const user = userEvent.setup();
    const router = createTestRouter(["/dashboards/6?range=last-3-years"]);
    render(<RouterProvider router={router} />);

    const row = (await screen.findByText("IE00BK5BQT80")).closest("tr")!;
    await user.click(row);

    await waitFor(() => expect(router.state.location.pathname).toBe("/dashboards/10"));
    expect(new URLSearchParams(router.state.location.search).get("filter.isin")).toBe("IE00BK5BQT80");
    expect(new URLSearchParams(router.state.location.search).get("range")).toBe("last-3-years");
    await waitFor(() =>
      expect(queryApi.runQuery).toHaveBeenLastCalledWith(expect.objectContaining({ source: "trades", filter: { isin: { eq: "IE00BK5BQT80" } } })),
    );
    // And it shows in the filter bar, so it can be cleared.
    expect(within(screen.getByRole("group", { name: "Dashboard filters" })).getByText(/isin/i)).toBeInTheDocument();
  });
});
