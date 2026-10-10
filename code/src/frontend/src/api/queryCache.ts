import { registerLogoutHook } from "../stores/authStore";
import { dataVersion } from "./client";

/** How long a result counts as current: within it, the same request is not sent again. */
export const FRESH_MS = 30_000;
const MAX_ENTRIES = 300;

type Entry = { value: unknown; at: number; version: number };

const entries = new Map<string, Entry>();
const pending = new Map<string, Promise<unknown>>();

registerLogoutHook(() => {
  entries.clear();
  pending.clear();
});

/**
 * The last result for a request, if any. `fresh` while it is younger than
 * FRESH_MS and nothing was changed since (a save, an import, a schema change
 * — any write through the API).
 */
export function peekCached<T>(key: string): { value: T; fresh: boolean } | undefined {
  const entry = entries.get(key);
  if (!entry) return undefined;
  return { value: entry.value as T, fresh: entry.version === dataVersion() && Date.now() - entry.at < FRESH_MS };
}

/**
 * Runs a request, or joins the same one already on its way, and keeps the
 * result. `force` (the refresh button, the dashboard's interval) always asks
 * the server again.
 */
export function loadCached<T>(key: string, load: () => Promise<T>, force = false): Promise<T> {
  const running = pending.get(key);
  if (running && !force) return running as Promise<T>;
  const version = dataVersion();
  const promise = load().then(
    (value) => {
      entries.delete(key);
      entries.set(key, { value, at: Date.now(), version });
      if (entries.size > MAX_ENTRIES) entries.delete(entries.keys().next().value as string);
      return value;
    },
  );
  pending.set(key, promise);
  const done = () => {
    if (pending.get(key) === promise) pending.delete(key);
  };
  promise.then(done, done);
  return promise;
}

/** Forget everything (tests). */
export function clearQueryCache(): void {
  entries.clear();
  pending.clear();
}

// vitest.setup.ts clears the cache after each test without importing app modules.
if (import.meta.env.MODE === "test") (globalThis as { __bentoClearQueryCache?: () => void }).__bentoClearQueryCache = clearQueryCache;
