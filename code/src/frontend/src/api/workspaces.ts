import { apiClient } from "./client";

type DataEnvelope<T> = { data: T };

// ── Workspaces (Step 5.5): separate areas of one installation ─────────────

export type Workspace = { id: number; slug: string; name: string; isDefault: boolean };

const path = "/workspaces";

export async function listWorkspaces(): Promise<Workspace[]> {
  return (await apiClient.get<DataEnvelope<Workspace[]>>(path)).data;
}

export async function createWorkspace(name: string, slug?: string): Promise<Workspace> {
  return (await apiClient.post<DataEnvelope<Workspace>>(path, { name, slug: slug || undefined })).data;
}

export async function renameWorkspace(id: number, name: string): Promise<Workspace> {
  return (await apiClient.put<DataEnvelope<Workspace>>(`${path}/${id}`, { name })).data;
}

/** Deletes the workspace with everything in it; `confirm` is its short name, typed again. */
export async function deleteWorkspace(id: number, confirm: string): Promise<void> {
  await apiClient.delete(`${path}/${id}?confirm=${encodeURIComponent(confirm)}`);
}
