import type {
  ModelColumn,
  ModelRelation,
  ModelSnapshot,
  ModelTable
} from "../../types/api";

export type RelatedDrillIn = {
  accessor: string;
  targetTable: string;
  label: string;
  description: string;
  mode: "filter" | "endpoint";
  filterColumn?: string;
  valueColumn?: string;
};

export type IncomingRelationSection = {
  targetTable: string;
  label: string;
  description: string;
  filterColumn: string;
  filterColumnLabel: string;
  relationType?: "1:N" | "N:M";
  hiddenColumn?: string;
  relatedColumn?: string;
};

export function deriveReferenceAccessor(column: ModelColumn): string {
  if (column.relationName?.trim()) {
    return column.relationName.trim();
  }

  return deriveReferenceAccessorName(
    column.name,
    getReferenceTableNameFromColumn(column) ?? column.name
  );
}

export function getReferencedTableName(
  snapshot: ModelSnapshot,
  currentTable: ModelTable,
  column: ModelColumn
): string | null {
  const referencedTable = getReferenceTableNameFromColumn(column);
  if (!referencedTable) return null;

  const accessor = deriveReferenceAccessor(column);
  const matchingRelation = (snapshot.relations ?? []).find((relation) => {
    const fromTable = getRelationFromTable(relation);
    const toTable = getRelationToTable(relation);

    return (
      !!fromTable &&
      !!toTable &&
      equalsIgnoreCase(toTable, currentTable.name) &&
      equalsIgnoreCase(fromTable, referencedTable) &&
      equalsIgnoreCase(getResolvedToAccessor(relation) ?? "", accessor)
    );
  });

  if (matchingRelation) {
    return getRelationFromTable(matchingRelation);
  }

  return referencedTable;
}

export function getRelatedDrillIns(
  snapshot: ModelSnapshot,
  currentTable: ModelTable
): RelatedDrillIn[] {
  const related = new Map<string, RelatedDrillIn>();
  const tablesByName = new Map(
    snapshot.tables.map((table) => [table.name.toLowerCase(), table] as const)
  );

  for (const relation of snapshot.relations ?? []) {
    const fromTable = getRelationFromTable(relation);
    const toTable = getRelationToTable(relation);
    if (!fromTable || !toTable) continue;

    if (equalsIgnoreCase(fromTable, currentTable.name)) {
      addRelatedDrillIn(related, tablesByName, relation, {
        accessor: getResolvedFromAccessor(relation),
        targetTable: toTable,
        currentTable,
        isFromSide: true
      });
    }

    if (equalsIgnoreCase(toTable, currentTable.name)) {
      addRelatedDrillIn(related, tablesByName, relation, {
        accessor: getResolvedToAccessor(relation),
        targetTable: fromTable,
        currentTable,
        isFromSide: false
      });
    }
  }

  // Compatibility fallback: older/current schemas may only declare direct
  // references on columns without a matching relations[] entry.
  for (const table of snapshot.tables) {
    for (const column of table.columns) {
      const referencedTable = getReferenceTableNameFromColumn(column);
      if (!referencedTable || !equalsIgnoreCase(referencedTable, currentTable.name)) {
        continue;
      }

      const accessor = deriveReferenceAccessor(column);
      addFallbackDrillIn(related, table, accessor, column);
    }
  }

  return Array.from(related.values());
}

export function getIncomingRelationSections(
  snapshot: ModelSnapshot,
  currentTable: ModelTable
): IncomingRelationSection[] {
  const sections = new Map<string, IncomingRelationSection>();
  const tablesByName = new Map(
    snapshot.tables.map((table) => [table.name.toLowerCase(), table] as const)
  );

  for (const relation of snapshot.relations ?? []) {
    const fromTable = getRelationFromTable(relation);
    const toTable = getRelationToTable(relation);
    if (!fromTable || !toTable) continue;

    if (relation.type === "N:M") {
      const junctionTableName =
        typeof relation.junctionTable === "string" && relation.junctionTable.trim()
          ? relation.junctionTable.trim()
          : typeof relation.resolvedJunctionTableName === "string" &&
              relation.resolvedJunctionTableName.trim()
            ? relation.resolvedJunctionTableName.trim()
            : null;
      const junctionTable = junctionTableName
        ? tablesByName.get(junctionTableName.toLowerCase())
        : null;
      if (!junctionTable) continue;

      if (equalsIgnoreCase(fromTable, currentTable.name)) {
        const filterColumn =
          typeof relation.fromColumn === "string" && relation.fromColumn.trim()
            ? relation.fromColumn.trim()
            : null;
        const relatedColumn =
          typeof relation.toColumn === "string" && relation.toColumn.trim()
            ? relation.toColumn.trim()
            : null;
        if (filterColumn) {
          const targetTable = tablesByName.get(toTable.toLowerCase());
          addIncomingRelationSection(
            sections,
            junctionTable,
            filterColumn,
            junctionTable.columns.find((column) => column.name === filterColumn)?.displayName ??
              filterColumn,
            {
              relationType: "N:M",
              label:
                getResolvedFromAccessor(relation) ??
                targetTable?.displayName ??
                targetTable?.name ??
                toTable,
              hiddenColumn: filterColumn,
              relatedColumn: relatedColumn ?? undefined,
            },
          );
        }
      }

      if (equalsIgnoreCase(toTable, currentTable.name)) {
        const filterColumn =
          typeof relation.toColumn === "string" && relation.toColumn.trim()
            ? relation.toColumn.trim()
            : null;
        const relatedColumn =
          typeof relation.fromColumn === "string" && relation.fromColumn.trim()
            ? relation.fromColumn.trim()
            : null;
        if (filterColumn) {
          const targetTable = tablesByName.get(fromTable.toLowerCase());
          addIncomingRelationSection(
            sections,
            junctionTable,
            filterColumn,
            junctionTable.columns.find((column) => column.name === filterColumn)?.displayName ??
              filterColumn,
            {
              relationType: "N:M",
              label:
                getResolvedToAccessor(relation) ??
                targetTable?.displayName ??
                targetTable?.name ??
                fromTable,
              hiddenColumn: filterColumn,
              relatedColumn: relatedColumn ?? undefined,
            },
          );
        }
      }

      continue;
    }

    if (!equalsIgnoreCase(fromTable, currentTable.name)) continue;
    if (!isOneToManyRelation(relation)) continue;

    const relationColumn =
      typeof relation.column === "string" && relation.column.trim().length > 0
        ? relation.column.trim()
        : null;
    if (!relationColumn) continue;

    const targetTable = tablesByName.get(toTable.toLowerCase());
    if (!targetTable) continue;

    addIncomingRelationSection(sections, targetTable, relationColumn);
  }

  for (const table of snapshot.tables) {
    if (equalsIgnoreCase(table.name, currentTable.name)) continue;

    for (const column of table.columns) {
      const referencedTable = getReferenceTableNameFromColumn(column);
      if (!referencedTable || !equalsIgnoreCase(referencedTable, currentTable.name)) {
        continue;
      }

      if (
        Array.from(sections.values()).some(
          (section) =>
            equalsIgnoreCase(section.targetTable, table.name) &&
            equalsIgnoreCase(section.filterColumn, column.name),
        )
      ) {
        continue;
      }

      addIncomingRelationSection(sections, table, column.name, column.displayName ?? column.name);
    }
  }

  return finalizeIncomingRelationLabels(Array.from(sections.values()));
}

function addRelatedDrillIn(
  related: Map<string, RelatedDrillIn>,
  tablesByName: Map<string, ModelTable>,
  relation: ModelRelation,
  options: {
    accessor: string | null | undefined;
    targetTable: string;
    currentTable: ModelTable;
    isFromSide: boolean;
  }
): void {
  const accessor = options.accessor?.trim();
  if (!accessor || related.has(accessor)) return;

  const targetTable = tablesByName.get(options.targetTable.toLowerCase());
  const label = targetTable?.displayName ?? options.targetTable;
  const filterConfig = resolveFilterConfig(
    relation,
    options.currentTable,
    targetTable,
    options.isFromSide
  );

  related.set(accessor, {
    accessor,
    targetTable: options.targetTable,
    label,
    description: `Related ${label} via ${accessor}`,
    mode: filterConfig ? "filter" : "endpoint",
    filterColumn: filterConfig?.filterColumn,
    valueColumn: filterConfig?.valueColumn
  });
}

function addFallbackDrillIn(
  related: Map<string, RelatedDrillIn>,
  targetTable: ModelTable,
  accessor: string,
  column: ModelColumn
): void {
  const normalizedAccessor = accessor.trim();
  if (!normalizedAccessor || related.has(normalizedAccessor)) return;
  if (
    Array.from(related.values()).some((drillIn) =>
      equalsIgnoreCase(drillIn.targetTable, targetTable.name)
    )
  ) {
    return;
  }

  const label = targetTable.displayName ?? targetTable.name;
  related.set(normalizedAccessor, {
    accessor: normalizedAccessor,
    targetTable: targetTable.name,
    label,
    description: `Related ${label} via ${column.displayName ?? column.name}`,
    mode: "filter",
    filterColumn: column.name
  });
}

function addIncomingRelationSection(
  sections: Map<string, IncomingRelationSection>,
  targetTable: ModelTable,
  filterColumn: string,
  filterColumnLabel?: string,
  options?: {
    relationType?: "1:N" | "N:M";
    label?: string;
    hiddenColumn?: string;
    relatedColumn?: string;
  }
): void {
  const key = `${targetTable.name.toLowerCase()}:${filterColumn.toLowerCase()}:${options?.relationType ?? "1:N"}`;
  if (sections.has(key)) return;

  const label = options?.label ?? targetTable.displayName ?? targetTable.name;
  sections.set(key, {
    targetTable: targetTable.name,
    label,
    description: `Related ${label} where ${filterColumn} points at this record`,
    filterColumn,
    filterColumnLabel: filterColumnLabel ?? filterColumn,
    relationType: options?.relationType ?? "1:N",
    hiddenColumn: options?.hiddenColumn,
    relatedColumn: options?.relatedColumn
  });
}

function finalizeIncomingRelationLabels(
  sections: IncomingRelationSection[]
): IncomingRelationSection[] {
  const duplicateCounts = new Map<string, number>();
  for (const section of sections) {
    const key = section.targetTable.toLowerCase();
    duplicateCounts.set(key, (duplicateCounts.get(key) ?? 0) + 1);
  }

  return sections
    .map((section) => ({
      ...section,
      label:
        (duplicateCounts.get(section.targetTable.toLowerCase()) ?? 0) > 1
          ? `${section.label} via ${section.filterColumnLabel}`
          : section.label
    }))
    .sort((left, right) => left.label.localeCompare(right.label));
}

function resolveFilterConfig(
  relation: ModelRelation,
  currentTable: ModelTable,
  targetTable: ModelTable | undefined,
  isFromSide: boolean
): { filterColumn: string; valueColumn?: string } | null {
  if (!isOneToManyRelation(relation)) return null;

  const relationColumn =
    typeof relation.column === "string" && relation.column.trim().length > 0
      ? relation.column.trim()
      : null;
  if (!relationColumn) return null;

  if (isFromSide) {
    return { filterColumn: relationColumn };
  }

  const primaryKeyColumn = targetTable?.columns.find((column) => column.primaryKey)?.name ?? "id";
  const currentColumnExists = currentTable.columns.some(
    (column) => column.name === relationColumn
  );
  if (!currentColumnExists) return null;

  return {
    filterColumn: primaryKeyColumn,
    valueColumn: relationColumn
  };
}

function getRelationFromTable(relation: ModelRelation): string | null {
  return typeof relation.from === "string"
    ? relation.from
    : typeof relation.fromTable === "string"
      ? relation.fromTable
      : null;
}

function getRelationToTable(relation: ModelRelation): string | null {
  return typeof relation.to === "string"
    ? relation.to
    : typeof relation.toTable === "string"
      ? relation.toTable
      : null;
}

function getResolvedFromAccessor(relation: ModelRelation): string | null {
  return typeof relation.resolvedFromAccessor === "string"
    ? relation.resolvedFromAccessor
    : typeof relation.fromAccessor === "string"
      ? relation.fromAccessor
      : null;
}

function getResolvedToAccessor(relation: ModelRelation): string | null {
  return typeof relation.resolvedToAccessor === "string"
    ? relation.resolvedToAccessor
    : typeof relation.toAccessor === "string"
      ? relation.toAccessor
      : null;
}

function isOneToManyRelation(relation: ModelRelation): boolean {
  return relation.type !== "N:M";
}

function getReferenceTableNameFromColumn(column: ModelColumn): string | null {
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
