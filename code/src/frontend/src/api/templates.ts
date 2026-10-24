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

/** A template Bento can install (Step 5.15): the recipes live on the server, for the app and MCP clients alike. */
export type TemplateSummary = {
  id: string;
  title: string;
  description: string;
  /** The template's tables by key → their name without a prefix. */
  base: Record<string, string>;
  /** Prefixes to try when a name is taken. */
  prefixes: string[];
  dashboards: string[];
  forms: string[];
  exampleRows: number;
};

export type TemplateInstallResult = {
  template: string;
  tables: Record<string, string>;
  dashboards: { id: number; name: string }[];
  forms: { id: number; name: string }[];
  exampleRows: number;
  /** The dashboard to open first. */
  open: number | null;
};

export async function listTemplateCatalog(): Promise<TemplateSummary[]> {
  return (await apiClient.get<DataEnvelope<TemplateSummary[]>>("/templates/catalog")).data;
}

/** Sets the template up on the server, as the user: tables, example rows, dashboards, forms. */
export async function installTemplate(id: string, options: { prefix?: string; examples: boolean }): Promise<TemplateInstallResult> {
  return (await apiClient.post<DataEnvelope<TemplateInstallResult>>(`/templates/${encodeURIComponent(id)}/install`, options)).data;
}
