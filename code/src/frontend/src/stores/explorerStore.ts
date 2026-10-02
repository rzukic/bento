import { create } from "zustand";
import { registerLogoutHook } from "./authStore";
import { cancelAutoSave } from "./explorer/autoSave";
import { createEditingSlice } from "./explorer/editingSlice";
import { createQuerySlice, initialQueryState } from "./explorer/querySlice";
import {
  createRecordEditorSlice,
  initialRecordEditorState
} from "./explorer/recordEditorSlice";
import { createRowOpsSlice, initialRowOpsState } from "./explorer/rowOpsSlice";
import { createViewSlice, initialViewState } from "./explorer/viewSlice";
import type { ExplorerStoreState } from "./explorer/types";

// ── Public surface ────────────────────────────────────────────────────
// The store is composed from slices under `./explorer/`; everything that
// features/ and tests import keeps living at this path.

export type {
  ExplorerStoreState,
  RecordEditorEntry,
  RecordEditorState,
  SavedViewsStatus,
  TableViewState,
  TableViewStatus
} from "./explorer/types";

export {
  buildCompositeRowId,
  extractExplorerViewConfig,
  getExplorerQueryForView,
  hydrateTableViewFromConfig,
  isWritableColumn
} from "./explorer/helpers";

// ── Store ─────────────────────────────────────────────────────────────

const initialState = {
  activeTable: initialQueryState.activeTable,
  views: initialQueryState.views,
  recordCacheByTable: initialRecordEditorState.recordCacheByTable,
  defaultViewIdByTable: initialViewState.defaultViewIdByTable,
  undoStackByTable: initialRowOpsState.undoStackByTable,
  recordEditor: initialRecordEditorState.recordEditor,
};

export const useExplorerStore = create<ExplorerStoreState>()((set, get, api) => ({
  ...createQuerySlice(set, get, api),
  ...createEditingSlice(set, get, api),
  ...createRowOpsSlice(set, get, api),
  ...createViewSlice(set, get, api),
  ...createRecordEditorSlice(set, get, api),

  reset: () => {
    cancelAutoSave();
    set({ ...initialState });
  }
}));

// Reset on logout so the next user never sees stale rows.
registerLogoutHook(() => {
  useExplorerStore.getState().reset();
});

/** Test-only helper. */
export function resetExplorerStoreForTest(): void {
  cancelAutoSave();
  useExplorerStore.setState({ ...initialState });
}
