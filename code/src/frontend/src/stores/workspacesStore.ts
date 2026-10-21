import { create } from "zustand";
import { listWorkspaces, type Workspace } from "../api/workspaces";
import { currentWorkspaceSlug } from "../lib/workspace";
import { registerLogoutHook } from "./authStore";

/**
 * The workspaces of the installation (Step 5.5), for the sidebar's switcher
 * and the admins' list in Settings. Loaded once; the list reloads it after a change.
 */
type WorkspacesStoreState = {
  workspaces: Workspace[] | null;
  load: (options?: { force?: boolean }) => Promise<Workspace[]>;
  reset: () => void;
};

export const useWorkspacesStore = create<WorkspacesStoreState>((set, get) => ({
  workspaces: null,
  async load(options) {
    const known = get().workspaces;
    if (known && !options?.force) return known;
    const listed = await listWorkspaces();
    // An older server (or an unexpected answer) has none to switch between.
    const workspaces = Array.isArray(listed) ? listed : [];
    set({ workspaces });
    return workspaces;
  },
  reset() {
    set({ workspaces: null });
  },
}));

registerLogoutHook(() => useWorkspacesStore.getState().reset());

/** The workspace this page works in, once the list is known. */
export function currentWorkspace(workspaces: Workspace[] | null): Workspace | undefined {
  const slug = currentWorkspaceSlug();
  return workspaces?.find((w) => (slug ? w.slug === slug : w.isDefault));
}
