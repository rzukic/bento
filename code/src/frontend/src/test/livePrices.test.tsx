import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Dashboard } from "../api/dashboards";
import type { CustomFunction } from "../api/functions";
import type { PriceSource } from "../api/priceSources";
import type { AggregationQuery } from "../api/query";
import { sourcesForDashboard } from "../features/dashboards/priceRefresh";
import { seedSetupInitializedForTest } from "../features/setup";
import { createTestRouter } from "../router";
import { resetAppStoreForTest } from "../stores/appStore";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import { resetSchemaStoreForTest, useSchemaStore } from "../stores/schemaStore";
import type { ModelSnapshot } from "../types/api";

const functionsApi = vi.hoisted(() => ({
  listFunctions: vi.fn(),
  getFunctionStatus: vi.fn(),
  listFunctionTemplates: vi.fn(),
  createFunction: vi.fn(),
  testFunction: vi.fn(),
}));
const sourcesApi = vi.hoisted(() => ({
  listPriceSources: vi.fn(),
  createPriceSource: vi.fn(),
  updatePriceSource: vi.fn(),
  runPriceSource: vi.fn(),
  listPriceSourceRuns: vi.fn(),
  suggestSymbols: vi.fn(),
  refreshPrices: vi.fn(),
}));
const credentialsApi = vi.hoisted(() => ({
  listCredentials: vi.fn(),
  createCredential: vi.fn(),
  updateCredential: vi.fn(),
  deleteCredential: vi.fn(),
}));
const dashboardsApi = vi.hoisted(() => ({ getDashboard: vi.fn(), listDashboards: vi.fn() }));
const queryApi = vi.hoisted(() => ({ runQuery: vi.fn(), evaluateVariables: vi.fn() }));

vi.mock("../api/functions", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/functions")>()), ...functionsApi }));
vi.mock("../api/priceSources", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/priceSources")>()), ...sourcesApi }));
vi.mock("../api/credentials", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/credentials")>()), ...credentialsApi }));
vi.mock("../api/dashboards", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/dashboards")>()), ...dashboardsApi }));
vi.mock("../api/query", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/query")>()), ...queryApi }));
vi.mock("../features/dashboards/charts/EChart", () => ({ EChart: ({ label }: { label: string }) => <div role="img" aria-label={label} /> }));

const snapshot = {
  version: 1,
  tables: [
    { name: "finance_trades", columns: [{ name: "id", type: "integer", primaryKey: true }, { name: "isin", type: "text" }, { name: "shares", type: "float" }] },
    {
      name: "finance_prices",
      columns: [
        { name: "id", type: "integer", primaryKey: true },
        { name: "isin", type: "text" },
        { name: "date", type: "date" },
        { name: "price", type: "currency" },
      ],
    },
  ],
  relations: [],
} as unknown as ModelSnapshot;

const yahoo: CustomFunction = {
  name: "yahoo_prices",
  description: null,
  arguments: [{ name: "symbols", type: "any" }, { name: "from", type: "date" }, { name: "to", type: "date" }],
  resultType: "any",
  code: "return [];",
  version: 1,
  createdAt: "",
  updatedAt: "",
  usedBy: [],
  allowedHosts: ["query1.finance.yahoo.com"],
  credentials: [],
  usableInFormulas: false,
};
const lookup: CustomFunction = { ...yahoo, name: "isin_to_yahoo", arguments: [{ name: "keys", type: "any" }], allowedHosts: ["api.openfigi.com"] };

const source: PriceSource = {
  id: 3,
  name: "Yahoo",
  enabled: true,
  config: {
    function: "yahoo_prices",
    lookupFunction: "isin_to_yahoo",
    table: "finance_prices",
    keyColumn: "isin",
    dateColumn: "date",
    priceColumn: "price",
    keysFrom: "finance_trades.isin",
    symbols: { IE00B4L5Y983: "EUNL.DE" },
    freshMinutes: 15,
    dailyAt: "22:30",
    timeZone: "Europe/Berlin",
  },
  status: { lastRunAt: null, lastSuccessAt: null, lastQuoteAt: "2026-10-06T13:26:00Z", lastError: null, requestsToday: 3, running: false },
  keys: [
    { key: "IE00B4L5Y983", symbol: "EUNL.DE", mapped: true, lastDate: "2026-10-06", lastPrice: 131.64 },
    { key: "IE00BK5BQT80", symbol: "IE00BK5BQT80", mapped: false, lastDate: null, lastPrice: null },
  ],
};

function signIn(role: string) {
  useAuthStore.setState({
    status: "authenticated",
    user: { id: 1, username: role, role },
    accessToken: "token",
    expiresAt: Date.now() + 60_000,
    loginInFlight: false,
    error: null,
  });
}

beforeEach(() => {
  resetAppStoreForTest();
  resetAuthStoreForTest();
  seedSetupInitializedForTest();
  resetSchemaStoreForTest();
  signIn("admin");
  useSchemaStore.setState({ status: "ready", snapshot, error: null, lastLoadedAt: Date.now(), inFlight: null });
  functionsApi.listFunctions.mockResolvedValue([yahoo, lookup]);
  functionsApi.getFunctionStatus.mockResolvedValue({ pending: false, lastRunAt: null, lastUpdated: 0, failures: [] });
  functionsApi.listFunctionTemplates.mockResolvedValue([]);
  sourcesApi.listPriceSources.mockResolvedValue([source]);
  sourcesApi.listPriceSourceRuns.mockResolvedValue([]);
  credentialsApi.listCredentials.mockResolvedValue([]);
  vi.stubGlobal("fetch", vi.fn(() => Promise.reject(new Error("fetch must be mocked in tests"))));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe("Price sources", () => {
  it("shows a source's securities, suggests symbols and saves them", async () => {
    sourcesApi.suggestSymbols.mockResolvedValue([{ key: "IE00BK5BQT80", symbol: "VWCE.DE" }]);
    sourcesApi.updatePriceSource.mockResolvedValue(source);
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/functions?tab=prices"])} />);

    expect(await screen.findByLabelText("Symbol for IE00B4L5Y983")).toHaveValue("EUNL.DE");
    expect(screen.getByText(/131[.,]64 · 2026-10-06/)).toBeInTheDocument();
    expect(screen.getByText(/3 requests today/)).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Suggest symbols" }));
    expect(await screen.findByDisplayValue("VWCE.DE")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() =>
      expect(sourcesApi.updatePriceSource).toHaveBeenCalledWith(3, expect.objectContaining({
        config: expect.objectContaining({ symbols: { IE00B4L5Y983: "EUNL.DE", IE00BK5BQT80: "VWCE.DE" } }),
      })),
    );
  });

  it("fetches on demand and reports the run", async () => {
    sourcesApi.runPriceSource.mockResolvedValue({ id: 9, kind: "history", startedAt: "", finishedAt: "", requests: 2, rowsWritten: 500, lastQuoteAt: null, error: null });
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/functions?tab=prices"])} />);
    await user.click(await screen.findByRole("button", { name: "Fetch history" }));
    expect(await screen.findByText("500 prices written · 2 requests.")).toBeInTheDocument();
    expect(sourcesApi.runPriceSource).toHaveBeenCalledWith(3, "history");
  });

  it("proposes the wealth template's tables for a new source", async () => {
    sourcesApi.listPriceSources.mockResolvedValue([]);
    sourcesApi.createPriceSource.mockResolvedValue({ ...source, id: 4 });
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/functions?tab=prices"])} />);
    await user.click(await screen.findByRole("button", { name: "Add a price source" }));
    expect(screen.getByLabelText("Function")).toHaveValue("yahoo_prices");
    expect(screen.getByLabelText("Table")).toHaveValue("finance_prices");
    expect(screen.getByLabelText("Securities from")).toHaveValue("finance_trades.isin");
    await user.type(screen.getByLabelText("Source name"), "Yahoo");
    await user.click(screen.getByRole("button", { name: "Create source" }));
    await waitFor(() =>
      expect(sourcesApi.createPriceSource).toHaveBeenCalledWith(
        "Yahoo",
        expect.objectContaining({ table: "finance_prices", keyColumn: "isin", dateColumn: "date", priceColumn: "price", lookupFunction: "isin_to_yahoo" }),
        true,
      ),
    );
  });
});

describe("Credentials", () => {
  it("adds a credential without ever showing its value", async () => {
    credentialsApi.createCredential.mockResolvedValue({});
    credentialsApi.listCredentials
      .mockResolvedValueOnce([])
      .mockResolvedValue([{ name: "alphavantage_key", description: "Prices", createdAt: "", updatedAt: null, lastUsedAt: null, usedBy: ["av_prices"], readable: true }]);
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/functions?tab=credentials"])} />);
    await user.type(await screen.findByLabelText("Credential name"), "alphavantage_key");
    await user.type(screen.getByLabelText("Credential value"), "secret-123");
    await user.click(screen.getByRole("button", { name: "Add credential" }));
    expect(credentialsApi.createCredential).toHaveBeenCalledWith("alphavantage_key", "secret-123", undefined);
    expect(await screen.findByText(/Used by av_prices/)).toBeInTheDocument();
    expect(screen.queryByText("secret-123")).not.toBeInTheDocument();
    expect(screen.getByLabelText("Credential value")).toHaveValue("");
  });

  it("is only for admins", async () => {
    signIn("viewer");
    render(<RouterProvider router={createTestRouter(["/functions?tab=credentials"])} />);
    const tabs = await screen.findByRole("tablist", { name: "Functions" });
    expect(within(tabs).queryByRole("tab", { name: "Credentials" })).not.toBeInTheDocument();
  });
});

describe("Dashboards with live prices", () => {
  const wealth: Dashboard = {
    id: 8,
    name: "Wealth",
    isShared: false,
    isOwner: true,
    createdAt: "",
    updatedAt: "",
    dashboard: {
      dateRange: { preset: "all" },
      widgets: [
        {
          id: "value",
          type: "kpi",
          title: "Depot value",
          position: { x: 0, y: 0, w: 4, h: 2 },
          data: { query: { source: "finance_trades", measures: [{ name: "value", formula: 'SUM(shares * LOOKUP_ASOF("finance_prices", "isin", isin, "date", TODAY(), "price"))' }] } },
        },
      ],
    },
  };

  it("matches sources by the tables a dashboard reads", () => {
    expect(sourcesForDashboard([source], wealth.dashboard).map((s) => s.id)).toEqual([3]);
    expect(sourcesForDashboard([source], { widgets: [{ data: { query: { source: "finance_prices_old" } } }] })).toEqual([]);
    expect(sourcesForDashboard([{ ...source, enabled: false }], wealth.dashboard)).toEqual([]);
  });

  it("fetches stale prices on open, reloads the widgets when they changed and shows their time", async () => {
    dashboardsApi.getDashboard.mockResolvedValue(wealth);
    queryApi.runQuery.mockResolvedValue({ data: [{ value: 1000 }], meta: { truncated: false, columns: [{ name: "value", type: "currency", role: "measure" }] } });
    sourcesApi.refreshPrices
      .mockResolvedValueOnce({ changed: true, lastQuoteAt: "2026-10-06T13:26:00Z", sources: [{ id: 3, name: "Yahoo", table: "finance_prices", fetched: true, rowsWritten: 2, lastQuoteAt: "2026-10-06T13:26:00Z", error: null }] })
      .mockResolvedValue({ changed: false, lastQuoteAt: "2026-10-06T13:26:00Z", sources: [{ id: 3, name: "Yahoo", table: "finance_prices", fetched: false, rowsWritten: 0, lastQuoteAt: "2026-10-06T13:26:00Z", error: null }] });
    render(<RouterProvider router={createTestRouter(["/dashboards/8"])} />);

    expect(await screen.findByText(/^Prices as of /)).toBeInTheDocument();
    expect(sourcesApi.refreshPrices).toHaveBeenCalledWith(["finance_prices"]);
    // The widget loaded again after the prices changed.
    await waitFor(() => expect(queryApi.runQuery.mock.calls.filter(([q]) => (q as AggregationQuery).measures?.[0]?.name === "value").length).toBeGreaterThanOrEqual(2));
  });

  it("says when the prices could not be updated", async () => {
    dashboardsApi.getDashboard.mockResolvedValue(wealth);
    queryApi.runQuery.mockResolvedValue({ data: [{ value: 1000 }], meta: { truncated: false, columns: [{ name: "value", type: "currency", role: "measure" }] } });
    sourcesApi.refreshPrices.mockResolvedValue({
      changed: false,
      lastQuoteAt: "2026-10-05T15:30:00Z",
      sources: [{ id: 3, name: "Yahoo", table: "finance_prices", fetched: true, rowsWritten: 0, lastQuoteAt: "2026-10-05T15:30:00Z", error: "yahoo_prices: Error: Yahoo answered 429" }],
    });
    render(<RouterProvider router={createTestRouter(["/dashboards/8"])} />);
    const status = await screen.findByText(/update failed/);
    expect(status).toHaveAttribute("title", "Yahoo: yahoo_prices: Error: Yahoo answered 429");
  });
});
