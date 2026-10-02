import { apiClient } from "./client";
import type { LoginRequest, LoginResponse, RegisterRequest, RegisterResponse } from "../types/auth";

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

/**
 * Anonymous only while the backend reports `registration_bootstrap_open`:
 * the very first account becomes the admin. Later registrations need an
 * admin token and live in the (future) user management UI, not here.
 */
export function postRegister(req: RegisterRequest): Promise<RegisterResponse> {
  return apiClient.post<RegisterResponse>("/auth/register", req, AUTH_INIT);
}
