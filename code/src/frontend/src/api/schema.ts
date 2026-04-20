import { ApiClientError, apiClient } from "./client";
import type {
  HealthResponse,
  SchemaApplyResponse,
  SchemaConfirmationRequiredDetails,
  SchemaModelDocument,
  SchemaPreviewResponse,
  SchemaValidationFailedDetails
} from "../types/api";

/**
 * Fetch the current Model JSON snapshot. Requires an authenticated admin
 * session; callers are responsible for only invoking this from inside the
 * authenticated shell.
 */
export function getSchemaSnapshot(): Promise<SchemaModelDocument> {
  return apiClient.get<SchemaModelDocument>("/admin/schema");
}

/**
 * Preview a draft schema without mutating the live backend state.
 */
export function previewSchemaDraft(draft: SchemaModelDocument): Promise<SchemaPreviewResponse> {
  return apiClient.post<SchemaPreviewResponse>("/admin/schema/preview", draft);
}

/**
 * Apply a draft schema through the existing admin endpoint.
 */
export function applySchemaDraft(
  draft: SchemaModelDocument,
  options?: { confirmDestructive?: boolean }
): Promise<SchemaApplyResponse> {
  const query = options?.confirmDestructive ? "?confirm_destructive=true" : "";
  return apiClient.put<SchemaApplyResponse>(`/admin/schema${query}`, draft);
}

/**
 * Fetch the public (anonymous) health probe. Safe to call from the login
 * page or from the shell before auth is resolved.
 */
export function getPublicHealth(): Promise<HealthResponse> {
  return apiClient.get<HealthResponse>("/admin/health", { anonymous: true });
}

export function isSchemaValidationFailedError(error: unknown): error is ApiClientError {
  return error instanceof ApiClientError && error.code === "VALIDATION_FAILED";
}

export function getSchemaValidationFailedDetails(error: unknown): SchemaValidationFailedDetails | null {
  if (!isSchemaValidationFailedError(error)) {
    return null;
  }

  const details = error.details;
  return details && typeof details === "object"
    ? (details as SchemaValidationFailedDetails)
    : null;
}

export function isSchemaConfirmationRequiredError(error: unknown): error is ApiClientError {
  return error instanceof ApiClientError && error.code === "CONFIRMATION_REQUIRED";
}

export function getSchemaConfirmationRequiredDetails(error: unknown): SchemaConfirmationRequiredDetails | null {
  if (!isSchemaConfirmationRequiredError(error)) {
    return null;
  }

  const details = error.details;
  return details && typeof details === "object"
    ? (details as SchemaConfirmationRequiredDetails)
    : null;
}
