import { create } from "zustand";
import { listForms, type SavedForm } from "../api/forms";
import { registerLogoutHook } from "./authStore";

/**
 * The forms the user sees (Step 4.4), shared by the sidebar's "Quick add",
 * dashboards and the forms page. Loaded once; pages that change a form reload it.
 */
type FormsStoreState = {
  forms: SavedForm[] | null;
  error: string | null;
  inFlight: Promise<SavedForm[]> | null;
  load: (options?: { force?: boolean }) => Promise<SavedForm[]>;
  reset: () => void;
};

export const useFormsStore = create<FormsStoreState>((set, get) => ({
  forms: null,
  error: null,
  inFlight: null,
  load: (options) => {
    const state = get();
    if (state.forms && !options?.force) return Promise.resolve(state.forms);
    if (state.inFlight && !options?.force) return state.inFlight;
    const promise = listForms()
      .then((forms) => {
        set({ forms, error: null, inFlight: null });
        return forms;
      })
      .catch(() => {
        set({ forms: get().forms ?? [], error: "The forms could not be loaded.", inFlight: null });
        return get().forms ?? [];
      });
    set({ inFlight: promise });
    return promise;
  },
  reset: () => set({ forms: null, error: null, inFlight: null }),
}));

registerLogoutHook(() => useFormsStore.getState().reset());

/** Pinned forms, for "Quick add". */
export function pinnedForms(forms: SavedForm[] | null): SavedForm[] {
  return (forms ?? []).filter((f) => f.form.pinned);
}

export function resetFormsStoreForTest(): void {
  useFormsStore.getState().reset();
}
