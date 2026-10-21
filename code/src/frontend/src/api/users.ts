import { apiClient } from "./client";

type DataEnvelope<T> = { data: T };

// ── Users (Step 5.5 part B): installation admins manage them ──────────────

export type UserWorkspace = { id: number; slug: string; name: string; role: string };

/** `role`: the installation role — admin (every workspace) or the role a member gets by default. */
export type BentoUser = { id: number; username: string; role: string; createdAt: string; workspaces: UserWorkspace[] };

export async function listUsers(): Promise<BentoUser[]> {
  return (await apiClient.get<DataEnvelope<BentoUser[]>>("/admin/users")).data;
}

export async function createUser(user: { username: string; password: string; role: string; workspaces: { id: number; role: string }[] }): Promise<BentoUser> {
  return (await apiClient.post<DataEnvelope<BentoUser>>("/admin/users", user)).data;
}

/** Deletes a user; their dashboards, forms, saved queries and views go to you. */
export async function deleteUser(id: number): Promise<void> {
  await apiClient.delete(`/admin/users/${id}`);
}

/** A new password signs the user out everywhere. */
export async function updateUser(id: number, changes: { role?: string; password?: string }): Promise<BentoUser> {
  return (await apiClient.put<DataEnvelope<BentoUser>>(`/admin/users/${id}`, changes)).data;
}
