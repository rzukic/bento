import { useAuthStore } from "../stores/authStore";

const API_BASE = "/api/v1";

export class ApiClientError extends Error {
  readonly status: number;
  readonly code?: string;
  readonly details?: unknown;

  constructor(message: string, status: number, code?: string, details?: unknown) {
    super(message);
    this.name = "ApiClientError";
    this.status = status;
    this.code = code;
    this.details = details;
  }
}

type ApiErrorBody = {
  error?: { code?: string; message?: string; details?: unknown };
};

export type ApiRequestInit = RequestInit & {
  /**
   * When true, a 401 response is returned as-is instead of triggering the
   * shared refresh-retry flow. Used by the auth endpoints themselves so we
   * never recurse: /auth/login and /auth/refresh MUST not try to refresh
   * their own session.
   */
  skipAuthRetry?: boolean;
  /**
   * When true, no `Authorization` header is attached. Used by the public
   * and anonymous routes (GET /api/v1/admin/health, POST /auth/login, …).
   */
  anonymous?: boolean;
  /**
   * Body to JSON-encode. Prefer this over setting `body` directly so the
   * client can set `Content-Type: application/json` consistently.
   */
  json?: unknown;
};

async function parseErrorBody(response: Response): Promise<ApiClientError> {
  let message = `Request failed: ${response.status}`;
  let code: string | undefined;
  let details: unknown;
  try {
    const body = (await response.json()) as ApiErrorBody;
    if (body?.error?.message) message = body.error.message;
    if (body?.error?.code) code = body.error.code;
    if (body?.error?.details !== undefined) details = body.error.details;
  } catch {
    if (response.statusText) message = response.statusText;
  }
  return new ApiClientError(message, response.status, code, details);
}

function buildInit(path: string, init: ApiRequestInit): RequestInit {
  const headers = new Headers(init.headers);
  let body: BodyInit | null | undefined = init.body;

  if (init.json !== undefined) {
    headers.set("Content-Type", "application/json");
    body = JSON.stringify(init.json);
  }

  if (!init.anonymous) {
    const accessToken = useAuthStore.getState().accessToken;
    if (accessToken && !headers.has("Authorization")) {
      headers.set("Authorization", `Bearer ${accessToken}`);
    }
  }

  // Always allow cookies so the HttpOnly refresh cookie goes with /auth/*.
  const merged: RequestInit = {
    credentials: "include",
    ...init,
    headers,
    body
  };

  // Strip our custom fields before handing it to fetch.
  delete (merged as ApiRequestInit).json;
  delete (merged as ApiRequestInit).anonymous;
  delete (merged as ApiRequestInit).skipAuthRetry;

  return merged;
}

async function doFetch(path: string, init: ApiRequestInit): Promise<Response> {
  return fetch(`${API_BASE}${path}`, buildInit(path, init));
}

/**
 * Core request helper. Injects the access token, parses error bodies into
 * `ApiClientError`, and on a 401 response attempts a single silent refresh +
 * retry. If the refresh also fails the store is logged out and the original
 * 401 error is rethrown so callers see a clean "session expired" failure.
 */
export async function apiRequest<T>(path: string, init: ApiRequestInit = {}): Promise<T> {
  let response = await doFetch(path, init);

  if (response.status === 401 && !init.skipAuthRetry) {
    const auth = useAuthStore.getState();
    // Only try to refresh if we ever had a session. For a never-authenticated
    // caller a 401 is simply "please log in".
    if (auth.accessToken || auth.status === "authenticated") {
      try {
        await auth.refresh();
        response = await doFetch(path, { ...init, skipAuthRetry: true });
      } catch {
        // The refresh itself was rejected, so the cookie is already unusable.
        auth.logout({ revokeSession: false });
        throw await parseErrorBody(response);
      }
    }
  }

  if (!response.ok) {
    throw await parseErrorBody(response);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  const contentType = response.headers.get("Content-Type") ?? "";
  if (!contentType.includes("application/json")) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

export const apiClient = {
  get<T>(path: string, init?: ApiRequestInit): Promise<T> {
    return apiRequest<T>(path, { ...init, method: "GET" });
  },
  post<T>(path: string, json?: unknown, init?: ApiRequestInit): Promise<T> {
    return apiRequest<T>(path, { ...init, method: "POST", json });
  },
  put<T>(path: string, json?: unknown, init?: ApiRequestInit): Promise<T> {
    return apiRequest<T>(path, { ...init, method: "PUT", json });
  },
  patch<T>(path: string, json?: unknown, init?: ApiRequestInit): Promise<T> {
    return apiRequest<T>(path, { ...init, method: "PATCH", json });
  },
  delete<T>(path: string, init?: ApiRequestInit): Promise<T> {
    return apiRequest<T>(path, { ...init, method: "DELETE" });
  }
};

/**
 * Legacy shim kept for any call sites that still import `apiFetch`. New code
 * should prefer `apiClient.get/post/...`.
 */
export async function apiFetch<T>(path: string, init?: RequestInit): Promise<T> {
  return apiRequest<T>(path, { ...init });
}
