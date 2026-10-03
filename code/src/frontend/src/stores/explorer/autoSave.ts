import type { AutoSaveTarget, ExplorerStoreState } from "./types";

// ── Auto-save debounce ─────────────────────────────────────────────────────
// Module-level timers so they survive store re-renders without polluting
// persisted state. Timers are isolated per scheduling table/view target.
//
// The helpers take the store's `get` accessor rather than importing the
// composed store, so this module never depends on `explorerStore.ts`.

type GetExplorerState = () => ExplorerStoreState;

const AUTO_SAVE_DEBOUNCE_MS = 500;
const _autoSaveTimers = new Map<string, ReturnType<typeof setTimeout>>();

export function getAutoSaveTarget(
  get: GetExplorerState,
  tableId?: string | null
): AutoSaveTarget | null {
  const store = get();
  const targetTable = tableId ?? store.activeTable;
  if (!targetTable) return null;
  const view = store.views[targetTable];
  if (!view || view.activeViewId === null) return null;
  return { tableId: targetTable, viewId: view.activeViewId };
}

function getAutoSaveKey(target: AutoSaveTarget): string {
  return `${target.tableId}:${target.viewId}`;
}

export function scheduleAutoSave(
  get: GetExplorerState,
  target = getAutoSaveTarget(get)
): void {
  if (!target) return;
  const key = getAutoSaveKey(target);
  const existing = _autoSaveTimers.get(key);
  if (existing !== undefined) {
    clearTimeout(existing);
  }

  const timer = setTimeout(() => {
    _autoSaveTimers.delete(key);
    const latest = get();
    const view = latest.views[target.tableId];
    if (!view?.autoSave) return;
    void latest.updateViewConfigFor(target.tableId, target.viewId);
  }, AUTO_SAVE_DEBOUNCE_MS);

  _autoSaveTimers.set(key, timer);
}

export function cancelAutoSave(target?: AutoSaveTarget | null): void {
  if (!target) {
    for (const timer of _autoSaveTimers.values()) {
      clearTimeout(timer);
    }
    _autoSaveTimers.clear();
    return;
  }

  const key = getAutoSaveKey(target);
  const timer = _autoSaveTimers.get(key);
  if (timer === undefined) return;
  clearTimeout(timer);
  _autoSaveTimers.delete(key);
}
