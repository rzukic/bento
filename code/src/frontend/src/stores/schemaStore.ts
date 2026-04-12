import { create } from "zustand";
import { getSchemaSnapshot } from "../api/schema";
import { ApiClientError } from "../api/client";
import { registerLogoutHook } from "./authStore";
import type { ModelSnapshot } from "../types/api";

export type SchemaStatus = "idle" | "loading" | "ready" | "error";

type SchemaStoreState = {
  status: SchemaStatus;
  snapshot: ModelSnapshot | null;
  error: string | null;
  lastLoadedAt: number | null;
  inFlight: Promise<ModelSnapshot | null> | null;

  load: (options?: { force?: boolean }) => Promise<ModelSnapshot | null>;
  reset: () => void;
};

const initialState = {
  status: "idle" as SchemaStatus,
  snapshot: null,
  error: null,
  lastLoadedAt: null,
  inFlight: null
};

export const useSchemaStore = create<SchemaStoreState>((set, get) => ({
  ...initialState,

  load: (options) => {
    const state = get();
    // Serve the cached snapshot without hitting the network unless the
    // caller explicitly asks for a fresh copy. Later modules (Data Explorer,
    // Schema Designer) can rely on this to avoid redundant round-trips.
    if (state.status === "ready" && state.snapshot && !options?.force) {
      return Promise.resolve(state.snapshot);
    }
    const existing = state.inFlight;
    if (existing && !options?.force) return existing;

    const promise = (async () => {
      set({ status: "loading", error: null });
      try {
        const snapshot = await getSchemaSnapshot();
        set({
          status: "ready",
          snapshot,
          error: null,
          lastLoadedAt: Date.now(),
          inFlight: null
        });
        return snapshot;
      } catch (cause) {
        const message =
          cause instanceof ApiClientError
            ? cause.message
            : "Unable to load the schema snapshot.";
        set({ status: "error", error: message, inFlight: null });
        return null;
      }
    })();

    set({ inFlight: promise });
    return promise;
  },

  reset: () => {
    set({ ...initialState });
  }
}));

/**
 * Drop the cached snapshot on logout so the next user never sees the
 * previous session's schema.
 */
registerLogoutHook(() => {
  useSchemaStore.getState().reset();
});

/** Test-only helper. */
export function resetSchemaStoreForTest(): void {
  useSchemaStore.setState({ ...initialState });
}
