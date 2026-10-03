import type { StateCreator } from "zustand";
import { createRow, patchRow } from "../../api/data";
import {
  buildCreatePayload,
  clearCellError,
  dropEmptyDraftRows,
  extractFieldErrors,
  getNextDraftColumn,
  getTableModel,
  isCreateReady,
  normalizeRowId,
  removeRecordCacheEntry,
  setCellErrorIn,
  upsertRecordCacheEntry
} from "./helpers";
import type { CellCommitIntent, EditingSlice, ExplorerStoreState, TableViewState } from "./types";
import type { ModelTable } from "../../types/api";

/** Ends an edit on a draft row according to why the editor committed. */
function settleDraftEdit(
  view: TableViewState,
  tableModel: ModelTable | null,
  rowId: string,
  columnName: string,
  intent: CellCommitIntent,
): TableViewState {
  if (intent === "advance" && tableModel) {
    const row = view.rows.find((candidate) => String(candidate.id ?? "") === rowId);
    const next = row ? getNextDraftColumn(tableModel, row, columnName) : null;
    return { ...view, editingCell: next ? { rowId, columnName: next } : null };
  }
  const ended = { ...view, editingCell: null };
  return intent === "leave" ? dropEmptyDraftRows(ended, tableModel) : ended;
}

export const createEditingSlice: StateCreator<ExplorerStoreState, [], [], EditingSlice> = (
  set,
  get
) => ({
  startCellEdit: (table, rowId, columnName) => {
    set((state) => {
      const view = state.views[table];
      if (!view) return state;
      // A draft is read-only for the moment it is being created on the server.
      if (
        view.pendingNewRowIds.includes(rowId) &&
        view.pendingCellSaves.some((key) => key.startsWith(`${rowId}:`))
      ) {
        return state;
      }
      const nextView =
        view.editingCell && view.editingCell.rowId !== rowId
          ? dropEmptyDraftRows(view, getTableModel(table), { preserveRowIds: [rowId] })
          : view;
      return {
        views: {
          ...state.views,
          [table]: { ...nextView, editingCell: { rowId, columnName } }
        }
      };
    });
  },

  cancelCellEdit: (table) => {
    const targetTable = table ?? get().activeTable;
    if (!targetTable) return;
    set((state) => {
      const view = state.views[targetTable];
      if (!view || view.editingCell === null) return state;
      // Escape only abandons the field; the draft goes too if it is still empty.
      const nextView = view.pendingNewRowIds.includes(view.editingCell.rowId)
        ? dropEmptyDraftRows(view, getTableModel(targetTable))
        : view;
      return {
        views: {
          ...state.views,
          [targetTable]: { ...nextView, editingCell: null }
        }
      };
    });
  },

  cellEditCommitted: (table, rowId, columnName, _nextValue) => {
    set((state) => {
      const view = state.views[table];
      if (!view) return state;
      const rowErrors = view.cellErrors[rowId];
      let nextErrors = view.cellErrors;
      if (rowErrors && rowErrors[columnName] !== undefined) {
        const { [columnName]: _removed, ...remainingForRow } = rowErrors;
        nextErrors = { ...view.cellErrors };
        if (Object.keys(remainingForRow).length === 0) {
          delete nextErrors[rowId];
        } else {
          nextErrors[rowId] = remainingForRow;
        }
      }
      return {
        views: {
          ...state.views,
          [table]: { ...view, editingCell: null, cellErrors: nextErrors }
        }
      };
    });
  },

  setCellError: (table, rowId, columnName, message) => {
    set((state) => {
      const view = state.views[table];
      if (!view) return state;
      const nextErrors = { ...view.cellErrors };
      const rowErrors = { ...(nextErrors[rowId] ?? {}) };
      if (message === null) {
        delete rowErrors[columnName];
        if (Object.keys(rowErrors).length === 0) {
          delete nextErrors[rowId];
        } else {
          nextErrors[rowId] = rowErrors;
        }
      } else {
        rowErrors[columnName] = message;
        nextErrors[rowId] = rowErrors;
      }
      return {
        views: {
          ...state.views,
          [table]: { ...view, cellErrors: nextErrors }
        }
      };
    });
  },

  commitCellEdit: async (table, rowId, columnName, nextValue, intent = "stay") => {
    const stateBefore = get();
    const view = stateBefore.views[table];
    if (!view) return;
    const rowIndex = view.rows.findIndex((r) => String(r.id ?? "") === rowId);
    const cachedRow = stateBefore.recordCacheByTable[table]?.[rowId] ?? null;
    const row = rowIndex === -1 ? cachedRow : view.rows[rowIndex]!;
    if (!row) return;
    const prevValue = row[columnName];
    const isPendingNewRow = view.pendingNewRowIds.includes(rowId);
    const tableModel = isPendingNewRow ? getTableModel(table) : null;
    if (isPendingNewRow && !tableModel) return;
    const createPayload = isPendingNewRow
      ? buildCreatePayload(tableModel!, row, columnName, nextValue)
      : null;
    const createReady = isPendingNewRow
      ? isCreateReady(tableModel!, row, columnName, nextValue)
      : false;

    // A complete draft whose last create failed retries even without a change.
    const retryCreate =
      isPendingNewRow && createReady && view.cellErrors[rowId] !== undefined;

    if (Object.is(prevValue, nextValue) && !retryCreate) {
      if (isPendingNewRow) {
        set((s) => {
          const v = s.views[table];
          if (!v) return s;
          return {
            views: {
              ...s.views,
              [table]: settleDraftEdit(v, tableModel, rowId, columnName, intent),
            },
          };
        });
        return;
      }
      // No-op: just exit edit mode without hitting the API.
      get().cellEditCommitted(table, rowId, columnName, nextValue);
      return;
    }

    if (isPendingNewRow && (!createReady || (createPayload && Object.keys(createPayload).length === 0))) {
      set((s) => {
        const v = s.views[table];
        if (!v) return s;
        const idx = v.rows.findIndex((candidate) => String(candidate.id ?? "") === rowId);
        const nextRows = v.rows.slice();
        if (idx !== -1) {
          nextRows[idx] = { ...nextRows[idx]!, [columnName]: nextValue };
        }
        const cacheRow =
          idx !== -1 ? nextRows[idx]! : (s.recordCacheByTable[table]?.[rowId] ?? null);
        const nextCache =
          cacheRow === null
            ? s.recordCacheByTable
            : upsertRecordCacheEntry(s.recordCacheByTable, table, rowId, cacheRow);
        return {
          views: {
            ...s.views,
            [table]: settleDraftEdit(
              {
                ...v,
                rows: nextRows,
                cellErrors: clearCellError(v.cellErrors, rowId, columnName),
              },
              tableModel,
              rowId,
              columnName,
              intent,
            ),
          },
          recordCacheByTable: nextCache,
        };
      });
      return;
    }

    const pendingKey = `${rowId}:${columnName}`;

    // Optimistic apply: write the new value into the row, exit edit mode,
    // clear any prior error on this cell, and mark the cell as pending.
    set((s) => {
      const v = s.views[table];
      if (!v) return s;
      const idx = v.rows.findIndex((candidate) => String(candidate.id ?? "") === rowId);
      const nextRows = v.rows.slice();
      const currentCacheRow = s.recordCacheByTable[table]?.[rowId] ?? null;
      const baseRow = idx !== -1 ? nextRows[idx]! : currentCacheRow;
      if (!baseRow) return s;
      const nextRow = { ...baseRow, [columnName]: nextValue };
      if (idx !== -1) {
        nextRows[idx] = nextRow;
      }
      const nextErrors = clearCellError(v.cellErrors, rowId, columnName);
      return {
        views: {
          ...s.views,
          [table]: {
            ...v,
            rows: nextRows,
            editingCell: null,
            cellErrors: nextErrors,
            pendingCellSaves: v.pendingCellSaves.includes(pendingKey)
              ? v.pendingCellSaves
              : [...v.pendingCellSaves, pendingKey],
          },
        },
        recordCacheByTable: upsertRecordCacheEntry(
          s.recordCacheByTable,
          table,
          rowId,
          nextRow,
        ),
      };
    });

    try {
      const updatedRaw = isPendingNewRow
        ? await createRow(table, createPayload ?? {})
        : await patchRow(table, rowId, { [columnName]: nextValue });
      const updated = tableModel
        ? normalizeRowId(tableModel, updatedRaw, rowId)
        : updatedRaw;
      set((s) => {
        const v = s.views[table];
        if (!v) return s;
        const idx = v.rows.findIndex((r) => String(r.id ?? "") === rowId);
        const nextRows = v.rows.slice();
        if (idx !== -1) {
          if (isPendingNewRow) {
            nextRows[idx] = updated;
          } else {
            // Reconcile from server response so columns like updated_at are fresh.
            nextRows[idx] = { ...nextRows[idx]!, ...updated };
          }
        }
        const nextCellErrors = { ...v.cellErrors };
        const nextRowErrors = { ...v.rowErrors };
        if (isPendingNewRow) {
          delete nextCellErrors[rowId];
          delete nextRowErrors[rowId];
        }
        const updatedRowId = String(updated.id ?? rowId);
        let nextRecordCache = removeRecordCacheEntry(s.recordCacheByTable, table, rowId);
        nextRecordCache = upsertRecordCacheEntry(
          nextRecordCache,
          table,
          updatedRowId,
          updated,
        );
        return {
          views: {
            ...s.views,
            [table]: {
              ...v,
              rows: nextRows,
              pendingCellSaves: v.pendingCellSaves.filter((k) => k !== pendingKey),
              pendingNewRowIds: isPendingNewRow
                ? v.pendingNewRowIds.filter((id) => id !== rowId)
                : v.pendingNewRowIds,
              editingCell:
                isPendingNewRow &&
                v.editingCell !== null &&
                v.editingCell.rowId === rowId
                  ? { ...v.editingCell, rowId: updatedRowId }
                  : v.editingCell,
              cellErrors: nextCellErrors,
              rowErrors: nextRowErrors,
            },
          },
          recordCacheByTable: nextRecordCache,
        };
      });
    } catch (err) {
      const fieldErrors = extractFieldErrors(err);
      const fallback = err instanceof Error ? err.message : "Couldn't save. Try again.";
      // A draft keeps what the user typed so they can fix it and retry;
      // an existing row goes back to its saved value.
      const restoredValue = isPendingNewRow ? nextValue : prevValue;

      set((s) => {
        const v = s.views[table];
        if (!v) return s;
        const idx = v.rows.findIndex((r) => String(r.id ?? "") === rowId);
        const nextRows = v.rows.slice();
        if (idx !== -1) {
          nextRows[idx] = { ...nextRows[idx]!, [columnName]: restoredValue };
        }
        const currentCacheRow = s.recordCacheByTable[table]?.[rowId] ?? null;
        let nextRecordCache = s.recordCacheByTable;
        if (currentCacheRow) {
          nextRecordCache = upsertRecordCacheEntry(
            s.recordCacheByTable,
            table,
            rowId,
            { ...currentCacheRow, [columnName]: restoredValue },
          );
        }
        let nextErrors = v.cellErrors;
        if (fieldErrors && fieldErrors.length > 0) {
          for (const fe of fieldErrors) {
            nextErrors = setCellErrorIn(nextErrors, rowId, fe.field, fe.message);
          }
          // If the backend didn't flag the column we just edited but the save
          // still failed, surface the generic message there too so the cell
          // shows its red underline.
          if (!fieldErrors.some((fe) => fe.field === columnName)) {
            nextErrors = setCellErrorIn(nextErrors, rowId, columnName, fallback);
          }
        } else {
          nextErrors = setCellErrorIn(nextErrors, rowId, columnName, fallback);
        }
        return {
          views: {
            ...s.views,
            [table]: {
              ...v,
              rows: nextRows,
              cellErrors: nextErrors,
              pendingCellSaves: v.pendingCellSaves.filter((k) => k !== pendingKey),
            },
          },
          recordCacheByTable: nextRecordCache,
        };
      });
    }
  },
});
