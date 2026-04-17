import { create } from "zustand";

export type ThemeMode = "system" | "light" | "dark";

type AppStoreState = {
  themeMode: ThemeMode;
  sidebarOpen: boolean;
  setThemeMode: (themeMode: ThemeMode) => void;
  setSidebarOpen: (sidebarOpen: boolean) => void;
  toggleSidebar: () => void;
};

const THEME_MODE_STORAGE_KEY = "repere.app.themeMode";

function loadPersistedThemeMode(): ThemeMode {
  if (typeof window === "undefined") return "system";
  try {
    const raw = window.localStorage.getItem(THEME_MODE_STORAGE_KEY);
    if (raw === "light" || raw === "dark" || raw === "system") return raw;
  } catch {
    /* ignore */
  }
  return "system";
}

function persistThemeMode(mode: ThemeMode): void {
  if (typeof window === "undefined") return;
  try {
    window.localStorage.setItem(THEME_MODE_STORAGE_KEY, mode);
  } catch {
    /* ignore */
  }
}

const initialAppStoreState = {
  themeMode: loadPersistedThemeMode(),
  sidebarOpen: true
};

export const useAppStore = create<AppStoreState>((set, get) => ({
  ...initialAppStoreState,
  setThemeMode: (themeMode) => {
    persistThemeMode(themeMode);
    set({ themeMode });
  },
  setSidebarOpen: (sidebarOpen) => set({ sidebarOpen }),
  toggleSidebar: () => set({ sidebarOpen: !get().sidebarOpen })
}));

/**
 * Applies the current theme mode to the document root as `data-theme` and a
 * CSS color-scheme hint. The shared primitive layer reads from these tokens,
 * so switching the attribute gives the whole shell a practical light/dark
 * baseline without per-component theme branches.
 */
export function applyThemeToDocument(mode: ThemeMode): void {
  if (typeof document === "undefined") return;
  const root = document.documentElement;
  const resolved =
    mode === "system"
      ? window.matchMedia?.("(prefers-color-scheme: light)").matches
        ? "light"
        : "dark"
      : mode;
  root.dataset.theme = resolved;
  root.style.colorScheme = resolved;
}

// Subscribe once so every `setThemeMode` reflects in the DOM. Selector form
// keeps this cheap: we only re-run when themeMode changes.
useAppStore.subscribe((state, prev) => {
  if (state.themeMode !== prev.themeMode) {
    applyThemeToDocument(state.themeMode);
  }
});

// Apply the initial value on module load (no-op under SSR/jsdom).
applyThemeToDocument(useAppStore.getState().themeMode);

export function resetAppStoreForTest(): void {
  useAppStore.setState(initialAppStoreState);
  applyThemeToDocument(initialAppStoreState.themeMode);
}
