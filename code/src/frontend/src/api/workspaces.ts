import { apiClient } from "./client";

type DataEnvelope<T> = { data: T };

// ── Workspaces (Step 5.5): separate areas of one installation ─────────────

/** `role`: the signed-in user's role in it. */
export type Workspace = { id: number; slug: string; name: string; isDefault: boolean; role?: string };

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

// ── Members (Step 5.5 part B) ─────────────────────────────────────────

export type WorkspaceRole = "admin" | "editor" | "viewer" | "api";

/** Installation admins are admins in every workspace; their row cannot be changed. */
export type WorkspaceMember = { userId: number; username: string; role: WorkspaceRole; installationAdmin: boolean };

export type MemberCandidate = { userId: number; username: string; role: string };

export async function listMembers(workspaceId: number): Promise<{ members: WorkspaceMember[]; candidates: MemberCandidate[] }> {
  const response = await apiClient.get<{ data: WorkspaceMember[]; candidates: MemberCandidate[] }>(`${path}/${workspaceId}/members`);
  return { members: response.data, candidates: response.candidates ?? [] };
}

export async function setMember(workspaceId: number, userId: number, role: WorkspaceRole): Promise<void> {
  await apiClient.put(`${path}/${workspaceId}/members/${userId}`, { role });
}

export async function removeMember(workspaceId: number, userId: number): Promise<void> {
  await apiClient.delete(`${path}/${workspaceId}/members/${userId}`);
}

/** Deletes the workspace with everything in it; `confirm` is its short name, typed again. */
export async function deleteWorkspace(id: number, confirm: string): Promise<void> {
  await apiClient.delete(`${path}/${id}?confirm=${encodeURIComponent(confirm)}`);
}
