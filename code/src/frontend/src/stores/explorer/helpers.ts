import { ApiClientError } from "../../api/client";
import { useAuthStore } from "../authStore";
import { useSchemaStore } from "../schemaStore";
import type {
  ExplorerViewConfig,
  ExplorerViewType,
  ExplorerQuery,
  ListMeta
} from "../../types/explorer";
import {
  buildExplorerQuery,
  DEFAULT_EXPLORER_VIEW_CONFIG,
  DEFAULT_QUERY,
  patchExplorerViewConfig
} from "../../types/explorer";
import type { SavedView } from "../../types/views";
import type { ModelColumn, ModelTable } from "../../types/api";
import type {
  FieldError,
  RecordCacheByTable,
  TableViewState,
  UndoEntry
} from "./types";

const UNDO_STACK_LIMIT = 10;

// ── View-config helpers ───────────────────────────────────────────────

export function cloneExplorerViewConfig(config: ExplorerViewConfig): ExplorerViewConfig {
  return patchExplorerViewConfig(config, {});
}

/**
 * Rebuild a config to match a target type. When saved.type and config.type
 * disagree (e.g. a view was saved while the in-memory config happened to
 * belong to a different type), this forces the config shape to the correct
 * type, preserving the shared base fields.
 */
export function rebuildConfigForType(
  config: ExplorerViewConfig,
  type: ExplorerViewType
): ExplorerViewConfig {
  const base = {
    search: config.search,
    sort: config.sort.map((s) => ({ ...s })),
    filters: config.filters.map((f) => ({ ...f })),
    hiddenColumnNames: [...config.hiddenColumnNames],
    columnOrder: [...config.columnOrder]
  };
  if (type === "kanban") {
    return {
      ...base,
      type: "kanban",
      kanbanColumn: config.type === "kanban" ? config.kanbanColumn : null,
      cardFieldNames: config.type === "kanban" ? [...config.cardFieldNames] : [],
      hiddenLaneValues: config.type === "kanban" ? [...(config.hiddenLaneValues ?? [])] : []
    };
  }
  return { ...base, type: "table" };
}

export function extractExplorerViewConfig(view: TableViewState): ExplorerViewConfig {
  return cloneExplorerViewConfig(view.config);
}

export function hydrateTableViewFromConfig(
  config: ExplorerViewConfig
): TableViewState {
  return {
    status: "idle",
    config: cloneExplorerViewConfig(config),
    page: DEFAULT_QUERY.page,
    perPage: DEFAULT_QUERY.perPage,
    rows: [],
    meta: null,
    error: null,
    source: { kind: "table" },
    selectedRowIds: [],
    editingCell: null,
    cellErrors: {},
    pendingCellSaves: [],
    pendingNewRowIds: [],
    rowErrors: {},
    context: null,
    savedViews: [],
    savedViewsStatus: "idle",
    activeViewId: null,
    autoSave: false
  };
}

export function freshTableView(): TableViewState {
  return hydrateTableViewFromConfig(DEFAULT_EXPLORER_VIEW_CONFIG);
}

// ── Cell-error helpers (used by the inline-edit save round-trip) ──────────

export function clearCellError(
  errors: Record<string, Record<string, string>>,
  rowId: string,
  columnName: string,
): Record<string, Record<string, string>> {
  const rowErrors = errors[rowId];
  if (!rowErrors || rowErrors[columnName] === undefined) return errors;
  const { [columnName]: _removed, ...rest } = rowErrors;
  const nextErrors = { ...errors };
  if (Object.keys(rest).length === 0) {
    delete nextErrors[rowId];
  } else {
    nextErrors[rowId] = rest;
  }
  return nextErrors;
}

export function setCellErrorIn(
  errors: Record<string, Record<string, string>>,
  rowId: string,
  columnName: string,
  message: string,
): Record<string, Record<string, string>> {
  const rowErrors = { ...(errors[rowId] ?? {}) };
  rowErrors[columnName] = message;
  return { ...errors, [rowId]: rowErrors };
}

export function extractFieldErrors(err: unknown): FieldError[] | null {
  if (!(err instanceof ApiClientError)) return null;
  if (err.status < 400 || err.status >= 500) return null;
  const details = err.details as { errors?: unknown } | undefined;
  const list = details?.errors;
  if (!Array.isArray(list)) return null;
  const out: FieldError[] = [];
  for (const item of list) {
    if (item && typeof item === "object") {
      const field = (item as { field?: unknown }).field;
      const message = (item as { message?: unknown }).message;
      if (typeof field === "string" && typeof message === "string") {
        out.push({ field, message });
      }
    }
  }
  return out.length > 0 ? out : null;
}

export function getExplorerQueryForView(view: TableViewState): ExplorerQuery {
  return buildExplorerQuery(view.config, {
    page: view.page,
    perPage: view.perPage
  });
}

// ── Ownership / schema lookups ────────────────────────────────────────

/**
 * Returns true if the current authenticated user owns the given view.
 * Used to gate destructive / mutating actions so users can't rename or
 * delete someone else's shared view.
 */
export function currentUserOwns(view: SavedView | undefined): boolean {
  if (!view) return false;
  const user = useAuthStore.getState().user;
  if (!user) return false;
  return view.owner_user_id === user.id;
}

export function getTableModel(tableName: string): ModelTable | null {
  return (
    useSchemaStore.getState().snapshot?.tables.find((table) => table.name === tableName) ??
    null
  );
}

// ── Column / row helpers ──────────────────────────────────────────────

export function isWritableColumn(column: ModelColumn): boolean {
  if (column.type === "computed") return false;
  if (column.references || column.type === "relation") return true;
  return !column.primaryKey;
}

export function getPrimaryKeyColumns(table: ModelTable): ModelColumn[] {
  return table.columns.filter((column) => Boolean(column.primaryKey));
}

export function buildCompositeRowId(table: ModelTable, row: Record<string, unknown>): string | null {
  const primaryKeyColumns = getPrimaryKeyColumns(table);
  if (primaryKeyColumns.length <= 1) return null;

  const parts: string[] = [];
  for (const column of primaryKeyColumns) {
    const value = row[column.name];
    if (value === undefined || value === null || value === "") {
      return null;
    }
    parts.push(`${column.name}:${String(value)}`);
  }

  return parts.join("|");
}

export function normalizeRowId(
  table: ModelTable,
  row: Record<string, unknown>,
  fallbackRowId?: string,
): Record<string, unknown> {
  if (row.id !== undefined && row.id !== null && row.id !== "") {
    return row;
  }

  const compositeRowId = buildCompositeRowId(table, row);
  if (compositeRowId) {
    return { ...row, id: compositeRowId };
  }

  if (fallbackRowId) {
    return { ...row, id: fallbackRowId };
  }

  return row;
}

function isTemporalColumn(column: ModelColumn): boolean {
  return column.type === "date" || column.type === "datetime";
}

function isServerManagedColumn(column: ModelColumn): boolean {
  return (
    (Boolean(column.primaryKey) && !column.references && column.type !== "relation") ||
    (isTemporalColumn(column) && column.default !== undefined)
  );
}

export function getWritableColumns(table: ModelTable): ModelColumn[] {
  return table.columns.filter(isWritableColumn);
}

export function buildLocalBlankRow(table: ModelTable, localRowId: string): Record<string, unknown> {
  const row: Record<string, unknown> = { id: localRowId };
  for (const column of table.columns) {
    if (column.name === "id") continue;
    row[column.name] = null;
  }
  return row;
}

export function buildCreatePayload(
  table: ModelTable,
  row: Record<string, unknown>,
  nextColumnName: string,
  nextValue: unknown,
): Record<string, unknown> {
  const payload: Record<string, unknown> = {};
  for (const column of getWritableColumns(table)) {
    const value = column.name === nextColumnName ? nextValue : row[column.name];
    if (value === undefined || value === null || value === "") continue;
    payload[column.name] = value;
  }
  return payload;
}

export function isCreateReady(
  table: ModelTable,
  row: Record<string, unknown>,
  nextColumnName: string,
  nextValue: unknown,
): boolean {
  for (const column of getWritableColumns(table)) {
    if (!column.primaryKey && column.nullable !== false) continue;
    if (column.default !== undefined) continue;

    const value = column.name === nextColumnName ? nextValue : row[column.name];
    if (value === undefined || value === null || value === "") {
      return false;
    }
  }

  return true;
}

export function buildDuplicatePayload(
  table: ModelTable,
  row: Record<string, unknown>,
): Record<string, unknown> {
  const payload: Record<string, unknown> = {};
  for (const column of getWritableColumns(table)) {
    if (isServerManagedColumn(column)) continue;
    if (column.name === "id") continue;
    const value = row[column.name];
    if (value === undefined) continue;
    payload[column.name] = value;
  }
  return payload;
}

export function adjustMetaTotal(meta: ListMeta | null, perPage: number, delta: number): ListMeta | null {
  if (!meta) return meta;
  const total = Math.max(0, meta.total + delta);
  return {
    ...meta,
    total,
    total_pages: Math.max(1, Math.ceil(total / Math.max(1, perPage))),
  };
}

export async function runInConcurrentBatches<T, R>(
  items: T[],
  concurrency: number,
  worker: (item: T) => Promise<R>,
): Promise<R[]> {
  const results: R[] = [];
  for (let index = 0; index < items.length; index += concurrency) {
    const batch = items.slice(index, index + concurrency);
    results.push(...(await Promise.all(batch.map((item) => worker(item)))));
  }
  return results;
}

export function trimUndoStack(stack: UndoEntry[]): UndoEntry[] {
  return stack.length > UNDO_STACK_LIMIT
    ? stack.slice(stack.length - UNDO_STACK_LIMIT)
    : stack;
}

export function dropPendingNewRows(
  view: TableViewState,
  options?: { preserveRowIds?: string[] },
): TableViewState {
  if (view.pendingNewRowIds.length === 0) return view;

  const preserve = new Set(options?.preserveRowIds ?? []);
  const toDrop = view.pendingNewRowIds.filter((rowId) => !preserve.has(rowId));
  if (toDrop.length === 0) return view;

  const dropSet = new Set(toDrop);
  const nextCellErrors = { ...view.cellErrors };
  const nextRowErrors = { ...view.rowErrors };
  for (const rowId of toDrop) {
    delete nextCellErrors[rowId];
    delete nextRowErrors[rowId];
  }

  return {
    ...view,
    rows: view.rows.filter((row) => !dropSet.has(String(row.id ?? ""))),
    meta: adjustMetaTotal(view.meta, view.perPage, -toDrop.length),
    pendingNewRowIds: view.pendingNewRowIds.filter((rowId) => preserve.has(rowId)),
    pendingCellSaves: view.pendingCellSaves.filter((key) => !dropSet.has(key.split(":")[0] ?? "")),
    selectedRowIds: view.selectedRowIds.filter((rowId) => !dropSet.has(rowId)),
    editingCell:
      view.editingCell && dropSet.has(view.editingCell.rowId) ? null : view.editingCell,
    cellErrors: nextCellErrors,
    rowErrors: nextRowErrors,
  };
}

// ── Record-cache helpers ──────────────────────────────────────────────

export function upsertRecordCacheEntry(
  cacheByTable: RecordCacheByTable,
  table: string,
  rowId: string,
  row: Record<string, unknown>,
): RecordCacheByTable {
  return {
    ...cacheByTable,
    [table]: {
      ...(cacheByTable[table] ?? {}),
      [rowId]: { ...row },
    },
  };
}

export function removeRecordCacheEntry(
  cacheByTable: RecordCacheByTable,
  table: string,
  rowId: string,
): RecordCacheByTable {
  const tableCache = cacheByTable[table];
  if (!tableCache || tableCache[rowId] === undefined) return cacheByTable;

  const nextTableCache = { ...tableCache };
  delete nextTableCache[rowId];

  if (Object.keys(nextTableCache).length === 0) {
    const nextCache = { ...cacheByTable };
    delete nextCache[table];
    return nextCache;
  }

  return {
    ...cacheByTable,
    [table]: nextTableCache,
  };
}
