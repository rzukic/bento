import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { migrateLegacyStorageKeys } from "../stores/appStore";

function createMemoryStorage(): Storage {
  const values = new Map<string, string>();
  return {
    get length() {
      return values.size;
    },
    clear: () => values.clear(),
    getItem: (key) => values.get(key) ?? null,
    key: (index) => Array.from(values.keys())[index] ?? null,
    removeItem: (key) => {
      values.delete(key);
    },
    setItem: (key, value) => {
      values.set(key, String(value));
    },
  };
}

describe("migrateLegacyStorageKeys", () => {
  beforeEach(() => {
    vi.stubGlobal("localStorage", createMemoryStorage());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("moves settings saved under the old names to the current keys", () => {
    window.localStorage.setItem("repere.app.themeMode", "dark");
    window.localStorage.setItem("repere.app.sidebarOpen", "false");
    window.localStorage.setItem("repere.schema.mode", "json");

    migrateLegacyStorageKeys();

    expect(window.localStorage.getItem("bento.app.themeMode")).toBe("dark");
    expect(window.localStorage.getItem("bento.app.sidebarOpen")).toBe("false");
    expect(window.localStorage.getItem("bento.schema.mode")).toBe("json");
    expect(window.localStorage.getItem("repere.app.themeMode")).toBeNull();
    expect(window.localStorage.getItem("repere.app.sidebarOpen")).toBeNull();
    expect(window.localStorage.getItem("repere.schema.mode")).toBeNull();
  });

  it("keeps a value already saved under the current key", () => {
    window.localStorage.setItem("bento.app.themeMode", "light");
    window.localStorage.setItem("repere.app.themeMode", "dark");

    migrateLegacyStorageKeys();

    expect(window.localStorage.getItem("bento.app.themeMode")).toBe("light");
    expect(window.localStorage.getItem("repere.app.themeMode")).toBeNull();
  });
});
