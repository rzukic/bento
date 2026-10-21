import { apiClient } from "./client";

type DataEnvelope<T> = { data: T };

/** A template installed in this workspace, with its tables: the template's name → the name here. */
export type InstalledTemplate = {
  id: string;
  title: string;
  installedAt: string;
  tables: Record<string, string>;
};

export async function listInstalledTemplates(): Promise<InstalledTemplate[]> {
  return (await apiClient.get<DataEnvelope<InstalledTemplate[]>>("/templates")).data;
}

/** Notes the install, so the AI and MCP clients learn the template's tables and conventions (and only this workspace's). */
export async function recordTemplate(id: string, title: string, tables: Record<string, string>): Promise<InstalledTemplate> {
  return (await apiClient.put<DataEnvelope<InstalledTemplate>>(`/templates/${encodeURIComponent(id)}`, { title, tables })).data;
}
