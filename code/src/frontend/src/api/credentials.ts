import { apiClient } from "./client";

/** A stored credential (Step 4.2): its value never comes back from the server. */
export type Credential = {
  name: string;
  description: string | null;
  createdAt: string;
  updatedAt: string | null;
  lastUsedAt: string | null;
  /** Functions that read it as credentials.name. */
  usedBy: string[];
  /** False when the server's secret changed and the value must be entered again. */
  readable: boolean;
};

type DataEnvelope<T> = { data: T };

export async function listCredentials(): Promise<Credential[]> {
  return (await apiClient.get<DataEnvelope<Credential[]>>("/admin/credentials")).data;
}

export async function createCredential(name: string, value: string, description?: string): Promise<Credential> {
  return (await apiClient.post<DataEnvelope<Credential>>("/admin/credentials", { name, value, description })).data;
}

export async function updateCredential(name: string, changes: { value?: string; description?: string }): Promise<Credential> {
  return (await apiClient.put<DataEnvelope<Credential>>(`/admin/credentials/${encodeURIComponent(name)}`, changes)).data;
}

export async function deleteCredential(name: string): Promise<void> {
  await apiClient.delete(`/admin/credentials/${encodeURIComponent(name)}`);
}
