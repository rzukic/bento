import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Workspace } from "../api/workspaces";
import { WorkspaceChecklist } from "../features/connections/WorkspaceChecklist";
import { MembersPanel } from "../features/workspaces/MembersPanel";
import { UsersPanel } from "../features/workspaces/UsersPanel";
import { WorkspaceGate } from "../features/workspaces/WorkspaceGate";
import { setCurrentWorkspaceForTests } from "../lib/workspace";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import { useWorkspacesStore } from "../stores/workspacesStore";
import { isInstallationAdmin } from "../types/auth";

const workspacesApi = vi.hoisted(() => ({ listWorkspaces: vi.fn(), listMembers: vi.fn(), setMember: vi.fn(), removeMember: vi.fn() }));
vi.mock("../api/workspaces", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/workspaces")>()), ...workspacesApi }));
const usersApi = vi.hoisted(() => ({ listUsers: vi.fn(), createUser: vi.fn(), updateUser: vi.fn() }));
vi.mock("../api/users", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/users")>()), ...usersApi }));

const all: Workspace[] = [
  { id: 1, slug: "default", name: "Default", isDefault: true, role: "admin" },
  { id: 2, slug: "gesundheit", name: "Gesundheit", isDefault: false, role: "admin" },
];

function signIn(user: { role: string; installation_role?: string; workspace_role?: string | null }) {
  useAuthStore.setState({
    status: "authenticated",
    user: { id: 1, username: "ruben", ...user },
    accessToken: "token",
    expiresAt: Date.now() + 60_000,
    loginInFlight: false,
    error: null,
  });
}

beforeEach(() => {
  resetAuthStoreForTest();
  useWorkspacesStore.getState().reset();
  workspacesApi.listWorkspaces.mockResolvedValue(all);
});

afterEach(() => {
  cleanup();
  setCurrentWorkspaceForTests(null);
  vi.clearAllMocks();
});

describe("members and users", () => {
  it("tells an installation admin from a workspace's admin", () => {
    expect(isInstallationAdmin({ id: 1, username: "a", role: "admin", installation_role: "admin" })).toBe(true);
    expect(isInstallationAdmin({ id: 2, username: "b", role: "admin", installation_role: "editor" })).toBe(false);
    // Older servers send no installation role: the role is the installation's.
    expect(isInstallationAdmin({ id: 3, username: "c", role: "admin" })).toBe(true);
  });

  it("lists the open workspace's members, changes a role and adds a person", async () => {
    signIn({ role: "admin", installation_role: "editor", workspace_role: "admin" });
    setCurrentWorkspaceForTests("gesundheit");
    workspacesApi.listMembers.mockResolvedValue({
      members: [
        { userId: 1, username: "ruben", role: "admin", installationAdmin: true },
        { userId: 5, username: "anna", role: "viewer", installationAdmin: false },
      ],
      candidates: [{ userId: 7, username: "ben", role: "viewer" }],
    });
    render(
      <MemoryRouter>
        <MembersPanel />
      </MemoryRouter>,
    );
    expect(await screen.findByText("Members of Gesundheit")).toBeInTheDocument();
    expect(await screen.findByText("Installation admin")).toBeInTheDocument();
    await userEvent.selectOptions(screen.getByLabelText("Role of anna"), "editor");
    await waitFor(() => expect(workspacesApi.setMember).toHaveBeenCalledWith(2, 5, "editor"));
    await userEvent.selectOptions(screen.getByLabelText("Add a person"), "7");
    await userEvent.click(screen.getByRole("button", { name: "Add" }));
    await waitFor(() => expect(workspacesApi.setMember).toHaveBeenCalledWith(2, 7, "editor"));
  });

  it("creates a member for chosen workspaces with a role in each", async () => {
    signIn({ role: "admin", installation_role: "admin", workspace_role: "admin" });
    usersApi.listUsers.mockResolvedValue([{ id: 1, username: "ruben", role: "admin", createdAt: "", workspaces: [] }]);
    usersApi.createUser.mockResolvedValue({ id: 5, username: "anna", role: "editor", createdAt: "", workspaces: [] });
    render(
      <MemoryRouter>
        <UsersPanel />
      </MemoryRouter>,
    );
    expect(await screen.findByText("Every workspace")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Add user" }));
    await userEvent.type(screen.getByLabelText(/^User name/), "anna");
    await userEvent.type(screen.getByLabelText(/^Password/), "anna-password");
    await userEvent.click(await screen.findByRole("checkbox", { name: "Gesundheit" }));
    await userEvent.selectOptions(screen.getByLabelText("Role in Gesundheit"), "viewer");
    await userEvent.click(screen.getByRole("button", { name: "Create" }));
    await waitFor(() =>
      expect(usersApi.createUser).toHaveBeenCalledWith({ username: "anna", password: "anna-password", role: "editor", workspaces: [{ id: 2, role: "viewer" }] }),
    );
  });

  it("sends someone outside the workspace to the first one they are in", async () => {
    signIn({ role: "viewer", installation_role: "viewer", workspace_role: null });
    workspacesApi.listWorkspaces.mockResolvedValue([all[1]]);
    const assign = vi.fn();
    const original = window.location;
    Object.defineProperty(window, "location", { configurable: true, value: { ...original, assign } });
    render(
      <MemoryRouter>
        <WorkspaceGate>
          <p>Secret dashboards</p>
        </WorkspaceGate>
      </MemoryRouter>,
    );
    await waitFor(() => expect(assign).toHaveBeenCalledWith("/w/gesundheit/"));
    expect(screen.queryByText("Secret dashboards")).toBeNull();
    Object.defineProperty(window, "location", { configurable: true, value: original });
  });

  it("tells someone in no workspace whom to ask", async () => {
    signIn({ role: "viewer", installation_role: "viewer", workspace_role: null });
    workspacesApi.listWorkspaces.mockResolvedValue([]);
    render(
      <MemoryRouter>
        <WorkspaceGate>
          <p>Secret dashboards</p>
        </WorkspaceGate>
      </MemoryRouter>,
    );
    expect(await screen.findByText("No workspace yet")).toBeInTheDocument();
  });

  it("lets a connection or key keep at least one workspace", async () => {
    const onChange = vi.fn();
    const { container } = render(<WorkspaceChecklist workspaces={all} chosen={[2]} onChange={onChange} />);
    const boxes = within(container).getAllByRole("checkbox");
    expect(boxes[1]).toBeDisabled();
    await userEvent.click(boxes[0]);
    expect(onChange).toHaveBeenCalledWith([2, 1]);
    cleanup();
    const { container: single } = render(<WorkspaceChecklist workspaces={[all[0]]} chosen={[1]} onChange={onChange} />);
    expect(single.querySelector("fieldset")).toBeNull();
  });
});
