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
    try {
      const response = await postRefresh();
      set(applySession(response));
    } catch {
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
          : "Unable to reach the authentication service.";
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
