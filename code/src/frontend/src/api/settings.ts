import { apiClient } from "./client";

export type WorkspaceSettings = {
  locale: string | null;
  currency: string;
  /** An IANA time zone; null: the server's. */
  timeZone?: string | null;
  weekStart?: "monday" | "sunday" | "saturday";
  /** The month the (fiscal) year starts in, 1–12. */
  yearStart?: number;
};

/** PUT /api/v1/admin/settings — how the workspace writes numbers and dates, its currency and its calendar. */
export async function saveWorkspaceSettings(settings: WorkspaceSettings): Promise<WorkspaceSettings> {
  return (await apiClient.put<{ data: WorkspaceSettings }>("/admin/settings", settings)).data;
}
