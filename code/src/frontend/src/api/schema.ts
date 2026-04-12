import { apiClient } from "./client";
import type { HealthResponse, ModelSnapshot } from "../types/api";

/**
 * Fetch the current Model JSON snapshot. Requires an authenticated admin
 * session; callers are responsible for only invoking this from inside the
 * authenticated shell.
 */
export function getSchemaSnapshot(): Promise<ModelSnapshot> {
  return apiClient.get<ModelSnapshot>("/admin/schema");
}

/**
 * Fetch the public (anonymous) health probe. Safe to call from the login
 * page or from the shell before auth is resolved.
 */
export function getPublicHealth(): Promise<HealthResponse> {
  return apiClient.get<HealthResponse>("/admin/health", { anonymous: true });
}
