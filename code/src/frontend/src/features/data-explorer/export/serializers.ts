import type { ModelColumn, ModelTable } from "../../../types/api";

export type ExportFormat = "csv" | "json";
export type ReferenceDisplayMap = Record<string, Record<string, string>>;

export function getOrderedVisibleColumns(
  table: ModelTable,
  hiddenColumnNames: string[],
  columnOrder: string[],
): ModelColumn[] {
  const hidden = new Set(hiddenColumnNames);
  const orderedNames = new Set(columnOrder);
  const visibleColumns = table.columns.filter(
    (column) => column.primaryKey || !hidden.has(column.name),
  );
  const primaryKeyColumns = visibleColumns.filter((column) => column.primaryKey);
  const orderedColumns = columnOrder
    .map((columnName) =>
      visibleColumns.find(
        (column) => !column.primaryKey && column.name === columnName,
      ),
    )
    .filter((column): column is ModelColumn => column !== undefined);
  const remainingColumns = visibleColumns.filter(
    (column) => !column.primaryKey && !orderedNames.has(column.name),
  );
  return [...primaryKeyColumns, ...orderedColumns, ...remainingColumns];
}

export function rowsToCsv(
  rows: Record<string, unknown>[],
  columns: ModelColumn[],
  referenceDisplays: ReferenceDisplayMap = {},
): string {
  const header = columns
    .map((column) => escapeCsvField(column.displayName ?? column.name))
    .join(",");
  const lines = rows.map((row) =>
    columns
      .map((column) =>
        escapeCsvField(
          formatCsvValue(
            column,
            row[column.name],
            referenceDisplays[column.name] ?? {},
          ),
        ),
      )
      .join(","),
  );
  return [header, ...lines].join("\r\n");
}

export function rowsToJson(
  rows: Record<string, unknown>[],
  columns: ModelColumn[],
  referenceDisplays: ReferenceDisplayMap = {},
): string {
  const payload = rows.map((row) => {
    const record: Record<string, unknown> = {};
    for (const column of columns) {
      const value = row[column.name];
      if (isReferenceColumn(column)) {
        record[column.name] = value ?? null;
        record[`${column.name}_display`] =
          value === null || value === undefined || value === ""
            ? null
            : referenceDisplays[column.name]?.[String(value)] ?? String(value);
        continue;
      }
      record[column.name] = formatJsonValue(column, value);
    }
    return record;
  });

  return JSON.stringify(payload, null, 2);
}

export function buildExportFilename(
  tableName: string,
  viewName: string | null | undefined,
  format: ExportFormat,
  date = new Date(),
): string {
  const yyyy = date.getFullYear();
  const mm = String(date.getMonth() + 1).padStart(2, "0");
  const dd = String(date.getDate()).padStart(2, "0");
  return `${sanitizeFilenameSegment(tableName)}-${sanitizeFilenameSegment(viewName ?? "AllRecords")}-${yyyy}-${mm}-${dd}.${format}`;
}

function formatCsvValue(
  column: ModelColumn,
  value: unknown,
  referenceDisplayById: Record<string, string>,
): string {
  if (value === null || value === undefined || value === "") {
    return "";
  }

  if (isReferenceColumn(column)) {
    return referenceDisplayById[String(value)] ?? String(value);
  }

  if (column.type === "boolean") {
    const booleanValue = normalizeBooleanValue(value);
    if (booleanValue !== null) {
      return booleanValue ? "TRUE" : "FALSE";
    }
  }

  if (column.type === "json" || typeof value === "object") {
    return safeJsonStringify(value);
  }

  if (typeof value === "boolean") {
    return value ? "true" : "false";
  }

  return String(value);
}

function formatJsonValue(column: ModelColumn, value: unknown): unknown {
  if (value === null || value === undefined) {
    return null;
  }

  if (column.type === "boolean") {
    return normalizeBooleanValue(value) ?? value;
  }

  return value;
}

function safeJsonStringify(value: unknown): string {
  try {
    const serialized = JSON.stringify(value);
    return serialized ?? "";
  } catch {
    return String(value);
  }
}

function escapeCsvField(value: string): string {
  const normalized = value.replace(/\r\n/g, "\n");
  if (!/[",\n\r]/.test(normalized)) {
    return normalized;
  }
  return `"${normalized.replace(/"/g, "\"\"")}"`;
}

function isReferenceColumn(column: ModelColumn): boolean {
  return column.type === "relation" || !!column.references;
}

function normalizeBooleanValue(value: unknown): boolean | null {
  if (typeof value === "boolean") return value;
  if (typeof value === "number") {
    if (value === 1) return true;
    if (value === 0) return false;
    return null;
  }

  if (typeof value === "string") {
    const normalized = value.trim().toLowerCase();
    if (normalized === "true" || normalized === "1") return true;
    if (normalized === "false" || normalized === "0") return false;
  }

  return null;
}

function sanitizeFilenameSegment(value: string): string {
  const trimmed = value.trim();
  if (!trimmed) return "export";
  return trimmed
    .replace(/[<>:"/\\|?*\u0000-\u001f]/g, "")
    .replace(/\s+/g, "-");
}
