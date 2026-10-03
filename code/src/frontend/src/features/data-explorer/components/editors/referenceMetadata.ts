import type { ModelColumn, ModelSnapshot, ModelTable } from "../../../../types/api";

/**
 * Resolve the referenced table name for an FK column. The Model JSON contract
 * accepts either a string ("table_name") or an object ({ table, column }), so
 * we accept both shapes at runtime.
 */
export function getReferenceTableName(column: ModelColumn): string | null {
  const refs = column.references;
  if (!refs) return null;
  if (typeof refs === "string") return refs;
  if (typeof refs === "object" && refs !== null) {
    const table = (refs as { table?: unknown }).table;
    return typeof table === "string" ? table : null;
  }
  return null;
}

/** Resolve the referenced primary-key column name. Defaults to "id". */
export function getReferencePrimaryKey(column: ModelColumn): string {
  const refs = column.references;
  if (refs && typeof refs === "object") {
    const col = (refs as { column?: unknown }).column;
    if (typeof col === "string" && col.length > 0) return col;
  }
  return "id";
}

/**
 * The column that names a record of `table`: the table's record label when
 * the schema sets one, otherwise the first non-key text column, then any
 * non-key column, then the first column.
 */
export function getDisplayColumnForTable(table: ModelTable | null): string {
  if (!table || table.columns.length === 0) return "id";
  const configured = table.recordLabel?.column;
  if (configured && table.columns.some((c) => c.name === configured)) return configured;
  const textCol = table.columns.find(
    (c) => (c.type === "text" || c.type === "string") && !c.primaryKey,
  );
  if (textCol) return textCol.name;
  const nonPk = table.columns.find((c) => !c.primaryKey);
  if (nonPk) return nonPk.name;
  return table.columns[0]!.name;
}

/** The record label's secondary column, when set and present. */
export function getSecondaryColumnForTable(table: ModelTable | null): string | null {
  const secondary = table?.recordLabel?.secondary;
  if (!table || !secondary) return null;
  if (secondary === getDisplayColumnForTable(table)) return null;
  return table.columns.some((c) => c.name === secondary) ? secondary : null;
}

export function findTable(snapshot: ModelSnapshot | null, name: string | null): ModelTable | null {
  if (!snapshot || !name) return null;
  return snapshot.tables.find((t) => t.name === name) ?? null;
}
