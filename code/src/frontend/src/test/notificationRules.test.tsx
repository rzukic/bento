import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { NotificationChannel, NotificationRule } from "../api/notifications";
import { RulesPanel } from "../features/notifications/RulesPanel";
import { resetSchemaStoreForTest, useSchemaStore } from "../stores/schemaStore";
import type { ModelSnapshot } from "../types/api";

const api = vi.hoisted(() => ({
  listRules: vi.fn(),
  listChannels: vi.fn(),
  createRule: vi.fn(),
  updateRule: vi.fn(),
  deleteRule: vi.fn(),
  checkRule: vi.fn(),
  testRule: vi.fn(),
}));
vi.mock("../api/notifications", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/notifications")>()), ...api }));
const dashboardsApi = vi.hoisted(() => ({ listDashboards: vi.fn() }));
vi.mock("../api/dashboards", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/dashboards")>()), ...dashboardsApi }));

const channel = { id: 3, name: "Slack", kind: "slack", target: "hooks.slack.com" } as NotificationChannel;

beforeEach(() => {
  resetSchemaStoreForTest();
  useSchemaStore.setState({
    status: "ready",
    snapshot: {
      version: 1,
      tables: [
        {
          name: "transactions",
          displayName: "Transactions",
          columns: [
            { name: "id", type: "integer", primaryKey: true },
            { name: "booked_on", type: "date" },
            { name: "amount", type: "currency" },
          ],
        },
      ],
      relations: [],
    } as unknown as ModelSnapshot,
    error: null,
    lastLoadedAt: Date.now(),
    inFlight: null,
  });
  api.listChannels.mockResolvedValue([channel]);
  api.listRules.mockResolvedValue([]);
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("notification rules", () => {
  it("adds a condition over this month's rows, by the table's date column", async () => {
    api.createRule.mockResolvedValue({});
    const user = userEvent.setup();
    render(<RulesPanel />);

    await user.click(await screen.findByRole("button", { name: "Add rule" }));
    await user.type(screen.getByLabelText("Name"), "Spending this month");
    await user.selectOptions(screen.getByLabelText("Table"), "transactions");
    await user.type(screen.getByLabelText("Value (formula)"), "-SUM(amount WHERE amount < 0)");
    await user.type(screen.getByLabelText("Number"), "1500,50");
    expect(screen.getByLabelText("By date in")).toHaveValue("booked_on");
    await user.click(screen.getAllByRole("button", { name: "Add rule" }).at(-1)!);

    await waitFor(() => expect(api.createRule).toHaveBeenCalledTimes(1));
    expect(api.createRule).toHaveBeenCalledWith(
      "Spending this month",
      "condition",
      {
        source: "transactions",
        formula: "-SUM(amount WHERE amount < 0)",
        period: "this-month",
        dateColumn: "booked_on",
        op: ">",
        threshold: 1500.5,
        every: "hour",
        at: null,
      },
      [3],
    );
  });

  it("offers the dashboards' budget widgets for a budget rule", async () => {
    dashboardsApi.listDashboards.mockResolvedValue([
      { id: 7, name: "Cash flow", dashboard: { widgets: [{ id: "budgets", type: "budget", title: "Budgets this month" }, { id: "k", type: "kpi" }] } },
    ]);
    api.createRule.mockResolvedValue({});
    const user = userEvent.setup();
    render(<RulesPanel />);

    await user.click(await screen.findByRole("button", { name: "Add rule" }));
    await user.selectOptions(screen.getByLabelText("Kind"), "budget");
    expect(await screen.findByRole("option", { name: "Cash flow · Budgets this month" })).toBeInTheDocument();
    await user.type(screen.getByLabelText("Name"), "Over budget");
    await user.clear(screen.getByLabelText("At % of the limit"));
    await user.type(screen.getByLabelText("At % of the limit"), "90");
    await user.click(screen.getAllByRole("button", { name: "Add rule" }).at(-1)!);

    await waitFor(() => expect(api.createRule).toHaveBeenCalledWith("Over budget", "budget", { dashboardId: 7, widgetId: "budgets", percent: 90 }, [3]));
  });

  it("checks a rule and says what it found", async () => {
    const rule = {
      id: 1,
      name: "Spending this month",
      kind: "condition",
      config: {},
      channels: [3],
      enabled: true,
      state: {},
      summary: "-SUM(amount) > 1000 · this month · hourly",
      createdAt: "",
      updatedAt: "",
    } as NotificationRule;
    api.listRules.mockResolvedValue([rule]);
    api.checkRule.mockResolvedValue({ state: { active: true, value: "1.205,00 €" }, fired: true, notification: { title: "Spending this month", text: "1.205,00 € — above 1.000,00 €." }, deliveries: [] });
    const user = userEvent.setup();
    render(<RulesPanel />);

    expect(await screen.findByText(/hourly → Slack/)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Check now" }));
    expect(await screen.findByText("It holds now and was told: “1.205,00 € — above 1.000,00 €.”")).toBeInTheDocument();
  });
});
