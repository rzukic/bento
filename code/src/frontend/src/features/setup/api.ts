import { apiClient } from "../../api/client";
import type { SampleDataResponse, SetupStatus } from "../../types/setup";

/**
 * `GET /setup/status` is allow-listed in the backend auth middleware and is
 * meant to be called before any session exists, so it must neither send a
 * token nor trigger the 401-refresh loop.
 */
const STATUS_INIT = { anonymous: true, skipAuthRetry: true } as const;

export function getSetupStatus(): Promise<SetupStatus> {
  return apiClient.get<SetupStatus>("/setup/status", STATUS_INIT);
}

/**
 * Loads the embedded demo workspace. Admin only in standard mode; the shared
 * client attaches the access token obtained in the wizard's admin step.
 */
export function postSampleData(): Promise<SampleDataResponse> {
  return apiClient.post<SampleDataResponse>("/setup/sample-data");
}
