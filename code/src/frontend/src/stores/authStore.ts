import { create } from "zustand";
import { postLogin, postLogout, postRefresh } from "../api/auth";
import { ApiClientError } from "../api/client";
import type { AuthUser, LoginResponse } from "../types/auth";

/**
 * Other session-scoped stores can register a reset hook here so that logging
 * out clears all per-user caches (schema snapshot, shell state, etc.) in a
 * single call instead of every feature remembering to listen on its own.
 */
const onLogoutHooks = new Set<() => void>();

export function registerLogoutHook(hook: () => void): () => void {
  onLogoutHooks.add(hook);
  return () => onLogoutHooks.delete(hook);
}

/**
 * Session lifecycle:
 *
 *   idle          — initial, before any bootstrap attempt
 *   bootstrapping — first refresh call in flight (attempting silent resume)
 *   authenticated — access token + user present and usable
 *   unauthenticated — no valid session; login required
 */
export type AuthStatus = "idle" | "bootstrapping" | "authenticated" | "unauthenticated";

type AuthStoreState = {
  status: AuthStatus;
  user: AuthUser | null;
  /** In-memory only. Never persisted to storage (XSS hygiene). */
  accessToken: string | null;
  expiresAt: number | null;
  loginInFlight: boolean;
  error: string | null;
  /**
   * Shared in-flight refresh promise so a burst of concurrent 401s only
   * triggers a single POST /auth/refresh. Not meant to be read by UI.
   */
  refreshInFlight: Promise<AuthUser> | null;

  bootstrap: () => Promise<void>;
  login: (username: string, password: string) => Promise<AuthUser>;
  refresh: () => Promise<AuthUser>;
  /**
   * Drop the in-memory session and, unless `revokeSession` is false, ask the
   * backend to revoke the refresh cookie so a page reload cannot resume it.
   */
  logout: (options?: { revokeSession?: boolean }) => void;
  clearError: () => void;
};

/** Longest pause before retrying a rate-limited refresh during bootstrap. */
const MAX_BOOTSTRAP_RETRY_DELAY_MS = 5_000;
const BOOTSTRAP_ATTEMPTS = 3;

/**
 * A 429 says nothing about whether the session is valid, only that the
 * backend wants us to slow down. Returns the delay to wait before retrying,
 * or null when the error is not a rate limit.
 */
export function rateLimitRetryDelayMs(cause: unknown): number | null {
  if (!(cause instanceof ApiClientError) || cause.status !== 429) return null;
  const details = cause.details as { retryAfterSeconds?: unknown } | undefined;
  const seconds = typeof details?.retryAfterSeconds === "number" ? details.retryAfterSeconds : 1;
  return Math.min(Math.max(seconds, 0.25) * 1000, MAX_BOOTSTRAP_RETRY_DELAY_MS);
}

const sleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

function applySession(response: LoginResponse): Pick<
  AuthStoreState,
  "status" | "user" | "accessToken" | "expiresAt" | "error"
> {
  return {
    status: "authenticated",
    user: response.user,
    accessToken: response.access_token,
    expiresAt: Date.parse(response.expires_at),
    error: null
  };
}

export const useAuthStore = create<AuthStoreState>((set, get) => ({
  status: "idle",
  user: null,
  accessToken: null,
  expiresAt: null,
  loginInFlight: false,
  error: null,
  refreshInFlight: null,

  bootstrap: async () => {
    const status = get().status;
    if (status !== "idle") return;
    set({ status: "bootstrapping" });
    for (let attempt = 1; attempt <= BOOTSTRAP_ATTEMPTS; attempt++) {
      try {
        const response = await postRefresh();
        set(applySession(response));
        return;
      } catch (cause) {
        // Rate-limited: the session may well be valid, so wait and try again
        // instead of sending the user to the login page.
        const delay = rateLimitRetryDelayMs(cause);
        if (delay !== null && attempt < BOOTSTRAP_ATTEMPTS) {
          await sleep(delay);
          continue;
        }
        break;
      }
    }
    {
      // no valid refresh cookie — that's fine, user simply needs to log in
      set({
        status: "unauthenticated",
        user: null,
        accessToken: null,
        expiresAt: null,
        error: null
      });
    }
  },

  login: async (username, password) => {
    set({ loginInFlight: true, error: null });
    try {
      const response = await postLogin({ username, password });
      set({ ...applySession(response), loginInFlight: false });
      return response.user;
    } catch (cause) {
      const message =
        cause instanceof ApiClientError
          ? cause.message
          : "Could not reach the server. Check your connection and try again.";
      set({ loginInFlight: false, error: message });
      throw cause;
    }
  },

  refresh: () => {
    const existing = get().refreshInFlight;
    if (existing) return existing;
    const promise = (async () => {
      try {
        const response = await postRefresh();
        set(applySession(response));
        return response.user;
      } finally {
        set({ refreshInFlight: null });
      }
    })();
    set({ refreshInFlight: promise });
    return promise;
  },

  logout: (options) => {
    // The refresh cookie is HttpOnly, so only the backend can end the session:
    // revoke it server-side and have the cookie cleared. Fire and forget — the
    // local state is cleared immediately either way. The 401-retry path in the
    // API client skips this because its cookie is already dead.
    if (options?.revokeSession !== false) {
      void postLogout().catch(() => {
        // Nothing sensible to do: the local session is gone regardless.
      });
    }
    set({
      status: "unauthenticated",
      user: null,
      accessToken: null,
      expiresAt: null,
      error: null,
      refreshInFlight: null
    });
    // Reset every other session-scoped store so a subsequent user lands on
    // a clean shell.
    onLogoutHooks.forEach((hook) => hook());
  },

  clearError: () => set({ error: null })
}));

/**
 * Reset the store to its initial state. Test-only helper.
 */
export function resetAuthStoreForTest(): void {
  useAuthStore.setState({
    status: "idle",
    user: null,
    accessToken: null,
    expiresAt: null,
    loginInFlight: false,
    error: null,
    refreshInFlight: null
  });
}
