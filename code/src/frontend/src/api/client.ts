import { currentWorkspaceSlug } from "../lib/workspace";
import { useAuthStore } from "../stores/authStore";

const API_BASE = "/api/v1";

/**
 * Goes up after every request that may change data (anything but reads,
 * queries, formula checks and auth), so cached query results know they may
 * be out of date (see queryCache.ts).
 */
let mutations = 0;

/**
 * A share link's token (Step 5.6): set on /s/<token>, every request then goes
 * as that link's visitor — no session, no refresh.
 */
let shareToken: string | null = null;

let shareOpened = 0;
let formTrap = "";

export function setShareToken(token: string | null): void {
  shareToken = token;
  shareOpened = Date.now();
  formTrap = "";
}

/** A public form's trap field (Step 5.6): people leave it empty, robots fill it. */
export function setFormTrap(value: string): void {
  formTrap = value;
}
const readOnlyPosts = /^\/(query(\/|$)|queries\/\d+\/run$|formulas\/|auth\/)/;

export function dataVersion(): number {
  return mutations;
}

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

/** Shown when the server answers without a readable error body. */
function fallbackMessage(status: number): string {
  if (status >= 500) return "The server is not responding right now. Try again in a moment.";
  if (status === 404) return "This could not be found.";
  if (status === 403) return "You do not have permission to do this.";
  return "Something went wrong. Try again.";
}

async function parseErrorBody(response: Response): Promise<ApiClientError> {
  let message = fallbackMessage(response.status);
  let code: string | undefined;
  let details: unknown;
  try {
    const body = (await response.json()) as ApiErrorBody;
    if (body?.error?.message) message = body.error.message;
    if (body?.error?.code) code = body.error.code;
    if (body?.error?.details !== undefined) details = body.error.details;
  } catch {
    // Not JSON (a proxy error page, an empty body): keep the fallback.
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

  // The page the request comes from (e.g. /dashboards/6?interval.net=week), for the
  // server's problem log when a query is slow or fails.
  if (typeof window !== "undefined" && !headers.has("X-Bento-Source")) {
    headers.set("X-Bento-Source", encodeURIComponent(window.location.pathname + window.location.search));
  }

  // The workspace the page works in (/w/<slug>); without the header the server uses the default one.
  const workspace = currentWorkspaceSlug();
  if (workspace && !headers.has("X-Bento-Workspace")) {
    headers.set("X-Bento-Workspace", workspace);
  }

  if (shareToken) {
    headers.set("X-Bento-Share", shareToken);
    // What tells a person filling in a public form from a robot.
    if ((init.method ?? "GET").toUpperCase() === "POST") {
      headers.set("X-Bento-Form-Check", `ok:${formTrap}`);
      headers.set("X-Bento-Form-Elapsed", ((Date.now() - shareOpened) / 1000).toFixed(1));
    }
  } else if (!init.anonymous) {
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

  if (response.status === 401 && !init.skipAuthRetry && !shareToken) {
    const auth = useAuthStore.getState();
    // Only try to refresh if we ever had a session. For a never-authenticated
    // caller a 401 is simply "please log in".
    if (auth.accessToken || auth.status === "authenticated") {
      try {
        await auth.refresh();
        response = await doFetch(path, { ...init, skipAuthRetry: true });
      } catch (refreshError) {
        // A rate-limited refresh says nothing about the session; keep it and
        // surface the 429 so the caller can retry later.
        if (refreshError instanceof ApiClientError && refreshError.status === 429) {
          throw refreshError;
        }
        // The refresh itself was rejected, so the cookie is already unusable.
        auth.logout({ revokeSession: false });
        throw await parseErrorBody(response);
      }
    }
  }

  if (!response.ok) {
    throw await parseErrorBody(response);
  }

  const method = (init.method ?? "GET").toUpperCase();
  if (method !== "GET" && method !== "HEAD" && !readOnlyPosts.test(path.split("?")[0])) mutations++;

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
