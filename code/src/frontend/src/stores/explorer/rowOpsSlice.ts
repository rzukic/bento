import type { StateCreator } from "zustand";
import { createRow, deleteRow as deleteRowApi } from "../../api/data";
import {
  adjustMetaTotal,
  buildDuplicatePayload,
  buildLocalBlankRow,
  dropPendingNewRows,
  freshTableView,
  getTableModel,
  getWritableColumns,
  removeRecordCacheEntry,
  runInConcurrentBatches,
  trimUndoStack,
  upsertRecordCacheEntry
} from "./helpers";
import type {
  ExplorerStoreState,
  RowOpsSlice,
  UndoDeletedRowSnapshot,
  UndoEntry
} from "./types";

const UNDO_RESTORE_CONCURRENCY = 4;

export const initialRowOpsState = {
  undoStackByTable: {} as Record<string, UndoEntry[]>,
};

export const createRowOpsSlice: StateCreator<ExplorerStoreState, [], [], RowOpsSlice> = (
  set,
  get
) => ({
  ...initialRowOpsState,

  toggleRowSelection: (table, rowId) => {
    set((state) => {
      const view = state.views[table] ?? freshTableView();
      const selectedRowIds = view.selectedRowIds.includes(rowId)
        ? view.selectedRowIds.filter((value) => value !== rowId)
        : [...view.selectedRowIds, rowId];

      return {
        activeTable: table,
        views: {
          ...state.views,
          [table]: {
            ...view,
            selectedRowIds
          }
        }
      };
    });
  },

  toggleSelectAllVisible: (table, rowIds) => {
    set((state) => {
      const view = state.views[table] ?? freshTableView();
      const normalizedRowIds = Array.from(new Set(rowIds.filter(Boolean)));
      const allSelected =
        normalizedRowIds.length > 0 &&
        normalizedRowIds.every((rowId) => view.selectedRowIds.includes(rowId));

      return {
        activeTable: table,
        views: {
          ...state.views,
          [table]: {
            ...view,
            selectedRowIds: allSelected ? [] : normalizedRowIds
          }
        }
      };
    });
  },

  setSelectedRows: (table, rowIds) => {
    set((state) => {
      const view = state.views[table] ?? freshTableView();

      return {
        activeTable: table,
        views: {
          ...state.views,
          [table]: {
            ...view,
            selectedRowIds: Array.from(new Set(rowIds.filter(Boolean)))
          }
        }
      };
    });
  },

  clearSelection: (table) => {
    const targetTable = table ?? get().activeTable;
    if (!targetTable) return;

    set((state) => {
      const view = state.views[targetTable] ?? freshTableView();
      if (view.selectedRowIds.length === 0) return state;

      return {
        activeTable: targetTable,
        views: {
          ...state.views,
          [targetTable]: {
            ...view,
            selectedRowIds: []
          }
        }
      };
    });
  },

  addBlankRow: (table) => {
    const tableModel = getTableModel(table);
    if (!tableModel) return;
    const firstWritableColumn = getWritableColumns(tableModel)[0];
    if (!firstWritableColumn) return;

    const localRowId = `__new__:${Date.now()}:${Math.random().toString(36).slice(2, 8)}`;
    const blankRow = buildLocalBlankRow(tableModel, localRowId);

    set((state) => {
      const currentView = state.views[table] ?? freshTableView();
      const view = dropPendingNewRows(currentView);
      return {
        activeTable: table,
        views: {
          ...state.views,
          [table]: {
            ...view,
            rows: [...view.rows, blankRow],
            meta: adjustMetaTotal(view.meta, view.perPage, 1),
            selectedRowIds: [],
            editingCell: { rowId: localRowId, columnName: firstWritableColumn.name },
            pendingNewRowIds: [...view.pendingNewRowIds, localRowId],
          },
        },
      };
    });
  },

  deleteRow: async (table, rowId) => {
    const stateBefore = get();
    const view = stateBefore.views[table];
    if (!view) return false;
    const rowIndex = view.rows.findIndex((row) => String(row.id ?? "") === rowId);
    if (rowIndex === -1) return false;
    const row = view.rows[rowIndex]!;
    const wasSelected = view.selectedRowIds.includes(rowId);

    if (view.pendingNewRowIds.includes(rowId)) {
      set((state) => {
        const currentView = state.views[table];
        if (!currentView) return state;
        const nextCellErrors = { ...currentView.cellErrors };
        const nextRowErrors = { ...currentView.rowErrors };
        delete nextCellErrors[rowId];
        delete nextRowErrors[rowId];
        return {
          views: {
            ...state.views,
            [table]: {
              ...currentView,
              rows: currentView.rows.filter((candidate) => String(candidate.id ?? "") !== rowId),
              meta: adjustMetaTotal(currentView.meta, currentView.perPage, -1),
              pendingNewRowIds: currentView.pendingNewRowIds.filter((id) => id !== rowId),
              pendingCellSaves: currentView.pendingCellSaves.filter(
                (key) => (key.split(":")[0] ?? "") !== rowId,
              ),
              selectedRowIds: currentView.selectedRowIds.filter((id) => id !== rowId),
              editingCell:
                currentView.editingCell?.rowId === rowId ? null : currentView.editingCell,
              cellErrors: nextCellErrors,
              rowErrors: nextRowErrors,
            },
          },
          recordCacheByTable: removeRecordCacheEntry(state.recordCacheByTable, table, rowId),
        };
      });
      return true;
    }

    set((state) => {
      const currentView = state.views[table];
      if (!currentView) return state;
      const nextRowErrors = { ...currentView.rowErrors };
      delete nextRowErrors[rowId];
      return {
        views: {
          ...state.views,
          [table]: {
            ...currentView,
            rows: currentView.rows.filter((candidate) => String(candidate.id ?? "") !== rowId),
            meta: adjustMetaTotal(currentView.meta, currentView.perPage, -1),
            selectedRowIds: currentView.selectedRowIds.filter((id) => id !== rowId),
            editingCell:
              currentView.editingCell?.rowId === rowId ? null : currentView.editingCell,
            rowErrors: nextRowErrors,
          },
        },
        recordCacheByTable: removeRecordCacheEntry(state.recordCacheByTable, table, rowId),
      };
    });

    try {
      await deleteRowApi(table, rowId);
      return true;
    } catch (err) {
      const message = err instanceof Error ? err.message : "Failed to delete row.";
      set((state) => {
        const currentView = state.views[table];
        if (!currentView) return state;
        const nextRows = currentView.rows.slice();
        nextRows.splice(rowIndex, 0, row);
        return {
          views: {
            ...state.views,
            [table]: {
              ...currentView,
              rows: nextRows,
              meta: adjustMetaTotal(currentView.meta, currentView.perPage, 1),
              selectedRowIds: wasSelected
                ? Array.from(new Set([...currentView.selectedRowIds, rowId]))
                : currentView.selectedRowIds,
              rowErrors: {
                ...currentView.rowErrors,
                [rowId]: message,
              },
            },
          },
          recordCacheByTable: upsertRecordCacheEntry(
            state.recordCacheByTable,
            table,
            rowId,
            row,
          ),
        };
      });
      return false;
    }
  },

  duplicateRow: async (table, rowId) => {
    const view = get().views[table];
    const tableModel = getTableModel(table);
    if (!view || !tableModel) return;
    const rowIndex = view.rows.findIndex((row) => String(row.id ?? "") === rowId);
    if (rowIndex === -1) return;
    const row = view.rows[rowIndex]!;

    try {
      const duplicated = await createRow(table, buildDuplicatePayload(tableModel, row));
      set((state) => {
        const currentView = state.views[table];
        if (!currentView) return state;
        const insertAfter = currentView.rows.findIndex(
          (candidate) => String(candidate.id ?? "") === rowId,
        );
        if (insertAfter === -1) return state;
        const nextRows = currentView.rows.slice();
        nextRows.splice(insertAfter + 1, 0, duplicated);
        const nextRowErrors = { ...currentView.rowErrors };
        delete nextRowErrors[rowId];
        return {
          views: {
            ...state.views,
            [table]: {
              ...currentView,
              rows: nextRows,
              meta: adjustMetaTotal(currentView.meta, currentView.perPage, 1),
              rowErrors: nextRowErrors,
            },
          },
        };
      });
    } catch (err) {
      const message = err instanceof Error ? err.message : "Failed to duplicate row.";
      set((state) => {
        const currentView = state.views[table];
        if (!currentView) return state;
        return {
          views: {
            ...state.views,
            [table]: {
              ...currentView,
              rowErrors: {
                ...currentView.rowErrors,
                [rowId]: message,
              },
            },
          },
        };
      });
    }
  },

  pushUndoDeleteEntry: (table, rows) => {
    const snapshots = rows
      .filter((item) => item.row)
      .map((item) => ({ index: item.index, row: { ...item.row } }));
    if (snapshots.length === 0) return;

    set((state) => {
      const current = state.undoStackByTable[table] ?? [];
      const nextEntry: UndoEntry = {
        kind: "delete",
        table,
        rows: snapshots,
        at: Date.now(),
      };
      return {
        undoStackByTable: {
          ...state.undoStackByTable,
          [table]: trimUndoStack([...current, nextEntry]),
        },
      };
    });
  },

  undoLast: async (table) => {
    const stack = get().undoStackByTable[table] ?? [];
    const entry = stack[stack.length - 1];
    if (!entry) {
      return { successCount: 0, failureCount: 0, total: 0 };
    }

    set((state) => ({
      undoStackByTable: {
        ...state.undoStackByTable,
        [table]: (state.undoStackByTable[table] ?? []).slice(0, -1),
      },
    }));

    const tableModel = getTableModel(table);
    if (!tableModel) {
      return {
        successCount: 0,
        failureCount: entry.rows.length,
        total: entry.rows.length,
      };
    }

    const results = await runInConcurrentBatches(
      entry.rows,
      UNDO_RESTORE_CONCURRENCY,
      async (snapshot) => {
        try {
          const recreated = await createRow(
            table,
            buildDuplicatePayload(tableModel, snapshot.row),
          );
          return { ok: true as const, snapshot, recreated };
        } catch (error) {
          return { ok: false as const, snapshot, error };
        }
      },
    );

    const successfulRestores = results
      .filter((result): result is { ok: true; snapshot: UndoDeletedRowSnapshot; recreated: Record<string, unknown> } => result.ok)
      .sort((left, right) => left.snapshot.index - right.snapshot.index);

    if (successfulRestores.length > 0) {
      set((state) => {
        const currentView = state.views[table];
        if (!currentView) return state;

        const nextRows = currentView.rows.slice();
        let inserted = 0;
        for (const restored of successfulRestores) {
          const insertAt = Math.min(restored.snapshot.index + inserted, nextRows.length);
          nextRows.splice(insertAt, 0, restored.recreated);
          inserted += 1;
        }

        const nextRowErrors = { ...currentView.rowErrors };
        for (const restored of successfulRestores) {
          delete nextRowErrors[String(restored.snapshot.row.id ?? "")];
        }

        return {
          views: {
            ...state.views,
            [table]: {
              ...currentView,
              rows: nextRows,
              meta: adjustMetaTotal(
                currentView.meta,
                currentView.perPage,
                successfulRestores.length,
              ),
              rowErrors: nextRowErrors,
            },
          },
        };
      });
    }

    return {
      successCount: successfulRestores.length,
      failureCount: entry.rows.length - successfulRestores.length,
      total: entry.rows.length,
    };
  },
});
