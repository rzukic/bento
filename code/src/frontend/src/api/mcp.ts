import { apiClient } from "./client";
import type { ApiKeyScope } from "./connections";

type DataEnvelope<T> = { data: T };

/** What a connected app may do, area by area (Step 4.6); the user ticks them on the consent page. */
export type McpPermission =
  | "data.read"
  | "data.write"
  | "data.delete"
  | "dashboards.write"
  | "forms.write"
  | "schema.write"
  | "functions.write";

export const MCP_PERMISSIONS: { id: McpPermission; label: string; detail: string; needs: "viewer" | "editor" | "admin"; danger?: boolean }[] = [
  { id: "data.read", label: "Read everything", detail: "Tables and rows, the schema, dashboards, forms, saved queries and functions; run analyses.", needs: "viewer" },
  { id: "data.write", label: "Add and change rows", detail: "For example a new transaction; also CSV imports.", needs: "editor" },
  { id: "data.delete", label: "Delete rows", detail: "Remove records from your tables.", needs: "editor", danger: true },
  { id: "dashboards.write", label: "Dashboards and saved queries", detail: "Create, change and delete them.", needs: "viewer" },
  { id: "forms.write", label: "Forms", detail: "Create, change and delete entry forms.", needs: "editor" },
  { id: "schema.write", label: "Tables and columns", detail: "Change the schema — this can delete data.", needs: "admin", danger: true },
  { id: "functions.write", label: "Functions and price sources", detail: "Write JavaScript functions and run price sources.", needs: "admin" },
];

/** OAuth scope text → permissions: the ids, and "write" for adding and changing rows. Read is always in. */
export function permissionsFromScope(scope: string | null): McpPermission[] {
  const wanted = new Set<McpPermission>(["data.read"]);
  for (const part of (scope ?? "").split(/\s+/)) {
    if (part === "write") wanted.add("data.write");
    else if (MCP_PERMISSIONS.some((p) => p.id === part)) wanted.add(part as McpPermission);
  }
  return MCP_PERMISSIONS.map((p) => p.id).filter((id) => wanted.has(id));
}

/** GET /api/v1/mcp — where clients connect, and whether sign-in with OAuth is available. */
export type McpInfo = { endpoint: string; oauth: boolean; canWrite: boolean };

/** An app the user connected with their account (an OAuth grant). */
export type McpConnection = {
  id: number;
  clientName: string;
  username: string | null;
  scope: ApiKeyScope;
  permissions: McpPermission[];
  /** What the user behind the app could grant it (their role). */
  allowed: McpPermission[];
  createdAt: string;
  lastUsedAt: string | null;
};

export type McpCall = {
  id: number;
  calledAt: string;
  userId: number | null;
  username: string | null;
  via: "oauth" | "api_key" | "session" | "open";
  credential: string | null;
  tool: string;
  arguments: string | null;
  ok: boolean;
  error: string | null;
  durationMs: number;
};

export async function getMcpInfo(): Promise<McpInfo> {
  return (await apiClient.get<DataEnvelope<McpInfo>>("/mcp")).data;
}

export async function listMcpConnections(): Promise<McpConnection[]> {
  return (await apiClient.get<DataEnvelope<McpConnection[]>>("/mcp/connections")).data;
}

export async function revokeMcpConnection(id: number): Promise<void> {
  await apiClient.delete<void>(`/mcp/connections/${id}`);
}

/** PUT /api/v1/mcp/connections/{id} — change what a connected app may do; applies to its next call. */
export async function updateMcpConnection(id: number, permissions: McpPermission[]): Promise<McpConnection> {
  return (await apiClient.put<DataEnvelope<McpConnection>>(`/mcp/connections/${id}`, { permissions })).data;
}

/** The calls made through one connected app, newest first. */
export async function listMcpConnectionCalls(id: number, limit = 100): Promise<McpCall[]> {
  return (await apiClient.get<DataEnvelope<McpCall[]>>(`/mcp/connections/${id}/calls?limit=${limit}`)).data;
}

export async function listMcpCalls(limit = 50): Promise<McpCall[]> {
  return (await apiClient.get<DataEnvelope<McpCall[]>>(`/mcp/calls?limit=${limit}`)).data;
}

/** What the consent page shows before the user decides. */
export type AuthorizeInfo = { clientName: string; redirectHost: string; canWrite: boolean; allowed: McpPermission[] };

export async function getAuthorizeInfo(clientId: string, redirectUri: string): Promise<AuthorizeInfo> {
  const params = new URLSearchParams({ client_id: clientId, redirect_uri: redirectUri });
  return (await apiClient.get<DataEnvelope<AuthorizeInfo>>(`/oauth/authorize?${params.toString()}`)).data;
}

export type AuthorizeDecision = {
  clientId: string;
  redirectUri: string;
  codeChallenge: string;
  codeChallengeMethod: string;
  permissions: McpPermission[];
  state: string | null;
  approve: boolean;
};

/** The address to send the browser to: back to the client with a code, or with access_denied. */
export async function decideAuthorization(decision: AuthorizeDecision): Promise<string> {
  return (await apiClient.post<DataEnvelope<{ redirectTo: string }>>("/oauth/authorize", decision)).data.redirectTo;
}
