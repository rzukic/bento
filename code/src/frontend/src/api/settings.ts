import { apiClient } from "./client";

export type WorkspaceSettings = { locale: string | null; currency: string };

/** PUT /api/v1/admin/settings — how the workspace writes numbers and dates, and its currency. */
export async function saveWorkspaceSettings(settings: WorkspaceSettings): Promise<WorkspaceSettings> {
  return (await apiClient.put<{ data: WorkspaceSettings }>("/admin/settings", settings)).data;
}
