import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { apiClient } from "../api/client";
import type { Workspace } from "../api/workspaces";
import { WorkspaceSwitcher } from "../features/workspaces/WorkspaceSwitcher";
import { WorkspacesPanel } from "../features/workspaces/WorkspacesPanel";
import { setCurrentWorkspaceForTests, workspaceApiPath, workspaceHref } from "../lib/workspace";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import { useWorkspacesStore } from "../stores/workspacesStore";

const api = vi.hoisted(() => ({
  listWorkspaces: vi.fn(),
  createWorkspace: vi.fn(),
  renameWorkspace: vi.fn(),
  deleteWorkspace: vi.fn(),
}));
vi.mock("../api/workspaces", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/workspaces")>()), ...api }));

const all: Workspace[] = [
  { id: 1, slug: "default", name: "Default", isDefault: true },
  { id: 2, slug: "finances", name: "Finances", isDefault: false },
];

beforeEach(() => {
  resetAuthStoreForTest();
  useWorkspacesStore.getState().reset();
  useAuthStore.setState({ status: "authenticated", user: { id: 1, username: "admin", role: "admin" }, accessToken: "token", expiresAt: Date.now() + 60_000, loginInFlight: false, error: null });
  api.listWorkspaces.mockResolvedValue(all);
});

afterEach(() => {
  cleanup();
  setCurrentWorkspaceForTests(null);
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe("workspaces", () => {
  it("addresses the current workspace: links, API paths and the request header", async () => {
    expect(workspaceHref(all[0])).toBe("/");
    expect(workspaceHref(all[1])).toBe("/w/finances/");
    expect(workspaceApiPath("/mcp/bento.mcpb")).toBe("/api/v1/mcp/bento.mcpb");

    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(new Response("{}", { headers: { "Content-Type": "application/json" } })));
    vi.stubGlobal("fetch", fetchMock);
    await apiClient.get("/dashboards");
    expect((fetchMock.mock.calls[0][1].headers as Headers).has("X-Bento-Workspace")).toBe(false);

    setCurrentWorkspaceForTests("finances");
    expect(workspaceApiPath("/mcp/bento.mcpb")).toBe("/api/v1/w/finances/mcp/bento.mcpb");
    await apiClient.get("/dashboards");
    expect((fetchMock.mock.calls[1][1].headers as Headers).get("X-Bento-Workspace")).toBe("finances");
    expect(fetchMock.mock.calls[1][0]).toBe("/api/v1/dashboards");
  });

  it("names the current workspace in the sidebar and lists the others", async () => {
    setCurrentWorkspaceForTests("finances");
    render(
      <MemoryRouter>
        <WorkspaceSwitcher />
      </MemoryRouter>,
    );
    const button = await screen.findByRole("button", { name: /Workspace: Finances/ });
    await userEvent.click(button);
    expect(screen.getByRole("menuitemradio", { name: "Default" })).toHaveAttribute("href", "/");
    expect(screen.getByRole("menuitemradio", { name: /Finances/ })).toHaveAttribute("aria-checked", "true");
    expect(screen.getByRole("menuitem", { name: "Manage workspaces" })).toBeInTheDocument();
  });

  it("shows the plain heading while there is only one workspace", async () => {
    api.listWorkspaces.mockResolvedValue([all[0]]);
    render(
      <MemoryRouter>
        <WorkspaceSwitcher />
      </MemoryRouter>,
    );
    await waitFor(() => expect(api.listWorkspaces).toHaveBeenCalled());
    expect(screen.getByText("Workspace")).toBeInTheDocument();
    expect(screen.queryByRole("button")).toBeNull();
  });

  it("lets admins create workspaces and delete one only with its short name typed", async () => {
    api.createWorkspace.mockResolvedValue({ id: 3, slug: "calendar", name: "Calendar", isDefault: false });
    render(
      <MemoryRouter>
        <WorkspacesPanel />
      </MemoryRouter>,
    );
    expect(await screen.findByText("/w/finances · /api/v1/w/finances · /mcp/finances")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Delete Default" })).toBeNull();

    await userEvent.click(screen.getByRole("button", { name: "New workspace" }));
    await userEvent.type(screen.getByLabelText(/^Name/), "Calendar");
    await userEvent.click(screen.getByRole("button", { name: "Create" }));
    await waitFor(() => expect(api.createWorkspace).toHaveBeenCalledWith("Calendar", undefined));

    await userEvent.click(screen.getByRole("button", { name: "Delete Finances" }));
    const confirm = screen.getByRole("button", { name: "Delete workspace" });
    expect(confirm).toBeDisabled();
    await userEvent.type(screen.getByLabelText(/Type “finances” to confirm/), "finances");
    expect(confirm).toBeEnabled();
    await userEvent.click(confirm);
    await waitFor(() => expect(api.deleteWorkspace).toHaveBeenCalledWith(2, "finances"));
  });
});
