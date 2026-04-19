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
 * Pick a sensible display column for a referenced table. We prefer the first
 * non-PK string column (the most common label), then any non-PK column, then
 * fall back to the first column. This is a heuristic — Phase 2 schemas don't
 * yet declare a dedicated `displayColumn` field.
 */
export function getDisplayColumnForTable(table: ModelTable | null): string {
  if (!table || table.columns.length === 0) return "id";
  const stringCol = table.columns.find(
    (c) => c.type === "string" && !c.primaryKey,
  );
  if (stringCol) return stringCol.name;
  const nonPk = table.columns.find((c) => !c.primaryKey);
  if (nonPk) return nonPk.name;
  return table.columns[0]!.name;
}

export function findTable(snapshot: ModelSnapshot | null, name: string | null): ModelTable | null {
  if (!snapshot || !name) return null;
  return snapshot.tables.find((t) => t.name === name) ?? null;
}
