import { apiClient } from "./client";
import type { LoginRequest, LoginResponse } from "../types/auth";

/**
 * Auth endpoints must opt out of the shared client's 401-refresh-retry loop —
 * they *are* that loop. They also explicitly opt out of the Authorization
 * header; login and refresh are anonymous calls that rely on the HttpOnly
 * refresh cookie for continuity.
 */
const AUTH_INIT = { anonymous: true, skipAuthRetry: true } as const;

export function postLogin(req: LoginRequest): Promise<LoginResponse> {
  return apiClient.post<LoginResponse>("/auth/login", req, AUTH_INIT);
}

export function postRefresh(): Promise<LoginResponse> {
  return apiClient.post<LoginResponse>("/auth/refresh", undefined, AUTH_INIT);
}
