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
  getAuthorizeInfo: vi.fn(),
  decideAuthorization: vi.fn(),
}));
vi.mock("../api/mcp", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/mcp")>()), ...api }));

const connection: McpConnection = {
  id: 4,
  clientName: "Claude",
  username: "admin",
  scope: "read-write",
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

describe("Connections → Claude", () => {
  it("shows the address, the setup commands, connected apps and calls", async () => {
    render(<RouterProvider router={createTestRouter(["/connections?tab=claude"])} />);

    expect(await screen.findByText("https://bento.example.com/mcp")).toBeInTheDocument();
    expect(screen.getByText("claude mcp add --transport http bento https://bento.example.com/mcp")).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: "Claude" })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByRole("link", { name: /download bento\.mcpb/i })).toHaveAttribute("href", "/api/v1/mcp/bento.mcpb");

    expect(await screen.findByText("Claude", { selector: "strong" })).toBeInTheDocument();
    expect(screen.getByText("Read and write")).toBeInTheDocument();

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
    api.getAuthorizeInfo.mockResolvedValue({ clientName: "Claude", redirectHost: "claude.ai", canWrite: true });
    api.decideAuthorization.mockResolvedValue("https://claude.ai/api/mcp/auth_callback?code=c0de&state=st4te");
    const assign = vi.fn();
    vi.stubGlobal("location", { ...window.location, assign });
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter([consentUrl])} />);

    expect(await screen.findByText("claude.ai")).toBeInTheDocument();
    expect(api.getAuthorizeInfo).toHaveBeenCalledWith("bento_client_x", "https://claude.ai/api/mcp/auth_callback");
    // The client asked for write and the user may write: preselected, but changeable.
    expect(screen.getByRole("radio", { name: /read and write/i })).toBeChecked();
    await user.click(screen.getByRole("radio", { name: /read only/i }));
    await user.click(screen.getByRole("button", { name: /allow/i }));

    await waitFor(() =>
      expect(api.decideAuthorization).toHaveBeenCalledWith({
        clientId: "bento_client_x",
        redirectUri: "https://claude.ai/api/mcp/auth_callback",
        codeChallenge: "abcdefghijklmnopqrstuvwxyzabcdefghijklmnopq",
        codeChallengeMethod: "S256",
        scope: "read",
        state: "st4te",
        approve: true,
      }),
    );
    expect(assign).toHaveBeenCalledWith("https://claude.ai/api/mcp/auth_callback?code=c0de&state=st4te");
  });

  it("offers only read access to a viewer", async () => {
    signIn("viewer");
    api.getAuthorizeInfo.mockResolvedValue({ clientName: "Claude", redirectHost: "claude.ai", canWrite: false });
    render(<RouterProvider router={createTestRouter([consentUrl])} />);

    expect(await screen.findByText("Your role can only read.")).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: /read and write/i })).toBeDisabled();
    expect(screen.getByRole("radio", { name: /read only/i })).toBeChecked();
  });

  it("refuses an incomplete link without asking the server", async () => {
    render(<RouterProvider router={createTestRouter(["/oauth/authorize?client_id=x"])} />);
    expect(await screen.findByText(/link is incomplete/i)).toBeInTheDocument();
    expect(api.getAuthorizeInfo).not.toHaveBeenCalled();
  });

  it("returns to the consent page with its query after signing in", async () => {
    signIn("admin", "unauthenticated");
    api.getAuthorizeInfo.mockResolvedValue({ clientName: "Claude", redirectHost: "claude.ai", canWrite: true });
    const router = createTestRouter([consentUrl]);
    render(<RouterProvider router={router} />);

    expect(await screen.findByRole("button", { name: /sign in/i })).toBeInTheDocument();
    const state = router.state.location.state as { from?: { pathname?: string; search?: string } } | null;
    expect(state?.from?.pathname).toBe("/oauth/authorize");
    expect(state?.from?.search).toContain("client_id=bento_client_x");
  });
});
