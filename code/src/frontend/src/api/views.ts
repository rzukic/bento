import { apiClient } from "./client";
import type { SavedView, CreateViewPayload, UpdateViewPayload } from "../types/views";

export type ExplorerTablePreferences = {
  default_view_id: number | null;
  auto_save: boolean;
};

export type ListViewsResponse = {
  data: SavedView[];
  preferences?: ExplorerTablePreferences;
};

/** GET /api/v1/views/{tableId} — own views + shared views from others */
export function listViews(tableId: string): Promise<ListViewsResponse> {
  return apiClient
    .get<ListViewsResponse>(`/views/${encodeURIComponent(tableId)}`);
}

/** POST /api/v1/views — create a new view owned by the current user */
export function createView(payload: CreateViewPayload): Promise<SavedView> {
  return apiClient.post<SavedView>("/views", payload);
}

/** PUT /api/v1/views/{id} — update name, config, or is_shared (owner only) */
export function updateView(id: number, payload: UpdateViewPayload): Promise<SavedView> {
  return apiClient.put<SavedView>(`/views/${id}`, payload);
}

/** DELETE /api/v1/views/{id} — delete a view (owner only) */
export function deleteView(id: number): Promise<void> {
  return apiClient.delete<void>(`/views/${id}`);
}
