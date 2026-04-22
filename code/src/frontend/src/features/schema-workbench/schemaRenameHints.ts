import type {
  SchemaColumn,
  SchemaIndex,
  SchemaModelDocument,
  SchemaRelation,
  SchemaTable,
} from "../../types/api";

type NamedSchemaItem = {
  name?: string | null;
  renamedFrom?: string | null;
};

type RenameHintResult<T> = {
  item: T;
  changed: boolean;
};

export function addAutomaticRenameHints(
  draft: SchemaModelDocument,
  baseline: SchemaModelDocument | null,
): SchemaModelDocument {
  if (!baseline) {
    return draft;
  }

  const baselineTablesByName = buildNameMap(baseline.tables);
  const draftTableNames = buildNameSet(draft.tables);
  const tableRenameMap = new Map<string, string>();
  let changed = false;

  const tables = draft.tables.map((table, index) => {
    const baselineTable = resolveBaselineTable(
      baseline.tables,
      baselineTablesByName,
      table,
      index,
    );
    const tableResult = addRenameHint(table, baselineTable, draftTableNames);
    let nextTable = tableResult.item;
    let tableChanged = tableResult.changed;

    const columnsResult = addCollectionRenameHints(
      nextTable.columns,
      baselineTable?.columns ?? [],
    );
    if (columnsResult.changed) {
      nextTable = {
        ...nextTable,
        columns: columnsResult.items,
      };
      tableChanged = true;
    }

    const indexesResult = addCollectionRenameHints(
      nextTable.indexes ?? [],
      baselineTable?.indexes ?? [],
    );
    if (indexesResult.changed) {
      nextTable = {
        ...nextTable,
        indexes: indexesResult.items,
      };
      tableChanged = true;
    }

    if (tableChanged) {
      changed = true;
    }

    const tableRename = resolveTableRename(
      nextTable,
      draftTableNames,
      baselineTablesByName,
    );
    if (tableRename) {
      tableRenameMap.set(normalizeName(tableRename.from), tableRename.to);
    }

    return nextTable;
  });

  const cascaded = cascadeTableRenameReferences(
    tables,
    draft.relations,
    tableRenameMap,
  );

  if (cascaded.changed) {
    changed = true;
  }

  return changed
    ? {
        ...draft,
        tables: cascaded.tables,
        relations: cascaded.relations,
      }
    : draft;
}

function resolveBaselineTable(
  baselineTables: SchemaTable[],
  baselineTablesByName: Map<string, SchemaTable>,
  draftTable: SchemaTable,
  index: number,
): SchemaTable | undefined {
  if (typeof draftTable.renamedFrom === "string") {
    return (
      baselineTablesByName.get(normalizeName(draftTable.renamedFrom)) ??
      baselineTables[index]
    );
  }

  return baselineTables[index];
}

function resolveTableRename(
  table: SchemaTable,
  draftTableNames: Set<string>,
  baselineTablesByName: Map<string, SchemaTable>,
): { from: string; to: string } | null {
  const tableName = getName(table);
  const previousName =
    typeof table.renamedFrom === "string" && table.renamedFrom.trim() !== ""
      ? table.renamedFrom
      : null;

  if (
    !tableName ||
    !previousName ||
    namesEqual(tableName, previousName) ||
    draftTableNames.has(normalizeName(previousName)) ||
    !baselineTablesByName.has(normalizeName(previousName))
  ) {
    return null;
  }

  return {
    from: previousName,
    to: tableName,
  };
}

function addCollectionRenameHints<T extends SchemaColumn | SchemaIndex>(
  draftItems: T[],
  baselineItems: T[],
): { items: T[]; changed: boolean } {
  const draftNames = buildNameSet(draftItems);
  let changed = false;

  const items = draftItems.map((item, index) => {
    const result = addRenameHint(item, baselineItems[index], draftNames);
    if (result.changed) {
      changed = true;
    }

    return result.item;
  });

  return changed ? { items, changed } : { items: draftItems, changed: false };
}

function addRenameHint<T extends NamedSchemaItem>(
  draftItem: T,
  baselineItem: T | undefined,
  draftNames: Set<string>,
): RenameHintResult<T> {
  const draftName = getName(draftItem);
  const baselineName = getName(baselineItem);

  if (
    !draftName ||
    !baselineName ||
    namesEqual(draftName, baselineName) ||
    hasOwnRenamedFrom(draftItem) ||
    draftNames.has(normalizeName(baselineName))
  ) {
    return { item: draftItem, changed: false };
  }

  return {
    item: {
      ...draftItem,
      renamedFrom: baselineName,
    },
    changed: true,
  };
}

function cascadeTableRenameReferences(
  tables: SchemaTable[],
  relations: SchemaRelation[],
  tableRenameMap: Map<string, string>,
): { tables: SchemaTable[]; relations: SchemaRelation[]; changed: boolean } {
  if (tableRenameMap.size === 0) {
    return { tables, relations, changed: false };
  }

  let changed = false;
  const nextTables = tables.map((table) => {
    let columnsChanged = false;
    const columns = table.columns.map((column) => {
      const references = rewriteTableName(column.references, tableRenameMap);
      if (references === column.references) {
        return column;
      }

      columnsChanged = true;
      return {
        ...column,
        references,
      };
    });

    if (!columnsChanged) {
      return table;
    }

    changed = true;
    return {
      ...table,
      columns,
    };
  });

  const nextRelations = relations.map((relation) => {
    const from = rewriteTableName(relation.from, tableRenameMap);
    const to = rewriteTableName(relation.to, tableRenameMap);

    if (from === relation.from && to === relation.to) {
      return relation;
    }

    changed = true;
    return {
      ...relation,
      from,
      to,
    };
  });

  return {
    tables: changed ? nextTables : tables,
    relations: changed ? nextRelations : relations,
    changed,
  };
}

function rewriteTableName<T extends string | null | undefined>(
  value: T,
  tableRenameMap: Map<string, string>,
): T {
  if (typeof value !== "string") {
    return value;
  }

  return (tableRenameMap.get(normalizeName(value)) ?? value) as T;
}

function buildNameMap<T extends NamedSchemaItem>(items: T[]): Map<string, T> {
  const map = new Map<string, T>();

  for (const item of items) {
    const name = getName(item);
    if (name) {
      map.set(normalizeName(name), item);
    }
  }

  return map;
}

function buildNameSet<T extends NamedSchemaItem>(items: T[]): Set<string> {
  return new Set(
    items
      .map((item) => getName(item))
      .filter((name): name is string => Boolean(name))
      .map(normalizeName),
  );
}

function getName(item: NamedSchemaItem | undefined): string | null {
  return typeof item?.name === "string" && item.name.trim() !== ""
    ? item.name
    : null;
}

function namesEqual(left: string, right: string): boolean {
  return normalizeName(left) === normalizeName(right);
}

function normalizeName(name: string): string {
  return name.toLowerCase();
}

function hasOwnRenamedFrom(item: NamedSchemaItem): boolean {
  return Object.prototype.hasOwnProperty.call(item, "renamedFrom");
}
