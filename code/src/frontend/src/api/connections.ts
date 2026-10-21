import { apiClient } from "./client";
import { useAuthStore } from "../stores/authStore";

export type ApiKeyScope = "read" | "read-write";

/** A key made with chosen permissions shows "permissions" as its scope. */
export type ApiKeyAccess = ApiKeyScope | "permissions";

export type ApiKey = {
  id: number;
  name: string;
  prefix: string | null;
  scope: ApiKeyAccess;
  /** Keys made with chosen permissions (like a connected app): data.read, schema.write, … */
  permissions?: string[] | null;
  createdBy: string | null;
  createdAt: string;
  expiresAt: string | null;
  lastUsedAt: string | null;
  revokedAt: string | null;
  status: "active" | "expired" | "revoked";
  /** The workspaces it may use (Step 5.5); null: every workspace. */
  workspaces?: number[] | null;
};

/** Where a key may be used: the given workspaces, or all of them. */
export type KeyWorkspaces = { workspaces: number[] } | { allWorkspaces: true };

type DataEnvelope<T> = { data: T };

export async function listApiKeys(): Promise<ApiKey[]> {
  return (await apiClient.get<DataEnvelope<ApiKey[]>>("/admin/api-keys")).data;
}

/** The plain key comes back once, here. */
export async function createApiKey(
  name: string,
  scope: ApiKeyScope,
  expiresInDays?: number,
  permissions?: string[],
  where?: KeyWorkspaces,
): Promise<{ key: string; apiKey: ApiKey }> {
  const access = permissions ? { permissions } : { scope };
  const response = await apiClient.post<{ data: ApiKey; key: string }>("/admin/api-keys", { name, expiresInDays, ...access, ...where });
  return { key: response.key, apiKey: response.data };
}

export async function revokeApiKey(id: number): Promise<void> {
  await apiClient.delete<void>(`/admin/api-keys/${id}`);
}

/** GET /api/v1/openapi.json — this installation's API, generated from its schema. */
export function getOpenApiDocument(): Promise<OpenApiDocument> {
  return apiClient.get<OpenApiDocument>("/openapi.json");
}

export type OpenApiSchema = {
  $ref?: string;
  type?: string;
  format?: string;
  enum?: unknown[];
  example?: unknown;
  description?: string;
  nullable?: boolean;
  readOnly?: boolean;
  properties?: Record<string, OpenApiSchema>;
  required?: string[];
  items?: OpenApiSchema;
  additionalProperties?: boolean | OpenApiSchema;
};

export type OpenApiParameter = {
  name: string;
  in: "query" | "path" | "header";
  required?: boolean;
  description?: string;
  style?: string;
  schema?: OpenApiSchema;
};

export type OpenApiOperation = {
  tags?: string[];
  summary?: string;
  description?: string;
  parameters?: OpenApiParameter[];
  requestBody?: { content?: Record<string, { schema?: OpenApiSchema }> };
  responses?: Record<string, { description?: string }>;
  security?: unknown[];
  "x-bento-scope"?: "read" | "write" | "none";
};

export type OpenApiDocument = {
  openapi: string;
  info: { title: string; version: string; description?: string };
  servers?: { url: string }[];
  tags?: { name: string; description?: string }[];
  paths: Record<string, Record<string, OpenApiOperation>>;
  components?: { schemas?: Record<string, OpenApiSchema> };
};

export type TryItResponse = { status: number; statusText: string; durationMs: number; body: string; contentType: string };

/**
 * Sends a request exactly as described, without the app's error handling,
 * so the explorer can show what the API answered. With `apiKey` the session
 * token stays out of it.
 */
export async function sendRaw(method: string, url: string, body: string | null, apiKey: string | null): Promise<TryItResponse> {
  const headers = new Headers({ Accept: "application/json" });
  if (body !== null) headers.set("Content-Type", "application/json");
  if (apiKey) headers.set("X-API-Key", apiKey);
  else {
    const token = useAuthStore.getState().accessToken;
    if (token) headers.set("Authorization", `Bearer ${token}`);
  }
  const started = performance.now();
  const response = await fetch(url, { method, headers, body: body ?? undefined, credentials: apiKey ? "omit" : "include" });
  const text = await response.text();
  return {
    status: response.status,
    statusText: response.statusText,
    durationMs: Math.round(performance.now() - started),
    body: text,
    contentType: response.headers.get("Content-Type") ?? "",
  };
}
