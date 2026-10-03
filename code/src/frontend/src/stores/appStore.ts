import { create } from "zustand";

export type ThemeMode = "system" | "light" | "dark";

export type SchemaWorkbenchMode = "visual" | "json";

type AppStoreState = {
  themeMode: ThemeMode;
  sidebarOpen: boolean;
  /** Last mode of the schema page; the diagram is the default. */
  schemaMode: SchemaWorkbenchMode;
  setSchemaMode: (mode: SchemaWorkbenchMode) => void;
  setThemeMode: (themeMode: ThemeMode) => void;
  setSidebarOpen: (sidebarOpen: boolean) => void;
  toggleSidebar: () => void;
};

const THEME_MODE_STORAGE_KEY = "repere.app.themeMode";
const SIDEBAR_OPEN_STORAGE_KEY = "repere.app.sidebarOpen";
const SCHEMA_MODE_STORAGE_KEY = "repere.schema.mode";

function loadPersistedSchemaMode(): SchemaWorkbenchMode {
  if (typeof window === "undefined") return "visual";
  try {
    return window.localStorage.getItem(SCHEMA_MODE_STORAGE_KEY) === "json" ? "json" : "visual";
  } catch {
    return "visual";
  }
}

function persistSchemaMode(mode: SchemaWorkbenchMode): void {
  if (typeof window === "undefined") return;
  try {
    window.localStorage.setItem(SCHEMA_MODE_STORAGE_KEY, mode);
  } catch {
    /* ignore */
  }
}

function loadPersistedSidebarOpen(): boolean {
  if (typeof window === "undefined") return true;
  try {
    return window.localStorage.getItem(SIDEBAR_OPEN_STORAGE_KEY) !== "false";
  } catch {
    return true;
  }
}

function persistSidebarOpen(open: boolean): void {
  if (typeof window === "undefined") return;
  try {
    window.localStorage.setItem(SIDEBAR_OPEN_STORAGE_KEY, String(open));
  } catch {
    /* ignore */
  }
}

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
  sidebarOpen: loadPersistedSidebarOpen(),
  schemaMode: loadPersistedSchemaMode()
};

export const useAppStore = create<AppStoreState>((set, get) => ({
  ...initialAppStoreState,
  setThemeMode: (themeMode) => {
    persistThemeMode(themeMode);
    set({ themeMode });
  },
  setSidebarOpen: (sidebarOpen) => {
    persistSidebarOpen(sidebarOpen);
    set({ sidebarOpen });
  },
  toggleSidebar: () => {
    const sidebarOpen = !get().sidebarOpen;
    persistSidebarOpen(sidebarOpen);
    set({ sidebarOpen });
  },
  setSchemaMode: (schemaMode) => {
    persistSchemaMode(schemaMode);
    set({ schemaMode });
  }
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
