import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Dashboard } from "../api/dashboards";
import type { NotificationChannel, NotificationRule } from "../api/notifications";
import { ReportsPanel } from "../features/notifications/ReportsPanel";
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
  previewReport: vi.fn(),
}));
vi.mock("../api/notifications", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/notifications")>()), ...api }));
const dashboardsApi = vi.hoisted(() => ({ listDashboards: vi.fn() }));
vi.mock("../api/dashboards", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/dashboards")>()), ...dashboardsApi }));

const channel = { id: 3, name: "Team mail", kind: "email", target: "team@example.com" } as NotificationChannel;

const support = {
  id: 7,
  name: "Support",
  dashboard: {
    widgets: [
      { id: "opened", type: "kpi", title: "Opened", position: { x: 0, y: 0, w: 3, h: 2 }, data: { query: { source: "tickets", measures: [{ name: "n", formula: "COUNT()" }] } } },
      { id: "avg", type: "kpi", title: "Average minutes", position: { x: 3, y: 0, w: 3, h: 2 }, data: { variable: "avg" } },
      { id: "plan", type: "kpi", title: "Plan", position: { x: 6, y: 0, w: 3, h: 2 }, data: { query: { source: "tickets" }, range: { preset: "next-12-months" } } },
      { id: "trend", type: "line", title: "Trend", position: { x: 0, y: 2, w: 6, h: 4 }, data: { query: { source: "tickets" } } },
    ],
  },
} as unknown as Dashboard;

beforeEach(() => {
  resetSchemaStoreForTest();
  useSchemaStore.setState({
    status: "ready",
    snapshot: {
      version: 1,
      tables: [
        {
          name: "tickets",
          displayName: "Tickets",
          columns: [
            { name: "id", type: "integer", primaryKey: true },
            { name: "opened_on", type: "date" },
            { name: "minutes", type: "integer" },
            { name: "team_id", type: "reference" },
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
  dashboardsApi.listDashboards.mockResolvedValue([support]);
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("reports", () => {
  it("puts a weekly report together from blocks of any table and dashboard, previews and saves it", async () => {
    api.createRule.mockResolvedValue({});
    api.previewReport.mockResolvedValue({
      event: "report",
      title: "Support week · Week 41 (5–11 Oct 2026)",
      text: "Week 41 (5–11 Oct 2026), compared with Week 40 (28 Sep – 4 Oct 2026).",
      sections: [{ title: null, facts: [{ label: "Tickets", value: "12", detail: "+3 (+33%) vs. Week 40", tone: "good" }] }],
    });
    const user = userEvent.setup();
    render(<ReportsPanel />);

    await user.click(await screen.findByRole("button", { name: "Add report" }));
    await user.type(screen.getByLabelText("Name"), "Support week");
    await user.selectOptions(screen.getByLabelText("Send"), "week");
    await user.selectOptions(screen.getByLabelText("On"), "1");
    expect(screen.getByLabelText("About")).toHaveDisplayValue("The week before");

    const add = screen.getByRole("group", { name: "Add a block" });
    await user.click(within(add).getByRole("button", { name: "Value" }));
    await user.type(screen.getByLabelText("Block 1 label"), "Tickets");
    await user.selectOptions(screen.getByLabelText("Table (block 1)"), "tickets");
    expect(screen.getByLabelText("Period by (block 1)")).toHaveValue("opened_on");
    await user.type(screen.getByLabelText("Value (formula) (block 1)"), "COUNT()");

    await user.click(within(add).getByRole("button", { name: "Top list" }));
    await user.selectOptions(screen.getByLabelText("Table (block 2)"), "tickets");
    await user.type(screen.getByLabelText("Value (formula) (block 2)"), "SUM(minutes)");
    await user.selectOptions(screen.getByLabelText("Per (block 2)"), "team_id");
    await user.selectOptions(screen.getByLabelText("Show (block 2)"), "desc:3");
    await user.selectOptions(screen.getByLabelText("More is (block 2)"), "bad");

    await user.click(within(add).getByRole("button", { name: "Dashboard figures" }));
    // Only key figures that follow the period: not the forecast, not the chart.
    await user.selectOptions(screen.getByLabelText("Dashboard (block 3)"), "7");
    expect(screen.getByRole("checkbox", { name: "Opened" })).toBeChecked();
    expect(screen.queryByRole("checkbox", { name: "Plan" })).toBeNull();
    await user.click(screen.getByRole("checkbox", { name: "Average minutes" }));

    await user.click(within(add).getByRole("button", { name: "Text" }));
    await user.type(screen.getByLabelText("Text (block 4)"), "Have a good week.");
    // The text goes first.
    await user.click(screen.getByRole("button", { name: "Move block 4 up" }));
    await user.click(screen.getByRole("button", { name: "Move block 3 up" }));
    await user.click(screen.getByRole("button", { name: "Move block 2 up" }));

    await user.click(screen.getByRole("checkbox", { name: /Let the AI write/ }));
    await user.type(screen.getByLabelText("For the AI (optional)"), "Auf Deutsch");

    await user.click(screen.getByRole("button", { name: "Preview" }));
    const preview = await screen.findByRole("article", { name: "Report preview" });
    expect(within(preview).getByText("+3 (+33%) vs. Week 40")).toHaveClass("report-preview__detail--good");

    await user.click(screen.getAllByRole("button", { name: "Add report" }).at(-1)!);
    await waitFor(() => expect(api.createRule).toHaveBeenCalledTimes(1));
    const expected = {
      every: "week",
      day: 1,
      at: "08:00",
      range: "last",
      compare: "previous",
      intro: null,
      ai: true,
      aiInstructions: "Auf Deutsch",
      blocks: [
        { type: "text", label: null, text: "Have a good week." },
        { type: "value", label: "Tickets", source: "tickets", formula: "COUNT()", dateColumn: "opened_on", format: "auto", higherIsBetter: true },
        {
          type: "top",
          label: null,
          source: "tickets",
          formula: "SUM(minutes)",
          dateColumn: "opened_on",
          format: "auto",
          higherIsBetter: false,
          groupBy: "team_id",
          limit: 3,
          order: "desc",
        },
        { type: "dashboard", label: null, dashboardId: 7, widgets: ["opened"] },
      ],
    };
    expect(api.previewReport).toHaveBeenCalledWith("Support week", expected);
    expect(api.createRule).toHaveBeenCalledWith("Support week", "report", expected, [3]);
  });

  it("lists only reports, previews one and sends it now", async () => {
    const report = {
      id: 5,
      name: "Monthly review",
      kind: "report",
      config: { every: "month", day: 1, at: "08:00", blocks: [{ type: "rules" }] },
      channels: [3],
      enabled: true,
      state: { checkedAt: "2026-10-01T06:00:00Z", value: "September 2026" },
      summary: "Monthly on the 1st at 08:00 · the last month against the month before · 1 block",
      createdAt: "",
      updatedAt: "",
    } as unknown as NotificationRule;
    const condition = { ...report, id: 6, name: "Too many tickets", kind: "condition" } as NotificationRule;
    api.listRules.mockResolvedValue([report, condition]);
    api.checkRule.mockResolvedValue({
      state: report.state,
      fired: false,
      deliveries: [],
      notification: { event: "report", title: "Monthly review · September 2026", text: "September 2026.", sections: [{ title: "Rules", text: "No rule holds right now." }] },
    });
    api.testRule.mockResolvedValue({ state: report.state, fired: true, deliveries: [{ status: "sent" }], notification: { title: "Test: Monthly review · September 2026" } });
    const user = userEvent.setup();
    render(<ReportsPanel />);

    expect(await screen.findByText("Monthly review")).toBeInTheDocument();
    expect(screen.queryByText("Too many tickets")).toBeNull();
    expect(screen.getByText(/Monthly on the 1st at 08:00/)).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Preview" }));
    expect(await screen.findByText("No rule holds right now.")).toBeInTheDocument();
    expect(api.checkRule).toHaveBeenCalledWith(5);

    await user.click(screen.getByRole("button", { name: "Send now" }));
    expect(await screen.findByRole("status")).toHaveTextContent("Sent: “Test: Monthly review · September 2026”");
  });
});
