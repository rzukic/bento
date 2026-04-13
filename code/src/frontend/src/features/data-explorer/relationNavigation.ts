import type { ModelColumn, ModelSnapshot, ModelTable } from "../../types/api";

export type RelatedDrillIn = {
  accessor: string;
  targetTable: string;
  filterColumn: string;
  label: string;
  description: string;
};

export function getReferencedTableName(column: ModelColumn): string | null {
  if (!column.references) return null;

  if (typeof column.references === "string") {
    return column.references;
  }

  if (typeof column.references === "object" && column.references !== null) {
    const table = (column.references as { table?: unknown }).table;
    return typeof table === "string" ? table : null;
  }

  return null;
}

export function deriveReferenceAccessor(column: ModelColumn): string {
  if (column.relationName?.trim()) {
    return column.relationName.trim();
  }

  return deriveReferenceAccessorName(column.name, getReferencedTableName(column) ?? column.name);
}

export function getRelatedDrillIns(snapshot: ModelSnapshot, currentTable: ModelTable): RelatedDrillIn[] {
  const related = new Map<string, RelatedDrillIn>();

  for (const table of snapshot.tables) {
    for (const column of table.columns) {
      const referencedTable = getReferencedTableName(column);
      if (!referencedTable || !equalsIgnoreCase(referencedTable, currentTable.name)) {
        continue;
      }

      const accessor =
        column.relationName?.trim() ||
        `${deriveReferenceAccessorName(column.name, currentTable.name)}_${table.name}`;

      if (related.has(accessor)) continue;

      related.set(accessor, {
        accessor,
        targetTable: table.name,
        filterColumn: column.name,
        label: table.displayName ?? table.name,
        description: `Related ${table.displayName ?? table.name} via ${column.displayName ?? column.name}`
      });
    }
  }

  return Array.from(related.values());
}

function deriveReferenceAccessorName(columnName: string, fallbackTableName: string): string {
  if (columnName.endsWith("_id") && columnName.length > 3) {
    return columnName.slice(0, -3);
  }

  return singularizeName(fallbackTableName);
}

function singularizeName(name: string): string {
  if (name.endsWith("ies") && name.length > 3) {
    return `${name.slice(0, -3)}y`;
  }

  if (name.endsWith("ses") && name.length > 3) {
    return name.slice(0, -2);
  }

  if (name.endsWith("s") && !name.endsWith("ss") && name.length > 1) {
    return name.slice(0, -1);
  }

  return name;
}

function equalsIgnoreCase(left: string, right: string): boolean {
  return left.localeCompare(right, undefined, { sensitivity: "accent" }) === 0;
}
