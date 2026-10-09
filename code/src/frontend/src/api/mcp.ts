import { apiClient } from "./client";
import type { ApiKeyScope } from "./connections";

type DataEnvelope<T> = { data: T };

/** GET /api/v1/mcp — where clients connect, and whether sign-in with OAuth is available. */
export type McpInfo = { endpoint: string; oauth: boolean; canWrite: boolean };

/** An app the user connected with their account (an OAuth grant). */
export type McpConnection = {
  id: number;
  clientName: string;
  username: string | null;
  scope: ApiKeyScope;
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

export async function listMcpCalls(limit = 50): Promise<McpCall[]> {
  return (await apiClient.get<DataEnvelope<McpCall[]>>(`/mcp/calls?limit=${limit}`)).data;
}

/** What the consent page shows before the user decides. */
export type AuthorizeInfo = { clientName: string; redirectHost: string; canWrite: boolean };

export async function getAuthorizeInfo(clientId: string, redirectUri: string): Promise<AuthorizeInfo> {
  const params = new URLSearchParams({ client_id: clientId, redirect_uri: redirectUri });
  return (await apiClient.get<DataEnvelope<AuthorizeInfo>>(`/oauth/authorize?${params.toString()}`)).data;
}

export type AuthorizeDecision = {
  clientId: string;
  redirectUri: string;
  codeChallenge: string;
  codeChallengeMethod: string;
  scope: ApiKeyScope;
  state: string | null;
  approve: boolean;
};

/** The address to send the browser to: back to the client with a code, or with access_denied. */
export async function decideAuthorization(decision: AuthorizeDecision): Promise<string> {
  return (await apiClient.post<DataEnvelope<{ redirectTo: string }>>("/oauth/authorize", decision)).data.redirectTo;
}
