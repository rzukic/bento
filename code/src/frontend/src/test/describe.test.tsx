import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Dashboard, DashboardDocument, DashboardVersion, DashboardWidget } from "../api/dashboards";
import { DescribeFormula } from "../features/ai/DescribeFormula";
import { forgetAiStatus } from "../features/ai/useAiEnabled";
import { seedSetupInitializedForTest } from "../features/setup";
import { createTestRouter } from "../router";
import { resetAppStoreForTest } from "../stores/appStore";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import { resetSchemaStoreForTest, useSchemaStore } from "../stores/schemaStore";
import type { ModelSnapshot } from "../types/api";

const aiApi = vi.hoisted(() => ({ getAiStatus: vi.fn(), describeWidget: vi.fn(), describeFormula: vi.fn(), listAiUsage: vi.fn(), getAiSettings: vi.fn() }));
const queryApi = vi.hoisted(() => ({ runQuery: vi.fn(), listSavedQueries: vi.fn(), evaluateVariables: vi.fn() }));
const dashboardsApi = vi.hoisted(() => ({
  getDashboard: vi.fn(),
  listDashboards: vi.fn(),
  updateDashboard: vi.fn(),
  listDashboardVersions: vi.fn(),
  restoreDashboardVersion: vi.fn(),
  listDeletedDashboards: vi.fn(),
  restoreDeletedDashboard: vi.fn(),
  listDashboardChanges: vi.fn(),
}));
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
        { name: "category_id", type: "reference", references: "categories" },
      ],
    },
  ],
  relations: [],
} as unknown as ModelSnapshot;

function dashboard(document: DashboardDocument, extra: Partial<Dashboard> = {}): Dashboard {
  return { id: 7, name: "Cash flow", isShared: false, isOwner: true, dashboard: document, createdAt: "", updatedAt: "", ...extra };
}

const built: DashboardWidget = {
  id: "ignored",
  type: "bar",
  title: "Expenses by main category",
  position: { x: 0, y: 0, w: 6, h: 4 },
  data: { dateColumn: "date", query: { source: "transactions", groupBy: [{ column: "category_id" }], measures: [{ name: "spent", fn: "sum", column: "amount" }] } },
};

beforeEach(() => {
  resetAppStoreForTest();
  resetAuthStoreForTest();
  seedSetupInitializedForTest();
  resetSchemaStoreForTest();
  forgetAiStatus();
  useAuthStore.setState({ status: "authenticated", user: { id: 1, username: "ruben", role: "admin" }, accessToken: "token", expiresAt: Date.now() + 60_000, loginInFlight: false, error: null });
  useSchemaStore.setState({ status: "ready", snapshot, error: null, lastLoadedAt: Date.now(), inFlight: null });
  aiApi.getAiStatus.mockResolvedValue({ enabled: true, configured: true, model: "claude-opus-5-5", budgetLeft: true });
  queryApi.runQuery.mockResolvedValue({ data: [], meta: { truncated: false, columns: [] } });
  queryApi.listSavedQueries.mockResolvedValue([]);
  queryApi.evaluateVariables.mockResolvedValue([]);
  dashboardsApi.listDashboardVersions.mockResolvedValue([]);
  dashboardsApi.listDeletedDashboards.mockResolvedValue([]);
  dashboardsApi.listDashboardChanges.mockResolvedValue([]);
  vi.stubGlobal("fetch", vi.fn(() => Promise.reject(new Error("fetch must be mocked in tests"))));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe("Describe a widget", () => {
  it("builds the widget from a sentence, keeps its place, undoes, and notes it in the saved history", async () => {
    dashboardsApi.getDashboard.mockResolvedValue(dashboard({ dateRange: { from: "2026-07-01", to: "2026-09-30" }, widgets: [] }));
    dashboardsApi.updateDashboard.mockImplementation(async (_id: number, changes: { dashboard: DashboardDocument }) => dashboard(changes.dashboard));
    aiApi.describeWidget.mockImplementation(async (request: { widget: DashboardWidget }) => ({
      widget: { ...built, id: request.widget.id, position: request.widget.position },
      formula: null,
      note: "Spending per main category, rent left out.",
      model: "claude-opus-5-5",
      costUsd: 0.031,
      toolCalls: 4,
      durationMs: 9000,
    }));
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/dashboards/7"])} />);

    await user.click(await screen.findByRole("button", { name: "Add widgets" }));
    await user.click(screen.getByRole("button", { name: "Add widget" }));
    await user.click(screen.getByRole("menuitem", { name: "Key figure" }));
    const editor = await screen.findByRole("dialog", { name: "Edit widget" });

    // Pasted, not typed: typing a sentence key by key re-renders the editor for each character, slow under parallel load.
    await user.click(await within(editor).findByRole("textbox", { name: "Describe the widget" }));
    await user.paste("Expenses by main category, without rent");
    await user.click(within(editor).getByRole("button", { name: "Build" }));

    expect(await within(editor).findByText("Spending per main category, rent left out.")).toBeInTheDocument();
    const sent = aiApi.describeWidget.mock.calls[0][0];
    expect(sent).toMatchObject({ sentence: "Expenses by main category, without rent", dashboard: "Cash flow", range: { from: "2026-07-01" } });
    expect(sent.widget.type).toBe("kpi");
    // The built widget is in the editor now, with its id and place.
    expect(within(editor).getByLabelText("Title")).toHaveValue("Expenses by main category");
    expect(within(editor).getByRole("radio", { name: "Bar chart" })).toBeChecked();

    // Undo, then build again and keep it.
    await user.click(within(editor).getByRole("button", { name: "Undo" }));
    expect(within(editor).getByRole("radio", { name: "Key figure" })).toBeChecked();
    await user.click(within(editor).getByRole("button", { name: "Build" }));
    await waitFor(() => expect(within(editor).getByRole("radio", { name: "Bar chart" })).toBeChecked());

    await user.click(within(editor).getByRole("button", { name: "Done" }));
    await user.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(dashboardsApi.updateDashboard).toHaveBeenCalled());
    const saved = dashboardsApi.updateDashboard.mock.calls[0][1];
    expect(saved.note).toBe("AI: Expenses by main category, without rent · Expenses by main category, without rent");
    expect(saved.dashboard.widgets[0]).toMatchObject({ type: "bar", title: "Expenses by main category", id: sent.widget.id });
  });

  it("shows no ✨ field while the AI is off", async () => {
    aiApi.getAiStatus.mockResolvedValue({ enabled: false, configured: true, model: "claude-opus-5-5", budgetLeft: true });
    dashboardsApi.getDashboard.mockResolvedValue(dashboard({ widgets: [] }));
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/dashboards/7"])} />);
    await user.click(await screen.findByRole("button", { name: "Add widgets" }));
    await user.click(screen.getByRole("button", { name: "Add widget" }));
    await user.click(screen.getByRole("menuitem", { name: "Key figure" }));
    const editor = await screen.findByRole("dialog", { name: "Edit widget" });
    await waitFor(() => expect(aiApi.getAiStatus).toHaveBeenCalled());
    expect(within(editor).queryByRole("textbox", { name: "Describe the widget" })).not.toBeInTheDocument();
  });
});

describe("Describe a formula", () => {
  it("writes the formula into the field and can undo it", async () => {
    aiApi.describeFormula.mockResolvedValue({ widget: null, formula: "AVG(price WHERE kind = \"buy\")", note: "Average price of the buys.", model: "m", costUsd: 0.01, toolCalls: 2, durationMs: 3000 });
    const onFormula = vi.fn();
    const user = userEvent.setup();
    render(<DescribeFormula table="trades" kind="measure" current="SUM(price)" onFormula={onFormula} />);

    await user.click(await screen.findByRole("button", { name: /Describe it instead/ }));
    await user.click(screen.getByRole("textbox", { name: "Describe the formula" }));
    await user.paste("average cost per share");
    await user.keyboard("{Enter}");
    expect(await screen.findByText("Average price of the buys.")).toBeInTheDocument();
    expect(aiApi.describeFormula).toHaveBeenCalledWith(expect.objectContaining({ table: "trades", kind: "measure", sentence: "average cost per share", current: "SUM(price)" }));
    expect(onFormula).toHaveBeenLastCalledWith("AVG(price WHERE kind = \"buy\")");

    await user.click(screen.getByRole("button", { name: "Undo" }));
    expect(onFormula).toHaveBeenLastCalledWith("SUM(price)");
  });
});

describe("History", () => {
  const versions: DashboardVersion[] = [
    { id: 12, dashboardId: 7, name: "Cash flow", widgets: 4, action: "update", changedBy: "ruben", via: "Claude Desktop", note: null, createdAt: "2026-10-09T18:00:00.000Z" },
    { id: 11, dashboardId: 7, name: "Cash flow", widgets: 3, action: "update", changedBy: "ruben", via: null, note: "AI: add a donut", createdAt: "2026-10-09T17:00:00.000Z" },
  ];

  it("lists who changed the dashboard and restores an earlier state", async () => {
    dashboardsApi.getDashboard.mockResolvedValue(dashboard({ widgets: [] }));
    dashboardsApi.listDashboardVersions.mockResolvedValue(versions);
    dashboardsApi.restoreDashboardVersion.mockResolvedValue(dashboard({ widgets: [] }, { name: "Cash flow (before)" }));
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/dashboards/7"])} />);

    await user.click(await screen.findByRole("button", { name: "History" }));
    const dialog = await screen.findByRole("dialog", { name: "History" });
    expect(await within(dialog).findByText("Changed by Claude Desktop")).toBeInTheDocument();
    expect(within(dialog).getByText("Changed by you")).toBeInTheDocument();
    expect(within(dialog).getByText("AI: add a donut")).toBeInTheDocument();

    await user.click(within(dialog).getAllByRole("button", { name: "Restore" })[0]);
    expect(dashboardsApi.restoreDashboardVersion).toHaveBeenCalledWith(7, 12);
    expect(await screen.findByText(/Restored\. The state before is in the history/)).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Cash flow (before)" })).toBeInTheDocument();
  });

  it("brings back a deleted dashboard from the list", async () => {
    dashboardsApi.listDashboards.mockResolvedValue([]);
    dashboardsApi.listDeletedDashboards.mockResolvedValue([{ ...versions[0], id: 20, action: "delete" }]);
    dashboardsApi.restoreDeletedDashboard.mockResolvedValue(dashboard({ widgets: [] }));
    dashboardsApi.getDashboard.mockResolvedValue(dashboard({ widgets: [] }));
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/dashboards"])} />);

    const section = await screen.findByRole("region", { name: "Recently deleted" });
    expect(within(section).getByText(/Deleted by Claude Desktop/)).toBeInTheDocument();
    await user.click(within(section).getByRole("button", { name: "Bring back" }));
    expect(dashboardsApi.restoreDeletedDashboard).toHaveBeenCalledWith(20);
  });

  it("lists what AI apps changed on the AI page, with undo", async () => {
    aiApi.getAiSettings.mockResolvedValue({ configured: true, enabled: true, model: "claude-opus-5-5", monthlyLimitUsd: 10, spentThisMonthUsd: 0, models: [] });
    aiApi.listAiUsage.mockResolvedValue([]);
    dashboardsApi.listDashboardChanges.mockResolvedValue([versions[0], { ...versions[0], id: 21, action: "delete", name: "Old" }]);
    dashboardsApi.restoreDashboardVersion.mockResolvedValue(dashboard({ widgets: [] }));
    dashboardsApi.restoreDeletedDashboard.mockResolvedValue(dashboard({ widgets: [] }));
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/ai"])} />);

    const section = await screen.findByRole("region", { name: "Changes by AI apps" });
    const rows = within(section).getAllByRole("row").slice(1);
    await user.click(within(rows[0]).getByRole("button", { name: "Undo" }));
    expect(dashboardsApi.restoreDashboardVersion).toHaveBeenCalledWith(7, 12);
    expect(await within(rows[0]).findByText("undone")).toBeInTheDocument();
    await user.click(within(rows[1]).getByRole("button", { name: "Undo" }));
    expect(dashboardsApi.restoreDeletedDashboard).toHaveBeenCalledWith(21);
  });
});
