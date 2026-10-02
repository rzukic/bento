import type { StateCreator } from "zustand";
import { fetchRow } from "../../api/data";
import { ApiClientError } from "../../api/client";
import {
  freshTableView,
  getPrimaryKeyColumns,
  getTableModel,
  normalizeRowId,
  upsertRecordCacheEntry
} from "./helpers";
import type {
  ExplorerStoreState,
  RecordCacheByTable,
  RecordEditorEntry,
  RecordEditorSlice,
  RecordEditorState
} from "./types";

export const initialRecordEditorState = {
  recordCacheByTable: {} as RecordCacheByTable,
  recordEditor: null as RecordEditorState | null,
};

/**
 * Drive the async fetch for a single record-editor stack entry (TASK-099). On
 * success the record is written into the editor cache, and into `views[table]`
 * only if that row is already present in the current result set. On failure
 * only the target entry flips to "error" — earlier entries in the stack are
 * untouched.
 *
 * `expectedIndex` lets us tolerate out-of-order async completions: if the
 * user pops before the fetch resolves, or pushes a new entry above, we bail
 * out instead of clobbering unrelated state.
 */
async function loadRecordEditorEntry(
  set: (partial: Partial<ExplorerStoreState> | ((state: ExplorerStoreState) => Partial<ExplorerStoreState> | ExplorerStoreState)) => void,
  _get: () => ExplorerStoreState,
  table: string,
  rowIdString: string,
  expectedIndex: number
): Promise<void> {
  try {
    const tableModel = getTableModel(table);
    const fetchedRaw = await fetchRow(table, rowIdString);
    const fetched = tableModel
      ? normalizeRowId(tableModel, fetchedRaw, rowIdString)
      : fetchedRaw;
    set((state) => {
      const editor = state.recordEditor;
      if (!editor) return state;
      const target = editor.stack[expectedIndex];
      if (!target || target.table !== table || target.rowId !== rowIdString) return state;

      const nextStack = editor.stack.slice();
      nextStack[expectedIndex] = {
        ...target,
        status: "ready",
        row: fetched,
        error: null,
      };

      const view = state.views[table];
      const nextRecordCache = upsertRecordCacheEntry(
        state.recordCacheByTable,
        table,
        rowIdString,
        fetched,
      );
      const updatedViews = view
        ? (() => {
            const idx = view.rows.findIndex(
              (row) => String(row.id ?? "") === rowIdString,
            );
            if (idx === -1) return state.views;
            const nextRows = view.rows.slice();
            nextRows[idx] = { ...nextRows[idx]!, ...fetched };
            return {
              ...state.views,
              [table]: { ...view, rows: nextRows },
            };
          })()
        : state.views;

      return {
        views: updatedViews,
        recordCacheByTable: nextRecordCache,
        recordEditor: { ...editor, stack: nextStack },
      };
    });
  } catch (cause) {
    const message =
      cause instanceof ApiClientError
        ? cause.message
        : cause instanceof Error
          ? cause.message
          : "Unable to load record.";
    set((state) => {
      const editor = state.recordEditor;
      if (!editor) return state;
      const target = editor.stack[expectedIndex];
      if (!target || target.table !== table || target.rowId !== rowIdString) return state;
      const nextStack = editor.stack.slice();
      nextStack[expectedIndex] = { ...target, status: "error", error: message };
      return { recordEditor: { ...editor, stack: nextStack } };
    });
  }
}

export const createRecordEditorSlice: StateCreator<
  ExplorerStoreState,
  [],
  [],
  RecordEditorSlice
> = (set, get) => ({
  ...initialRecordEditorState,

  openRecordEditor: async ({ table, rowId, row: providedRow = null }) => {
    const rowIdString = String(rowId);
    let shouldFetch = true;
    set((state) => {
      const view = state.views[table] ?? freshTableView();
      const existing =
        providedRow ??
        view.rows.find((candidate) => String(candidate.id ?? "") === rowIdString) ??
        null;
      const tableModel = getTableModel(table);
      shouldFetch = !(
        existing !== null &&
        ((tableModel !== null && getPrimaryKeyColumns(tableModel).length > 1) ||
          table.startsWith("_jt_"))
      );
      const entry: RecordEditorEntry = {
        table,
        rowId: rowIdString,
        status: shouldFetch ? "loading" : "ready",
        row: existing,
        error: null,
      };
      return {
        views: state.views[table] ? state.views : { ...state.views, [table]: view },
        recordCacheByTable:
          existing === null
            ? state.recordCacheByTable
            : upsertRecordCacheEntry(state.recordCacheByTable, table, rowIdString, existing),
        recordEditor: { open: true, stack: [entry] },
      };
    });

    if (!shouldFetch) return;
    await loadRecordEditorEntry(set, get, table, rowIdString, 0);
  },

  pushRecordEditor: async ({ table, rowId, row: providedRow = null }) => {
    const current = get().recordEditor;
    if (!current || !current.open) return;

    const rowIdString = String(rowId);
    // No-op if the top entry already matches (prevent double-push on re-click).
    const top = current.stack[current.stack.length - 1];
    if (top && top.table === table && top.rowId === rowIdString) return;

    let pushedIndex = -1;
    let shouldFetch = true;
    set((state) => {
      const editor = state.recordEditor;
      if (!editor || !editor.open) return state;
      const view = state.views[table] ?? freshTableView();
      const existing =
        providedRow ??
        view.rows.find((row) => String(row.id ?? "") === rowIdString) ??
        null;
      const tableModel = getTableModel(table);
      shouldFetch = !(
        existing !== null &&
        ((tableModel !== null && getPrimaryKeyColumns(tableModel).length > 1) ||
          table.startsWith("_jt_"))
      );
      const entry: RecordEditorEntry = {
        table,
        rowId: rowIdString,
        status: shouldFetch ? "loading" : "ready",
        row: existing,
        error: null,
      };
      const nextStack = [...editor.stack, entry];
      pushedIndex = nextStack.length - 1;
      return {
        views: state.views[table] ? state.views : { ...state.views, [table]: view },
        recordCacheByTable:
          existing === null
            ? state.recordCacheByTable
            : upsertRecordCacheEntry(state.recordCacheByTable, table, rowIdString, existing),
        recordEditor: { ...editor, stack: nextStack },
      };
    });

    if (pushedIndex === -1) return;
    if (!shouldFetch) return;
    await loadRecordEditorEntry(set, get, table, rowIdString, pushedIndex);
  },

  popRecordEditor: () => {
    set((state) => {
      const editor = state.recordEditor;
      if (!editor) return state;
      if (editor.stack.length <= 1) {
        return { recordEditor: null };
      }
      return {
        recordEditor: { ...editor, stack: editor.stack.slice(0, -1) },
      };
    });
  },

  closeRecordEditor: () => {
    set({ recordEditor: null });
  },

  retryRecordEditorLoad: async () => {
    const editor = get().recordEditor;
    if (!editor || editor.stack.length === 0) return;
    const topIndex = editor.stack.length - 1;
    const top = editor.stack[topIndex]!;
    set((state) => {
      const current = state.recordEditor;
      if (!current) return state;
      const nextStack = current.stack.slice();
      const target = nextStack[topIndex];
      if (!target) return state;
      nextStack[topIndex] = { ...target, status: "loading", error: null };
      return { recordEditor: { ...current, stack: nextStack } };
    });
    await loadRecordEditorEntry(set, get, top.table, top.rowId, topIndex);
  },
});
