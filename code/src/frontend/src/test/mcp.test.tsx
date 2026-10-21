import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { McpCall, McpConnection } from "../api/mcp";
import { seedSetupInitializedForTest } from "../features/setup";
import { createTestRouter } from "../router";
import { resetAppStoreForTest } from "../stores/appStore";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import { resetSchemaStoreForTest } from "../stores/schemaStore";

const api = vi.hoisted(() => ({
  getMcpInfo: vi.fn(),
  listMcpConnections: vi.fn(),
  revokeMcpConnection: vi.fn(),
  listMcpCalls: vi.fn(),
  listMcpConnectionCalls: vi.fn(),
  updateMcpConnection: vi.fn(),
  getAuthorizeInfo: vi.fn(),
  decideAuthorization: vi.fn(),
}));
vi.mock("../api/mcp", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/mcp")>()), ...api }));

const connection: McpConnection = {
  id: 4,
  clientName: "Claude",
  username: "admin",
  scope: "read-write",
  permissions: ["data.read", "data.write", "schema.write"],
  allowed: ["data.read", "data.write", "data.delete", "dashboards.write", "forms.write", "schema.write", "functions.write"],
  createdAt: "2026-10-09T12:00:00.000Z",
  lastUsedAt: "2026-10-09T12:05:00.000Z",
};

const calls: McpCall[] = [
  {
    id: 2,
    calledAt: "2026-10-09T12:05:00.000Z",
    userId: 1,
    username: "admin",
    via: "oauth",
    credential: "Claude",
    tool: "create_row",
    arguments: '{"table":"transactions","values":{"amount":-12.4}}',
    ok: true,
    error: null,
    durationMs: 31,
  },
  {
    id: 1,
    calledAt: "2026-10-09T12:04:00.000Z",
    userId: 1,
    username: "admin",
    via: "api_key",
    credential: "API key #3",
    tool: "list_rows",
    arguments: '{"table":"nope"}',
    ok: false,
    error: "Error 404: Table 'nope' not found.",
    durationMs: 9,
  },
];

function signIn(role: string, status: "authenticated" | "unauthenticated" = "authenticated") {
  useAuthStore.setState({
    status,
    user: status === "authenticated" ? { id: 1, username: "ruben", role } : null,
    accessToken: status === "authenticated" ? "token" : null,
    expiresAt: Date.now() + 60_000,
    loginInFlight: false,
    error: null,
  });
}

const consentUrl =
  "/oauth/authorize?response_type=code&client_id=bento_client_x&redirect_uri=" +
  encodeURIComponent("https://claude.ai/api/mcp/auth_callback") +
  "&code_challenge=abcdefghijklmnopqrstuvwxyzabcdefghijklmnopq&code_challenge_method=S256&state=st4te&scope=read%20write";

beforeEach(() => {
  resetAppStoreForTest();
  resetAuthStoreForTest();
  seedSetupInitializedForTest();
  resetSchemaStoreForTest();
  signIn("admin");
  api.getMcpInfo.mockResolvedValue({ endpoint: "https://bento.example.com/mcp", oauth: true, canWrite: true });
  api.listMcpConnections.mockResolvedValue([connection]);
  api.listMcpCalls.mockResolvedValue(calls);
  vi.stubGlobal("fetch", vi.fn(() => Promise.reject(new Error("fetch must be mocked in tests"))));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe("Connections → AI apps", () => {
  it("opens a connected app: changes what it may do and lists its calls", async () => {
    api.listMcpConnectionCalls.mockResolvedValue([calls[0], { ...calls[1], via: "oauth", credential: "Claude" }]);
    api.updateMcpConnection.mockImplementation(async (_id: number, permissions: string[]) => ({ ...connection, permissions }));
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/connections?tab=claude"])} />);

    await user.click(await screen.findByRole("button", { name: "Open Claude" }));
    const dialog = await screen.findByRole("dialog", { name: "Claude" });
    expect(api.listMcpConnectionCalls).toHaveBeenCalledWith(4);
    expect(await within(dialog).findByRole("heading", { name: "Calls (2, 1 failed)" })).toBeInTheDocument();

    // A call opens to its full arguments and error.
    await user.click(within(dialog).getByRole("button", { name: "Show details of list_rows" }));
    expect(within(dialog).getByText(/"table": "nope"/)).toBeInTheDocument();
    expect(within(dialog).getAllByText("Error 404: Table 'nope' not found.").length).toBeGreaterThan(0);

    // Take the schema away, allow deleting rows.
    const save = within(dialog).getByRole("button", { name: "Save permissions" });
    expect(save).toBeDisabled();
    await user.click(within(dialog).getByRole("checkbox", { name: /Tables and columns/ }));
    await user.click(within(dialog).getByRole("checkbox", { name: /Delete rows/ }));
    await user.click(save);
    expect(api.updateMcpConnection).toHaveBeenCalledWith(4, ["data.read", "data.write", "data.delete"], undefined);
    expect(await within(dialog).findByText(/applies to the app's next call/)).toBeInTheDocument();

    // The table behind the dialog shows the new access.
    await user.click(within(dialog).getByRole("button", { name: "Close" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(screen.getByText("Delete rows", { selector: ".mcp__permission" })).toBeInTheDocument();
    expect(screen.queryByText("Tables and columns", { selector: ".mcp__permission" })).not.toBeInTheDocument();
  });

  it("shows the address, the setup for Claude, ChatGPT and Codex, connected apps and calls", async () => {
    render(<RouterProvider router={createTestRouter(["/connections?tab=ai"])} />);

    expect(await screen.findByText("https://bento.example.com/mcp")).toBeInTheDocument();
    expect(screen.getByText("claude mcp add --transport http bento https://bento.example.com/mcp")).toBeInTheDocument();
    expect(screen.getByText("codex mcp add bento --url https://bento.example.com/mcp")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "ChatGPT" })).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: "AI apps" })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByRole("link", { name: /download bento\.mcpb/i })).toHaveAttribute("href", "/api/v1/mcp/bento.mcpb");

    expect(await screen.findByText("Claude", { selector: "strong" })).toBeInTheDocument();
    expect(screen.getByText("Add and change rows")).toBeInTheDocument();
    expect(screen.getByText("Tables and columns")).toHaveClass("mcp__permission--danger");

    expect(screen.getByText("create_row")).toBeInTheDocument();
    expect(screen.getByText("API key #3")).toBeInTheDocument();
    expect(screen.getByText("Error 404: Table 'nope' not found.")).toBeInTheDocument();
  });

  it("disconnects an app after confirmation", async () => {
    api.revokeMcpConnection.mockResolvedValue(undefined);
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/connections?tab=claude"])} />);

    await user.click(await screen.findByRole("button", { name: "Disconnect" }));
    const dialog = await screen.findByRole("dialog");
    await user.click(within(dialog).getByRole("button", { name: "Disconnect" }));
    await waitFor(() => expect(api.revokeMcpConnection).toHaveBeenCalledWith(4));
  });
});

describe("OAuth consent page", () => {
  it("asks for access and sends the user back with the decision", async () => {
    api.getAuthorizeInfo.mockResolvedValue({
      clientName: "Claude",
      redirectHost: "claude.ai",
      canWrite: true,
      allowed: ["data.read", "data.write", "data.delete", "dashboards.write", "forms.write", "schema.write", "functions.write"],
    });
    api.decideAuthorization.mockResolvedValue("https://claude.ai/api/mcp/auth_callback?code=c0de&state=st4te");
    const assign = vi.fn();
    vi.stubGlobal("location", { ...window.location, assign });
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter([consentUrl])} />);

    expect(await screen.findByText("claude.ai")).toBeInTheDocument();
    expect(api.getAuthorizeInfo).toHaveBeenCalledWith("bento_client_x", "https://claude.ai/api/mcp/auth_callback", null);
    // The client asked for write: adding rows is preselected, the rest is up to the user; reading is always in.
    expect(screen.getByRole("checkbox", { name: /read everything/i })).toBeChecked();
    expect(screen.getByRole("checkbox", { name: /read everything/i })).toBeDisabled();
    expect(screen.getByRole("checkbox", { name: /add and change rows/i })).toBeChecked();
    expect(screen.getByRole("checkbox", { name: /tables and columns/i })).not.toBeChecked();
    await user.click(screen.getByRole("checkbox", { name: /dashboards and saved queries/i }));
    await user.click(screen.getByRole("checkbox", { name: /add and change rows/i }));
    await user.click(screen.getByRole("button", { name: /allow/i }));

    await waitFor(() =>
      expect(api.decideAuthorization).toHaveBeenCalledWith({
        clientId: "bento_client_x",
        redirectUri: "https://claude.ai/api/mcp/auth_callback",
        codeChallenge: "abcdefghijklmnopqrstuvwxyzabcdefghijklmnopq",
        codeChallengeMethod: "S256",
        permissions: ["data.read", "dashboards.write"],
        state: "st4te",
        approve: true,
      }),
    );
    expect(assign).toHaveBeenCalledWith("https://claude.ai/api/mcp/auth_callback?code=c0de&state=st4te");
  });

  it("never ticks deleting rows or the schema in advance, even when the app asks for everything", async () => {
    api.getAuthorizeInfo.mockResolvedValue({
      clientName: "Codex",
      redirectHost: "127.0.0.1:52363",
      canWrite: true,
      allowed: ["data.read", "data.write", "data.delete", "dashboards.write", "forms.write", "schema.write", "functions.write"],
    });
    render(<RouterProvider router={createTestRouter([consentUrl.replace("scope=read%20write", "scope=data.read+data.write+data.delete+dashboards.write+forms.write+schema.write+functions.write")])} />);

    expect(await screen.findByRole("checkbox", { name: /add and change rows/i })).toBeChecked();
    expect(screen.getByRole("checkbox", { name: /functions and price sources/i })).toBeChecked();
    expect(screen.getByRole("checkbox", { name: /delete rows/i })).not.toBeChecked();
    expect(screen.getByRole("checkbox", { name: /tables and columns/i })).not.toBeChecked();
  });

  it("offers a viewer only what the role allows, and All picks those", async () => {
    signIn("viewer");
    api.getAuthorizeInfo.mockResolvedValue({ clientName: "Claude", redirectHost: "claude.ai", canWrite: false, allowed: ["data.read", "dashboards.write"] });
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter([consentUrl])} />);

    expect(await screen.findAllByText("Needs the admin role.")).toHaveLength(2);
    expect(screen.getAllByText("Needs the editor role.")).toHaveLength(3);
    expect(screen.getByRole("checkbox", { name: /add and change rows/i })).toBeDisabled();
    expect(screen.getByRole("checkbox", { name: /add and change rows/i })).not.toBeChecked();
    await user.click(screen.getByRole("button", { name: "All" }));
    expect(screen.getByRole("checkbox", { name: /dashboards and saved queries/i })).toBeChecked();
    expect(screen.getByRole("checkbox", { name: /tables and columns/i })).not.toBeChecked();
  });

  it("refuses an incomplete link without asking the server", async () => {
    render(<RouterProvider router={createTestRouter(["/oauth/authorize?client_id=x"])} />);
    expect(await screen.findByText(/link is incomplete/i)).toBeInTheDocument();
    expect(api.getAuthorizeInfo).not.toHaveBeenCalled();
  });

  it("returns to the consent page with its query after signing in", async () => {
    signIn("admin", "unauthenticated");
    api.getAuthorizeInfo.mockResolvedValue({ clientName: "Claude", redirectHost: "claude.ai", canWrite: true, allowed: ["data.read"] });
    const router = createTestRouter([consentUrl]);
    render(<RouterProvider router={router} />);

    expect(await screen.findByRole("button", { name: /sign in/i })).toBeInTheDocument();
    const state = router.state.location.state as { from?: { pathname?: string; search?: string } } | null;
    expect(state?.from?.pathname).toBe("/oauth/authorize");
    expect(state?.from?.search).toContain("client_id=bento_client_x");
  });
});
