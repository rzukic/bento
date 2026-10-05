import { CompletionContext } from "@codemirror/autocomplete";
import { EditorState } from "@codemirror/state";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { CustomFunction, FunctionStatus } from "../api/functions";
import { formulaCompletions } from "../features/formulas/formulaLanguage";
import { seedSetupInitializedForTest } from "../features/setup";
import { createTestRouter } from "../router";
import { resetAppStoreForTest } from "../stores/appStore";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import { resetSchemaStoreForTest } from "../stores/schemaStore";

const api = vi.hoisted(() => ({
  listFunctions: vi.fn(),
  getFunctionStatus: vi.fn(),
  getFunctionVersions: vi.fn(),
  createFunction: vi.fn(),
  updateFunction: vi.fn(),
  deleteFunction: vi.fn(),
  testFunction: vi.fn(),
}));
vi.mock("../api/functions", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/functions")>()), ...api }));

const riskClass: CustomFunction = {
  name: "risk_class",
  description: "Low, medium or high",
  arguments: [{ name: "volatility", type: "float" }],
  resultType: "text",
  code: 'return volatility > 0.3 ? "high" : "low";',
  version: 2,
  createdAt: "2026-10-05T10:00:00Z",
  updatedAt: "2026-10-05T11:00:00Z",
  usedBy: [
    { kind: "column", name: "assets.risk", table: "assets", column: "risk" },
    { kind: "dashboard", name: "Wealth", dashboardId: 8 },
  ],
};

const idle: FunctionStatus = { pending: false, lastRunAt: "2026-10-05T11:00:01Z", lastUpdated: 0, failures: [] };

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
  api.listFunctions.mockResolvedValue([riskClass]);
  api.getFunctionStatus.mockResolvedValue(idle);
  vi.stubGlobal("fetch", vi.fn(() => Promise.reject(new Error("fetch must be mocked in tests"))));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe("Functions page", () => {
  it("lists functions, shows one with its usages, tests and saves a change", async () => {
    api.testFunction.mockResolvedValue({ value: "high", resultType: "text", error: null, logs: ["v 0.4"], durationMs: 0.4 });
    api.updateFunction.mockResolvedValue({ ...riskClass, version: 3 });
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/functions"])} />);

    await user.click(await screen.findByRole("button", { name: /risk_class/ }));
    expect(await screen.findByRole("heading", { name: "risk_class" })).toBeInTheDocument();
    expect(screen.getByText("Version 2")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "assets.risk" })).toHaveAttribute("href", "/data/assets");
    expect(screen.getByRole("link", { name: "Wealth" })).toHaveAttribute("href", "/dashboards/8");
    expect(screen.getByText('CUSTOM("risk_class", volatility)')).toBeInTheDocument();

    await user.type(screen.getByLabelText("Test value for volatility"), "0,4");
    await user.click(screen.getByRole("button", { name: "Run" }));
    expect(api.testFunction).toHaveBeenCalledWith(expect.objectContaining({ code: riskClass.code, resultType: "text" }), [0.4]);
    expect(await screen.findByText('"high"', { selector: ".function-test__result .mono" })).toBeInTheDocument();
    expect(screen.getByLabelText("console.log output")).toHaveTextContent("v 0.4");

    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
    await user.selectOptions(screen.getByLabelText("Result type"), "boolean");
    expect(screen.getByText("Unsaved changes")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() =>
      expect(api.updateFunction).toHaveBeenCalledWith("risk_class", expect.objectContaining({ resultType: "boolean", arguments: [{ name: "volatility", type: "float" }] })),
    );
  });

  it("creates a function with its arguments", async () => {
    api.listFunctions.mockResolvedValueOnce([]).mockResolvedValue([{ ...riskClass, name: "month_end", usedBy: [] }]);
    api.createFunction.mockResolvedValue({ ...riskClass, name: "month_end", usedBy: [] });
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/functions"])} />);

    await user.click(await screen.findByRole("button", { name: "Create your first function" }));
    await user.type(screen.getByLabelText("Function name"), "Month End");
    await user.clear(screen.getByLabelText("Name of argument 1"));
    await user.type(screen.getByLabelText("Name of argument 1"), "day");
    await user.selectOptions(screen.getByLabelText("Type of day"), "date");
    await user.selectOptions(screen.getByLabelText("Result type"), "date");
    await user.click(screen.getByRole("button", { name: "Create function" }));

    await waitFor(() =>
      expect(api.createFunction).toHaveBeenCalledWith(
        expect.objectContaining({ name: "month_end", arguments: [{ name: "day", type: "date" }], resultType: "date" }),
      ),
    );
    expect(await screen.findByRole("heading", { name: "month_end" })).toBeInTheDocument();
  });

  it("shows stored values that failed, and is read-only for viewers", async () => {
    api.getFunctionStatus.mockResolvedValue({
      ...idle,
      failures: [{ table: "assets", column: "risk", function: "risk_class", rows: 2, error: "TypeError: x is null (line 1)" }],
    });
    signIn("viewer");
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/functions?fn=risk_class"])} />);

    expect(await screen.findByText("assets.risk: failed on 2 rows")).toBeInTheDocument();
    expect(screen.getByText(/failed on 2 rows/, { selector: ".functions-list__failed" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Save" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "New" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Run" })).not.toBeInTheDocument();
    expect(screen.getByLabelText("Result type")).toBeDisabled();

    api.getFunctionVersions.mockResolvedValue([
      { version: 2, arguments: riskClass.arguments, resultType: "text", code: riskClass.code, createdAt: "2026-10-05T11:00:00Z" },
      { version: 1, arguments: riskClass.arguments, resultType: "text", code: "return 1;", createdAt: "2026-10-05T10:00:00Z" },
    ]);
    await user.click(screen.getByRole("button", { name: "Show versions" }));
    const history = await screen.findByRole("region", { name: "History" });
    expect(within(history).getByText("v1")).toBeInTheDocument();
    expect(within(history).getByText("current")).toBeInTheDocument();
  });
});

describe("Formula completion", () => {
  it("completes custom functions to a CUSTOM call", () => {
    const source = formulaCompletions([], [
      { name: "risk_class", category: "Custom", signature: 'CUSTOM("risk_class", volatility)', description: "", example: "", returns: "text", apply: 'CUSTOM("risk_class", ' },
    ]);
    const result = source(new CompletionContext(EditorState.create({ doc: "ris" }), 3, false));
    expect(result?.options.find((o) => o.label === "risk_class")?.apply).toBe('CUSTOM("risk_class", ');
  });
});
