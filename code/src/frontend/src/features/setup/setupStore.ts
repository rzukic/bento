import { create } from "zustand";
import { ApiClientError } from "../../api/client";
import type { SetupStatus } from "../../types/setup";
import { getSetupStatus } from "./api";

/**
 * Installation state lifecycle:
 *
 *   idle    — nothing fetched yet
 *   loading — `GET /setup/status` in flight
 *   ready   — `info` is populated
 *   error   — the status call failed; the app must keep working, so callers
 *             treat this like an initialised installation
 */
export type SetupStoreStatus = "idle" | "loading" | "ready" | "error";

type SetupStoreState = {
  status: SetupStoreStatus;
  info: SetupStatus | null;
  error: string | null;
  /**
   * Set when the wizard is entered on an uninitialised installation. The
   * admin step flips `info.initialized` to `true` mid-flow, and without this
   * flag `/setup` would bounce the user to the workspace before the sample
   * data and done steps.
   */
  wizardActive: boolean;

  load: (options?: { force?: boolean }) => Promise<SetupStatus | null>;
  beginWizard: () => void;
  markInitialized: () => void;
  completeWizard: () => void;
};

export const useSetupStore = create<SetupStoreState>((set, get) => ({
  status: "idle",
  info: null,
  error: null,
  wizardActive: false,

  load: async (options) => {
    const state = get();
    if (state.status === "loading") return state.info;
    if (state.status === "ready" && state.info && !options?.force) return state.info;

    set({ status: "loading", error: null });
    try {
      const info = await getSetupStatus();
      set({ status: "ready", info, error: null });
      return info;
    } catch (cause) {
      const message =
        cause instanceof ApiClientError
          ? cause.message
          : "Unable to reach the backend to check the installation state.";
      // Keep whatever we knew before so a transient failure during the wizard
      // does not wipe the environment summary.
      set({ status: "error", error: message });
      return get().info;
    }
  },

  beginWizard: () => {
    if (!get().wizardActive) set({ wizardActive: true });
  },

  markInitialized: () => {
    const info = get().info;
    if (!info || info.initialized) return;
    set({ info: { ...info, initialized: true, registration_bootstrap_open: false } });
  },

  completeWizard: () => set({ wizardActive: false })
}));

/** Reset to the initial state. Test-only helper. */
export function resetSetupStoreForTest(): void {
  useSetupStore.setState({ status: "idle", info: null, error: null, wizardActive: false });
}

/**
 * Seed an initialised installation so router-level tests that are not about
 * the wizard skip the status round-trip. Test-only helper.
 */
export function seedSetupInitializedForTest(overrides: Partial<SetupStatus> = {}): void {
  useSetupStore.setState({
    status: "ready",
    error: null,
    wizardActive: false,
    info: {
      initialized: true,
      auth_mode: "standard",
      auth_required: true,
      registration_bootstrap_open: false,
      database_provider: "sqlite",
      schema_version: 0,
      sample_data_available: true,
      version: "test",
      ...overrides
    }
  });
}
