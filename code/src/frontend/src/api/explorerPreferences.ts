import { apiClient } from "./client";
import type { ExplorerTablePreferences } from "./views";

export type UpdateExplorerTablePreferencesPayload = Partial<ExplorerTablePreferences>;

/** PUT /api/v1/explorer/preferences/{tableId} — update per-table explorer preferences */
export function updateExplorerTablePreferences(
  tableId: string,
  payload: UpdateExplorerTablePreferencesPayload,
): Promise<ExplorerTablePreferences> {
  return apiClient.put<ExplorerTablePreferences>(
    `/explorer/preferences/${encodeURIComponent(tableId)}`,
    payload,
  );
}

