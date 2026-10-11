import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { AiSettings, AiUsageRow } from "../api/ai";
import type { Dashboard, DashboardWidget } from "../api/dashboards";
import type { QueryResult } from "../api/query";
import { ApiClientError } from "../api/client";
import { seedSetupInitializedForTest } from "../features/setup";
import { createTestRouter } from "../router";
import { resetAppStoreForTest } from "../stores/appStore";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import { resetSchemaStoreForTest, useSchemaStore } from "../stores/schemaStore";
import type { ModelSnapshot } from "../types/api";

const aiApi = vi.hoisted(() => ({ getAiStatus: vi.fn(), explain: vi.fn(), getAiSettings: vi.fn(), saveAiSettings: vi.fn(), listAiUsage: vi.fn() }));
const queryApi = vi.hoisted(() => ({ runQuery: vi.fn(), listSavedQueries: vi.fn(), evaluateVariables: vi.fn() }));
const dashboardsApi = vi.hoisted(() => ({ getDashboard: vi.fn(), listDashboards: vi.fn() }));
vi.mock("../api/ai", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/ai")>()), ...aiApi }));
vi.mock("../api/query", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/query")>()), ...queryApi }));
vi.mock("../api/dashboards", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/dashboards")>()), ...dashboardsApi }));

const snapshot = {
  version: 1,
  tables: [
    {
      name: "transactions",
      columns: [
        { name: "id", type: "integer", primaryKey: true },
        { name: "date", type: "date" },
        { name: "amount", type: "currency" },
      ],
    },
  ],
  relations: [],
} as unknown as ModelSnapshot;

const kpi: DashboardWidget = {
  id: "spent",
  type: "kpi",
  title: "Spent in the range",
  position: { x: 0, y: 0, w: 3, h: 2 },
  data: { query: { source: "transactions", measures: [{ name: "spent", formula: "-SUM(amount WHERE amount < 0)" }] }, dateColumn: "date" },
};
const note: DashboardWidget = { id: "note", type: "text", title: "Notes", position: { x: 3, y: 0, w: 3, h: 2 }, display: { text: "Hello" } };
const dashboard: Dashboard = {
  id: 7,
  name: "Cash flow",
  isShared: true,
  isOwner: false,
  dashboard: { widgets: [kpi, note], dateRange: { from: "2026-09-01", to: "2026-09-30" } },
  createdAt: "",
  updatedAt: "",
};
// The dashboard page loads lazily; under a full parallel run that can take longer than findBy's default second.
const spentResult: QueryResult = { data: [{ spent: 2340 }], meta: { truncated: false, columns: [{ name: "spent", type: "currency", role: "measure" }] } };

function signIn(role: string) {
  useAuthStore.setState({ status: "authenticated", user: { id: 2, username: role, role }, accessToken: "token", expiresAt: Date.now() + 60_000, loginInFlight: false, error: null });
}

beforeEach(() => {
  resetAppStoreForTest();
  resetAuthStoreForTest();
  seedSetupInitializedForTest();
  resetSchemaStoreForTest();
  signIn("viewer");
  useSchemaStore.setState({ status: "ready", snapshot, error: null, lastLoadedAt: Date.now(), inFlight: null });
  dashboardsApi.getDashboard.mockResolvedValue(dashboard);
  queryApi.runQuery.mockResolvedValue(spentResult);
  queryApi.listSavedQueries.mockResolvedValue([]);
  queryApi.evaluateVariables.mockResolvedValue([]);
  aiApi.getAiStatus.mockResolvedValue({ enabled: true, configured: true, model: "claude-opus-5-5", budgetLeft: true });
  vi.stubGlobal("fetch", vi.fn(() => Promise.reject(new Error("fetch must be mocked in tests"))));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe("Explain this", () => {
  it("sends the widget with its range and numbers, shows the answer and takes a follow-up", async () => {
    aiApi.explain
      .mockResolvedValueOnce({ answer: "You spent **2.340 €**.\n- Rent: 1.000 €\n- Groceries: 410 €", model: "claude-opus-5-5", costUsd: 0.023, toolCalls: 3, durationMs: 8200 })
      .mockResolvedValueOnce({ answer: "Mostly the car repair.", model: "claude-opus-5-5", costUsd: 0.004, toolCalls: 1, durationMs: 3000 });
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/dashboards/7"])} />);

    await user.click(await screen.findByRole("button", { name: "Explain Spent in the range" }));
    // Text widgets have nothing to explain.
    expect(screen.queryByRole("button", { name: "Explain Notes" })).not.toBeInTheDocument();

    const panel = await screen.findByRole("complementary", { name: "Explain Spent in the range" });
    expect(await within(panel).findByText("2.340 €", { selector: "strong" })).toBeInTheDocument();
    expect(within(panel).getByText("Rent: 1.000 €")).toBeInTheDocument();
    expect(within(panel).getByText(/3 lookups · \$0\.02 · 8 s/)).toBeInTheDocument();

    const sent = aiApi.explain.mock.calls[0][0];
    expect(sent).toMatchObject({
      dashboardId: 7,
      dashboard: "Cash flow",
      title: "Spent in the range",
      type: "kpi",
      range: { from: "2026-09-01", to: "2026-09-30" },
      result: [{ spent: 2340 }],
    });
    expect(sent.language).toBeTruthy();
    // The query as it ran: the dashboard's range is in its filter.
    expect(JSON.stringify(sent.query)).toContain("2026-09-01");

    await user.type(within(panel).getByRole("textbox", { name: "Question about this widget" }), "Why so high?");
    await user.click(within(panel).getByRole("button", { name: "Ask" }));
    expect(await within(panel).findByText("Mostly the car repair.")).toBeInTheDocument();
    expect(within(panel).getByText("Why so high?")).toBeInTheDocument();
    expect(aiApi.explain.mock.calls[1][0]).toMatchObject({ question: "Why so high?", title: "Spent in the range" });

    await user.click(within(panel).getByRole("button", { name: "Close explanation" }));
    await waitFor(() => expect(screen.queryByRole("complementary", { name: "Explain Spent in the range" })).not.toBeInTheDocument());
  });

  it("says why it cannot explain, e.g. when the budget is used up", async () => {
    aiApi.explain.mockRejectedValue(new ApiClientError("This month's AI budget is used up ($5.00 of $5.00).", 422));
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/dashboards/7"])} />);
    await user.click(await screen.findByRole("button", { name: "Explain Spent in the range" }));
    expect(await screen.findByText(/budget is used up/)).toBeInTheDocument();
  });

  it("shows no Explain buttons while the AI is not set up", async () => {
    aiApi.getAiStatus.mockResolvedValue({ enabled: false, configured: false, model: "claude-opus-5-5", budgetLeft: true });
    render(<RouterProvider router={createTestRouter(["/dashboards/7"])} />);
    expect(await screen.findByRole("heading", { name: "Spent in the range" })).toBeInTheDocument();
    await waitFor(() => expect(aiApi.getAiStatus).toHaveBeenCalled());
    expect(screen.queryByRole("button", { name: /^Explain/ })).not.toBeInTheDocument();
  });
});

describe("AI page", () => {
  const settings: AiSettings = {
    configured: false,
    enabled: false,
    model: "claude-opus-5-5",
    monthlyLimitUsd: 10,
    spentThisMonthUsd: 0,
    models: [
      { id: "claude-opus-5-5", name: "Claude Opus 5.5", description: "Most capable" },
      { id: "claude-sonnet-5-5", name: "Claude Sonnet 5.5", description: "Fast" },
    ],
  };
  const usage: AiUsageRow[] = [
    { id: 1, calledAt: "2026-10-09T12:00:00.000Z", userId: 2, username: "ruben", feature: "explain", model: "claude-opus-5-5", inputTokens: 1200, outputTokens: 300, cacheReadTokens: 6000, cacheWriteTokens: 0, costUsd: 0.0123, toolCalls: 2, ok: true, error: null, durationMs: 6400 },
  ];

  it("lets an admin store the key, pick a model and a limit, and lists the calls", async () => {
    signIn("admin");
    aiApi.getAiSettings.mockResolvedValue(settings);
    aiApi.listAiUsage.mockResolvedValue(usage);
    aiApi.getAiStatus.mockResolvedValue({ enabled: false, configured: false, model: "claude-opus-5-5", budgetLeft: true });
    aiApi.saveAiSettings
      .mockResolvedValueOnce({ ...settings, configured: true, enabled: true })
      .mockResolvedValueOnce({ ...settings, configured: true, enabled: true, model: "claude-sonnet-5-5", monthlyLimitUsd: 5, spentThisMonthUsd: 0.01 });
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/ai"])} />);

    await user.type(await screen.findByLabelText(/Anthropic API key/), "sk-ant-test");
    await user.click(screen.getByRole("button", { name: "Save key" }));
    expect(aiApi.saveAiSettings).toHaveBeenCalledWith({ apiKey: "sk-ant-test" });
    expect(await screen.findByText("The API key is stored.")).toBeInTheDocument();
    expect(screen.getByLabelText(/Anthropic API key/)).toHaveValue("");

    await user.selectOptions(screen.getByLabelText("Model"), "claude-sonnet-5-5");
    const limit = screen.getByLabelText("Monthly limit (US$)");
    await user.clear(limit);
    await user.type(limit, "5");
    await user.click(screen.getByRole("button", { name: "Save" }));
    expect(aiApi.saveAiSettings).toHaveBeenLastCalledWith({ model: "claude-sonnet-5-5", monthlyLimitUsd: 5 });
    expect(await screen.findByText(/\$0\.01 of \$5\.00 used this month/)).toBeInTheDocument();

    const row = screen.getByRole("row", { name: /Explain/ });
    expect(within(row).getByText("ruben")).toBeInTheDocument();
    expect(within(row).getByText("$0.01")).toBeInTheDocument();
  });

  it("tells a viewer who sets it up", async () => {
    aiApi.getAiStatus.mockResolvedValue({ enabled: false, configured: false, model: "claude-opus-5-5", budgetLeft: true });
    aiApi.listAiUsage.mockResolvedValue([]);
    render(<RouterProvider router={createTestRouter(["/ai"])} />);
    expect(await screen.findByText(/An admin sets up the AI/)).toBeInTheDocument();
    expect(aiApi.getAiSettings).not.toHaveBeenCalled();
  });
});
