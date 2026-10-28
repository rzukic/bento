import type { WorkspaceRole } from "../../api/workspaces";

/** What each role may do in a workspace, for the role pickers. */
export const ROLE_OPTIONS: { value: WorkspaceRole; label: string }[] = [
  { value: "admin", label: "Admin — schema, settings, apps, members" },
  { value: "editor", label: "Editor — add and change rows" },
  { value: "viewer", label: "Viewer — look only" },
];

export const ROLE_NAMES: Record<string, string> = { admin: "Admin", editor: "Editor", viewer: "Viewer", api: "API" };
