import type {
  SchemaColumn,
  SchemaIndex,
  SchemaModelDocument,
  SchemaRelation,
  SchemaTable,
} from "../../types/api";

export const visualTableLayoutField = "layout";

export type VisualTableLayout = {
  x: number;
  y: number;
  color?: string | null;
  [key: string]: unknown;
};

export type VisualSchemaDraftIssueSeverity = "error" | "warning";

export type VisualSchemaDraftIssue = {
  code: string;
  message: string;
  path: string;
  severity: VisualSchemaDraftIssueSeverity;
};

export type VisualTableMutationResult = {
  document: SchemaModelDocument;
  tableName: string;
};

export type VisualRelationMutationResult = {
  document: SchemaModelDocument;
  issues: VisualSchemaDraftIssue[];
  relationName: string | null;
};

export type CreateOneToManyRelationOptions = {
  columnName?: string | null;
  fromAccessor?: string | null;
  fromTableName: string;
  indexName?: string | null;
  relationName?: string | null;
  reuseColumnName?: string | null;
  toAccessor?: string | null;
  toTableName: string;
};

export type CreateManyToManyRelationOptions = {
  fromAccessor?: string | null;
  fromColumnName?: string | null;
  fromTableName: string;
  junctionTableName?: string | null;
  relationName?: string | null;
  toAccessor?: string | null;
  toColumnName?: string | null;
  toTableName: string;
  layout?: VisualTableLayout | null;
};

export type UpdateOneToManyRelationOptions = {
  baselineDocument?: SchemaModelDocument | null;
  columnName?: string | null;
  fromAccessor?: string | null;
  fromTableName: string;
  relationName: string;
  toAccessor?: string | null;
  toTableName: string;
};

export type ConvertOneToManyRelationToManyToManyOptions = {
  fromAccessor?: string | null;
  fromColumnName?: string | null;
  junctionTableName?: string | null;
  layout?: VisualTableLayout | null;
  relationName: string;
  toAccessor?: string | null;
  toColumnName?: string | null;
};

export type ConvertManyToManyRelationToOneToManyOptions = {
  columnName?: string | null;
  fromAccessor?: string | null;
  relationName: string;
  toAccessor?: string | null;
};

export type UpdateManyToManyRelationOptions = {
  fromAccessor?: string | null;
  fromColumnName?: string | null;
  fromTableName?: string | null;
  junctionTableName?: string | null;
  relationName: string;
  toAccessor?: string | null;
  toColumnName?: string | null;
  toTableName?: string | null;
};

type NamedSchemaItem = {
  name: string;
};

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

function cloneTables(
  document: SchemaModelDocument,
  mapTable: (table: SchemaTable, index: number) => SchemaTable,
): SchemaModelDocument {
  return {
    ...document,
    tables: document.tables.map(mapTable),
  };
}

function clampInsertIndex(index: number | undefined, length: number): number {
  if (index === undefined || !Number.isInteger(index)) {
    return length;
  }

  return Math.max(0, Math.min(index, length));
}

function moveItem<T>(items: T[], fromIndex: number, toIndex: number): T[] {
  if (
    fromIndex < 0 ||
    fromIndex >= items.length ||
    toIndex < 0 ||
    toIndex >= items.length ||
    fromIndex === toIndex
  ) {
    return items.slice();
  }

  const next = items.slice();
  const [item] = next.splice(fromIndex, 1);
  next.splice(toIndex, 0, item);
  return next;
}

function getDuplicateNames<T extends NamedSchemaItem>(items: T[]): Set<string> {
  const seen = new Set<string>();
  const duplicates = new Set<string>();

  for (const item of items) {
    const name = item.name.trim();
    if (!name) {
      continue;
    }

    if (seen.has(name)) {
      duplicates.add(name);
    }

    seen.add(name);
  }

  return duplicates;
}

function pushIssue(
  issues: VisualSchemaDraftIssue[],
  code: string,
  message: string,
  path: string,
  severity: VisualSchemaDraftIssueSeverity = "error",
): void {
  issues.push({ code, message, path, severity });
}

function findTable(document: SchemaModelDocument, tableName: string): SchemaTable | null {
  return document.tables.find((table) => table.name === tableName) ?? null;
}

function findTableIndex(document: SchemaModelDocument, tableName: string): number {
  return document.tables.findIndex((table) => table.name === tableName);
}

function findColumnIndex(table: SchemaTable, columnName: string): number {
  return table.columns.findIndex((column) => column.name === columnName);
}

function findRelationIndex(
  document: SchemaModelDocument,
  relationName: string,
): number {
  return document.relations.findIndex((relation) => relation.name === relationName);
}

function findIndexIndex(table: SchemaTable, indexName: string): number {
  return (table.indexes ?? []).findIndex((index) => index.name === indexName);
}

function getVisibleJunctionTableName(relation: SchemaRelation): string | null {
  return relation.junctionTable?.trim() || relation.resolvedJunctionTableName?.trim() || null;
}

function getUniqueSchemaObjectName(existingNames: Iterable<string>, baseName: string): string {
  const names = new Set(existingNames);
  if (!names.has(baseName)) {
    return baseName;
  }

  let suffix = 2;
  while (names.has(`${baseName}_${suffix}`)) {
    suffix += 1;
  }

  return `${baseName}_${suffix}`;
}

function singularizeSchemaName(name: string): string {
  if (name.endsWith("ies") && name.length > 3) {
    return `${name.slice(0, -3)}y`;
  }

  if (name.endsWith("s") && name.length > 1) {
    return name.slice(0, -1);
  }

  return name;
}

function getReferenceAccessor(columnName: string, fallbackTableName: string): string {
  if (columnName.endsWith("_id") && columnName.length > 3) {
    return columnName.slice(0, -3);
  }

  return singularizeSchemaName(fallbackTableName);
}

function resolveExistingFromAccessor(relation: SchemaRelation): string {
  if (relation.fromAccessor?.trim()) {
    return relation.fromAccessor;
  }

  return relation.name;
}

function resolveExistingToAccessor(relation: SchemaRelation): string {
  if (relation.toAccessor?.trim()) {
    return relation.toAccessor;
  }

  return relation.column
    ? getReferenceAccessor(relation.column, relation.from)
    : singularizeSchemaName(relation.from);
}

function relationUsesAccessor(
  document: SchemaModelDocument,
  tableName: string,
  accessor: string,
  exceptRelationName?: string | null,
): boolean {
  return document.relations.some((relation) => {
    if (
      exceptRelationName &&
      relation.name.toLowerCase() === exceptRelationName.toLowerCase()
    ) {
      return false;
    }

    if (relation.from === tableName && resolveExistingFromAccessor(relation) === accessor) {
      return true;
    }

    return relation.to === tableName && resolveExistingToAccessor(relation) === accessor;
  });
}

function removeMissingIndexColumns(
  indexes: SchemaIndex[] | undefined,
  removedColumns: Set<string>,
): SchemaIndex[] | undefined {
  if (!indexes) {
    return indexes;
  }

  return indexes
    .map((index) => ({
      ...index,
      columns: index.columns.filter((columnName) => !removedColumns.has(columnName)),
    }))
    .filter((index) => index.columns.length > 0);
}

function cloneColumn(column: SchemaColumn): SchemaColumn {
  return { ...column };
}

function cloneIndex(index: SchemaIndex, existingNames: Set<string>, tableName: string): SchemaIndex {
  const baseName = index.name.includes(tableName)
    ? index.name
    : `idx_${tableName}_${index.columns.join("_") || "columns"}`;
  const name = getUniqueSchemaObjectName(existingNames, baseName);
  existingNames.add(name);

  const { renamedFrom: _renamedFrom, ...rest } = index;
  return {
    ...rest,
    name,
    columns: index.columns.slice(),
  };
}

export function getUniqueVisualSchemaName(
  existingNames: Iterable<string>,
  baseName: string,
): string {
  return getUniqueSchemaObjectName(existingNames, baseName);
}

export function getDefaultOneToManyRelationDraft(
  document: SchemaModelDocument,
  fromTableName: string,
  toTableName: string,
): {
  columnName: string;
  fromAccessor: string;
  indexName: string;
  relationName: string;
  toAccessor: string;
} {
  const fromTableBase = singularizeSchemaName(fromTableName);
  const relationBase =
    fromTableName === toTableName
      ? `${fromTableBase}_children`
      : `${fromTableBase}_${toTableName}`;
  const columnBase =
    fromTableName === toTableName
      ? `parent_${fromTableBase}_id`
      : `${fromTableBase}_id`;
  const toTable = findTable(document, toTableName);
  const columnName = getUniqueSchemaObjectName(
    toTable?.columns.map((column) => column.name) ?? [],
    columnBase,
  );
  const fromAccessor = getUniqueSchemaObjectName(
    document.relations
      .filter((relation) => relation.from === fromTableName)
      .map(resolveExistingFromAccessor),
    fromTableName === toTableName ? "children" : toTableName,
  );
  const toAccessor = getUniqueSchemaObjectName(
    document.relations
      .filter((relation) => relation.to === toTableName)
      .map(resolveExistingToAccessor),
    fromTableName === toTableName ? `parent_${fromTableBase}` : fromTableBase,
  );
  const indexName = getUniqueSchemaObjectName(
    toTable?.indexes?.map((index) => index.name) ?? [],
    `idx_${toTableName}_${columnName}`,
  );

  return {
    columnName,
    fromAccessor,
    indexName,
    relationName: getUniqueSchemaObjectName(
      document.relations.map((relation) => relation.name),
      relationBase,
    ),
    toAccessor,
  };
}

export function getDefaultManyToManyRelationDraft(
  document: SchemaModelDocument,
  fromTableName: string,
  toTableName: string,
): {
  fromAccessor: string;
  fromColumnName: string;
  junctionTableName: string;
  relationName: string;
  toAccessor: string;
  toColumnName: string;
} {
  const ordered = [fromTableName, toTableName].sort((left, right) =>
    left.localeCompare(right),
  );
  const fromBase = singularizeSchemaName(fromTableName);
  const toBase = singularizeSchemaName(toTableName);
  const relationBase =
    fromTableName === toTableName
      ? `${fromBase}_peers`
      : `${fromBase}_${toTableName}`;
  const junctionBase =
    fromTableName === toTableName
      ? `_jt_${fromTableName}_${fromTableName}`
      : `_jt_${ordered[0]}_${ordered[1]}`;

  return {
    fromColumnName:
      fromTableName === toTableName ? `left_${fromBase}_id` : `${fromBase}_id`,
    toColumnName:
      fromTableName === toTableName ? `right_${toBase}_id` : `${toBase}_id`,
    fromAccessor: getUniqueSchemaObjectName(
      document.relations
        .filter((relation) => relation.from === fromTableName)
        .map(resolveExistingFromAccessor),
      toTableName,
    ),
    toAccessor: getUniqueSchemaObjectName(
      document.relations
        .filter((relation) => relation.to === toTableName)
        .map(resolveExistingToAccessor),
      fromTableName,
    ),
    junctionTableName: getUniqueSchemaObjectName(
      document.tables.map((table) => table.name),
      junctionBase,
    ),
    relationName: getUniqueSchemaObjectName(
      document.relations.map((relation) => relation.name),
      relationBase,
    ),
  };
}

export function getTableLayout(table: SchemaTable): VisualTableLayout | null {
  const layout = table[visualTableLayoutField];
  if (!isRecord(layout)) {
    return null;
  }

  if (typeof layout.x !== "number" || typeof layout.y !== "number") {
    return null;
  }

  return layout as VisualTableLayout;
}

export function setTableLayout(
  document: SchemaModelDocument,
  tableName: string,
  layout: VisualTableLayout,
): SchemaModelDocument {
  return cloneTables(document, (table) =>
    table.name === tableName
      ? {
          ...table,
          [visualTableLayoutField]: {
            ...(isRecord(table[visualTableLayoutField])
              ? table[visualTableLayoutField]
              : {}),
            ...layout,
          },
        }
      : table,
  );
}

export function clearTableLayout(
  document: SchemaModelDocument,
  tableName: string,
): SchemaModelDocument {
  return cloneTables(document, (table) => {
    if (table.name !== tableName) {
      return table;
    }

    const { [visualTableLayoutField]: _layout, ...rest } = table;
    return rest as SchemaTable;
  });
}

export function addTable(
  document: SchemaModelDocument,
  table: SchemaTable,
  insertIndex?: number,
): SchemaModelDocument {
  const tables = document.tables.slice();
  tables.splice(clampInsertIndex(insertIndex, tables.length), 0, table);

  return {
    ...document,
    tables,
  };
}

export function createStarterTable(
  document: SchemaModelDocument,
  layout?: VisualTableLayout,
): VisualTableMutationResult {
  const tableName = getUniqueSchemaObjectName(
    document.tables.map((table) => table.name),
    "new_table",
  );
  const table: SchemaTable = {
    name: tableName,
    columns: [
      {
        name: "id",
        type: "integer",
        primaryKey: true,
        nullable: false,
      },
    ],
    indexes: [],
    ...(layout ? { [visualTableLayoutField]: layout } : {}),
  };

  return {
    document: addTable(document, table),
    tableName,
  };
}

export function updateTable(
  document: SchemaModelDocument,
  tableName: string,
  update: Partial<SchemaTable> | ((table: SchemaTable) => SchemaTable),
): SchemaModelDocument {
  return cloneTables(document, (table) => {
    if (table.name !== tableName) {
      return table;
    }

    return typeof update === "function" ? update(table) : { ...table, ...update };
  });
}

export function renameTable(
  document: SchemaModelDocument,
  tableName: string,
  nextName: string,
  renamedFrom?: string | null,
): SchemaModelDocument {
  const shouldUpdateRenameHint = arguments.length >= 4;

  return {
    ...document,
    tables: document.tables.map((table) => {
      if (table.name !== tableName) {
        return {
          ...table,
          columns: table.columns.map((column) =>
            column.type === "reference" && column.references === tableName
              ? { ...column, references: nextName }
              : column,
          ),
        };
      }

      const nextTable: SchemaTable = {
        ...table,
        name: nextName,
      };

      if (shouldUpdateRenameHint) {
        if (renamedFrom) {
          nextTable.renamedFrom = renamedFrom;
        } else {
          delete nextTable.renamedFrom;
        }
      }

      return nextTable;
    }),
    relations: document.relations.map((relation) => ({
      ...relation,
      from: relation.from === tableName ? nextName : relation.from,
      to: relation.to === tableName ? nextName : relation.to,
      junctionTable:
        relation.junctionTable === tableName ? nextName : relation.junctionTable,
    })),
  };
}

export function removeTable(
  document: SchemaModelDocument,
  tableName: string,
): SchemaModelDocument {
  return {
    ...document,
    tables: document.tables.filter((table) => table.name !== tableName),
  };
}

export function deleteTableWithDependents(
  document: SchemaModelDocument,
  tableName: string,
): SchemaModelDocument {
  return {
    ...document,
    tables: document.tables
      .filter((table) => table.name !== tableName)
      .map((table) => {
        const removedColumns = new Set(
          table.columns
            .filter(
              (column) =>
                column.type === "reference" && column.references === tableName,
            )
            .map((column) => column.name),
        );

        if (removedColumns.size === 0) {
          return table;
        }

        return {
          ...table,
          columns: table.columns.filter((column) => !removedColumns.has(column.name)),
          indexes: removeMissingIndexColumns(table.indexes, removedColumns),
        };
      }),
    relations: document.relations.filter(
      (relation) =>
        relation.from !== tableName &&
        relation.to !== tableName &&
        relation.junctionTable !== tableName,
    ),
  };
}

export function duplicateTable(
  document: SchemaModelDocument,
  tableName: string,
): VisualTableMutationResult {
  const source = findTable(document, tableName);
  if (!source) {
    return { document, tableName };
  }

  const nextTableName = getUniqueSchemaObjectName(
    document.tables.map((table) => table.name),
    `${tableName}_copy`,
  );
  const layout = getTableLayout(source);
  const existingIndexNames = new Set<string>();
  const { renamedFrom: _renamedFrom, ...sourceWithoutRenameHint } = source;
  const duplicatedTable: SchemaTable = {
    ...sourceWithoutRenameHint,
    name: nextTableName,
    displayName: source.displayName ? `${source.displayName} Copy` : undefined,
    columns: source.columns.map((column) => {
      const { renamedFrom: _columnRenamedFrom, ...rest } = cloneColumn(column);
      if (rest.type === "reference" && rest.references === tableName) {
        rest.references = nextTableName;
      }

      return rest;
    }),
    indexes: (source.indexes ?? []).map((index) =>
      cloneIndex(index, existingIndexNames, nextTableName),
    ),
    ...(layout
      ? {
          [visualTableLayoutField]: {
            ...layout,
            x: layout.x + 36,
            y: layout.y + 36,
          },
        }
      : {}),
  };

  const existingRelationNames = new Set(document.relations.map((relation) => relation.name));
  const duplicatedRelations = document.relations
    .filter((relation) => relation.from === tableName && relation.to === tableName)
    .map((relation) => {
      const relationName = getUniqueSchemaObjectName(
        existingRelationNames,
        `${relation.name}_copy`,
      );
      existingRelationNames.add(relationName);

      return {
        ...relation,
        name: relationName,
        from: nextTableName,
        to: nextTableName,
      };
    });

  return {
    document: {
      ...document,
      tables: [...document.tables, duplicatedTable],
      relations: [...document.relations, ...duplicatedRelations],
    },
    tableName: nextTableName,
  };
}

export function addColumn(
  document: SchemaModelDocument,
  tableName: string,
  column: SchemaColumn,
  insertIndex?: number,
): SchemaModelDocument {
  return cloneTables(document, (table) => {
    if (table.name !== tableName) {
      return table;
    }

    const columns = table.columns.slice();
    columns.splice(clampInsertIndex(insertIndex, columns.length), 0, column);
    return { ...table, columns };
  });
}

export function updateColumn(
  document: SchemaModelDocument,
  tableName: string,
  columnName: string,
  update: Partial<SchemaColumn> | ((column: SchemaColumn) => SchemaColumn),
): SchemaModelDocument {
  return cloneTables(document, (table) => {
    if (table.name !== tableName) {
      return table;
    }

    return {
      ...table,
      columns: table.columns.map((column) => {
        if (column.name !== columnName) {
          return column;
        }

        return typeof update === "function"
          ? update(column)
          : { ...column, ...update };
      }),
    };
  });
}

export function removeColumn(
  document: SchemaModelDocument,
  tableName: string,
  columnName: string,
): SchemaModelDocument {
  return cloneTables(document, (table) =>
    table.name === tableName
      ? {
          ...table,
          columns: table.columns.filter((column) => column.name !== columnName),
        }
      : table,
  );
}

export function reorderColumn(
  document: SchemaModelDocument,
  tableName: string,
  columnName: string,
  targetIndex: number,
): SchemaModelDocument {
  return cloneTables(document, (table) => {
    if (table.name !== tableName) {
      return table;
    }

    const fromIndex = findColumnIndex(table, columnName);
    return {
      ...table,
      columns: moveItem(table.columns, fromIndex, targetIndex),
    };
  });
}

export function addRelation(
  document: SchemaModelDocument,
  relation: SchemaRelation,
  insertIndex?: number,
): SchemaModelDocument {
  const relations = document.relations.slice();
  relations.splice(clampInsertIndex(insertIndex, relations.length), 0, relation);

  return {
    ...document,
    relations,
  };
}

export function updateRelation(
  document: SchemaModelDocument,
  relationName: string,
  update: Partial<SchemaRelation> | ((relation: SchemaRelation) => SchemaRelation),
): SchemaModelDocument {
  return {
    ...document,
    relations: document.relations.map((relation) => {
      if (relation.name !== relationName) {
        return relation;
      }

      return typeof update === "function"
        ? update(relation)
        : { ...relation, ...update };
    }),
  };
}

function renameColumnInTable(
  document: SchemaModelDocument,
  tableName: string,
  columnName: string,
  nextColumnName: string,
): SchemaModelDocument {
  return cloneTables(document, (table) => {
    if (table.name !== tableName) {
      return table;
    }

    return {
      ...table,
      columns: table.columns.map((column) =>
        column.name === columnName ? { ...column, name: nextColumnName } : column,
      ),
      indexes: table.indexes?.map((index) => ({
        ...index,
        columns: index.columns.map((indexedColumn) =>
          indexedColumn === columnName ? nextColumnName : indexedColumn,
        ),
      })),
    };
  });
}

export function removeRelation(
  document: SchemaModelDocument,
  relationName: string,
): SchemaModelDocument {
  return {
    ...document,
    relations: document.relations.filter(
      (relation) => relation.name !== relationName,
    ),
  };
}

export function renameRelation(
  document: SchemaModelDocument,
  relationName: string,
  nextName: string,
): VisualRelationMutationResult {
  const trimmed = nextName.trim();
  const relation = document.relations.find(
    (candidate) => candidate.name === relationName,
  );

  if (!relation) {
    return {
      document,
      issues: [
        {
          code: "visual_missing_relation",
          message: `Relation "${relationName}" does not exist.`,
          path: "relations[]",
          severity: "error",
        },
      ],
      relationName,
    };
  }

  if (trimmed === relationName) {
    return { document, issues: [], relationName };
  }

  if (!trimmed) {
    return {
      document,
      issues: [
        {
          code: "visual_relation_name_required",
          message: "Relation name is required.",
          path: "relations[].name",
          severity: "error",
        },
      ],
      relationName,
    };
  }

  if (
    document.relations.some(
      (candidate) => candidate.name.toLowerCase() === trimmed.toLowerCase(),
    )
  ) {
    return {
      document,
      issues: [
        {
          code: "visual_relation_name_taken",
          message: `Relation name "${trimmed}" is already in use.`,
          path: "relations[].name",
          severity: "error",
        },
      ],
      relationName,
    };
  }

  const renamedRelations = document.relations.map((candidate) =>
    candidate.name === relationName ? { ...candidate, name: trimmed } : candidate,
  );

  const nextTables = document.tables.map((table) => {
    let columnsChanged = false;
    const nextColumns = table.columns.map((column) => {
      if (
        column.type === "reference" &&
        typeof column.relationName === "string" &&
        column.relationName === relationName
      ) {
        columnsChanged = true;
        return { ...column, relationName: trimmed };
      }
      return column;
    });

    if (!columnsChanged) {
      return table;
    }

    return { ...table, columns: nextColumns };
  });

  return {
    document: {
      ...document,
      tables: nextTables,
      relations: renamedRelations,
    },
    issues: [],
    relationName: trimmed,
  };
}

export function deleteRelationWithArtifacts(
  document: SchemaModelDocument,
  relationName: string,
): VisualRelationMutationResult {
  const relation = document.relations.find(
    (candidate) => candidate.name === relationName,
  );

  if (!relation) {
    return {
      document,
      issues: [
        {
          code: "visual_missing_relation",
          message: `Relation "${relationName}" does not exist.`,
          path: "relations[]",
          severity: "error",
        },
      ],
      relationName,
    };
  }

  let nextDocument = removeRelation(document, relationName);

  if (relation.type === "1:N" && relation.column) {
    nextDocument = removeColumn(nextDocument, relation.to, relation.column);
    nextDocument = cloneTables(nextDocument, (table) =>
      table.name === relation.to
        ? {
            ...table,
            indexes: removeMissingIndexColumns(
              table.indexes,
              new Set([relation.column!]),
            ),
          }
        : table,
    );
  }

  const junctionTableName =
    relation.type === "N:M"
      ? relation.junctionTable?.trim() || relation.resolvedJunctionTableName?.trim()
      : null;

  if (
    junctionTableName &&
    nextDocument.tables.some((table) => table.name === junctionTableName)
  ) {
    nextDocument = removeTable(nextDocument, junctionTableName);
  }

  return {
    document: nextDocument,
    issues: [],
    relationName: null,
  };
}

export function updateOneToManyRelation(
  document: SchemaModelDocument,
  options: UpdateOneToManyRelationOptions,
): VisualRelationMutationResult {
  const issues: VisualSchemaDraftIssue[] = [];
  const relation = document.relations.find(
    (candidate) => candidate.name === options.relationName,
  );
  const fromTable = findTable(document, options.fromTableName);
  const toTable = findTable(document, options.toTableName);

  if (!relation) {
    pushIssue(
      issues,
      "visual_missing_relation",
      `Relation "${options.relationName}" does not exist.`,
      "relations[]",
    );
    return { document, issues, relationName: options.relationName };
  }

  if (relation.type !== "1:N") {
    const nextDocument = updateRelation(document, relation.name, {
      from: options.fromTableName,
      to: options.toTableName,
      fromAccessor: options.fromAccessor ?? relation.fromAccessor,
      toAccessor: options.toAccessor ?? relation.toAccessor,
    });
    return { document: nextDocument, issues: [], relationName: relation.name };
  }

  if (!fromTable) {
    pushIssue(
      issues,
      "visual_missing_relation_from_table",
      `Relation parent table "${options.fromTableName}" does not exist.`,
      `relations[].from`,
    );
  }

  if (!toTable) {
    pushIssue(
      issues,
      "visual_missing_relation_to_table",
      `Relation child table "${options.toTableName}" does not exist.`,
      `relations[].to`,
    );
  }

  const defaults = getDefaultOneToManyRelationDraft(
    document,
    options.fromTableName,
    options.toTableName,
  );
  const columnName = (options.columnName ?? relation.column ?? defaults.columnName).trim();
  const fromAccessor = (options.fromAccessor ?? relation.fromAccessor ?? defaults.fromAccessor).trim();
  const toAccessor = (options.toAccessor ?? relation.toAccessor ?? defaults.toAccessor).trim();

  if (!columnName) {
    pushIssue(
      issues,
      "visual_column_name_required",
      "Reference column name is required.",
      `relations[].column`,
    );
  }

  if (!fromAccessor) {
    pushIssue(
      issues,
      "visual_relation_from_accessor_required",
      "Parent-table accessor is required.",
      `relations[].fromAccessor`,
    );
  } else if (
    fromTable &&
    relationUsesAccessor(document, options.fromTableName, fromAccessor, relation.name)
  ) {
    pushIssue(
      issues,
      "visual_relation_accessor_collision",
      `Accessor "${fromAccessor}" is already used on table "${options.fromTableName}".`,
      `relations[].fromAccessor`,
    );
  }

  if (!toAccessor) {
    pushIssue(
      issues,
      "visual_relation_to_accessor_required",
      "Child-table accessor is required.",
      `relations[].toAccessor`,
    );
  } else if (
    toTable &&
    relationUsesAccessor(document, options.toTableName, toAccessor, relation.name)
  ) {
    pushIssue(
      issues,
      "visual_relation_accessor_collision",
      `Accessor "${toAccessor}" is already used on table "${options.toTableName}".`,
      `relations[].toAccessor`,
    );
  }

  if (
    options.fromTableName === options.toTableName &&
    fromAccessor &&
    toAccessor &&
    fromAccessor === toAccessor
  ) {
    pushIssue(
      issues,
      "visual_relation_accessor_collision",
      `Self-referential relation accessors must be distinct on table "${options.fromTableName}".`,
      `relations[].toAccessor`,
    );
  }

  if (toTable && columnName) {
    const existingColumn = toTable.columns.find(
      (column) => column.name === columnName,
    );
    if (
      existingColumn &&
      (existingColumn.type !== "reference" ||
        existingColumn.references !== options.fromTableName)
    ) {
      pushIssue(
        issues,
        "visual_invalid_relation_reference_target",
        `Column "${options.toTableName}.${columnName}" must be a reference to "${options.fromTableName}".`,
        `relations[].column`,
      );
    }
  }

  if (issues.length > 0 || !fromTable || !toTable) {
    return { document, issues, relationName: relation.name };
  }

  const isDraftOnlyRelation = !options.baselineDocument?.relations.some(
    (candidate) => candidate.name === relation.name,
  );

  let nextDocument = document;
  if (
    relation.to &&
    relation.column &&
    (relation.to !== options.toTableName || relation.column !== columnName)
  ) {
    const previousColumnName = relation.column;
    const previousTable = findTable(nextDocument, relation.to);
    const previousColumn = previousTable?.columns.find(
      (column) => column.name === previousColumnName,
    );
    if (previousColumn) {
      nextDocument = removeColumn(nextDocument, relation.to, previousColumnName);
      nextDocument = cloneTables(nextDocument, (table) =>
        table.name === relation.to
          ? {
              ...table,
              indexes: removeMissingIndexColumns(table.indexes, new Set([previousColumnName])),
            }
          : table,
      );
    }
  }

  const targetTable = findTable(nextDocument, options.toTableName);
  const targetColumn = targetTable?.columns.find(
    (column) => column.name === columnName,
  );
  if (!targetColumn) {
    nextDocument = addColumn(nextDocument, options.toTableName, {
      name: columnName,
      type: "reference",
      references: options.fromTableName,
      nullable: true,
      relationName: relation.name,
    });
  } else if (isDraftOnlyRelation && targetColumn.type === "reference") {
    nextDocument = updateColumn(
      nextDocument,
      options.toTableName,
      columnName,
      {
        references: options.fromTableName,
        relationName: targetColumn.relationName ?? relation.name,
      },
    );
  }

  const tableAfterColumn = findTable(nextDocument, options.toTableName);
  const hasSupportingIndex = Boolean(
    tableAfterColumn?.indexes?.some(
      (index) => index.columns.length === 1 && index.columns[0] === columnName,
    ),
  );
  if (isDraftOnlyRelation && tableAfterColumn && !hasSupportingIndex) {
    nextDocument = addIndex(nextDocument, options.toTableName, {
      name: getUniqueSchemaObjectName(
        tableAfterColumn.indexes?.map((index) => index.name) ?? [],
        `idx_${options.toTableName}_${columnName}`,
      ),
      columns: [columnName],
      unique: false,
    });
  }

  nextDocument = updateRelation(nextDocument, relation.name, {
    from: options.fromTableName,
    to: options.toTableName,
    column: columnName,
    fromAccessor,
    toAccessor,
  });

  return { document: nextDocument, issues: [], relationName: relation.name };
}

export function updateManyToManyRelation(
  document: SchemaModelDocument,
  options: UpdateManyToManyRelationOptions,
): VisualRelationMutationResult {
  const relation = document.relations.find(
    (candidate) => candidate.name === options.relationName,
  );
  if (!relation) {
    return {
      document,
      issues: [
        {
          code: "visual_missing_relation",
          message: `Relation "${options.relationName}" does not exist.`,
          path: "relations[]",
          severity: "error",
        },
      ],
      relationName: options.relationName,
    };
  }

  if (relation.type !== "N:M") {
    return {
      document,
      issues: [
        {
          code: "visual_invalid_relation_type",
          message: `Relation "${options.relationName}" is not an N:M relation.`,
          path: "relations[]",
          severity: "error",
        },
      ],
      relationName: relation.name,
    };
  }

  const issues: VisualSchemaDraftIssue[] = [];
  const currentJunctionTableName = getVisibleJunctionTableName(relation);
  const nextFrom = (options.fromTableName ?? relation.from).trim();
  const nextTo = (options.toTableName ?? relation.to).trim();
  const nextFromColumn = (options.fromColumnName ?? relation.fromColumn ?? "").trim();
  const nextToColumn = (options.toColumnName ?? relation.toColumn ?? "").trim();
  const nextJunctionTableName = (
    options.junctionTableName ??
    currentJunctionTableName ??
    ""
  ).trim();
  const nextFromAccessor = (options.fromAccessor ?? relation.fromAccessor ?? "").trim();
  const nextToAccessor = (options.toAccessor ?? relation.toAccessor ?? "").trim();

  const fromTable = findTable(document, nextFrom);
  const toTable = findTable(document, nextTo);
  const junctionTable = currentJunctionTableName
    ? findTable(document, currentJunctionTableName)
    : null;

  if (!nextFrom || !fromTable) {
    pushIssue(
      issues,
      "visual_missing_relation_from_table",
      `Relation source table "${nextFrom}" does not exist.`,
      "relations[].from",
    );
  }

  if (!nextTo || !toTable) {
    pushIssue(
      issues,
      "visual_missing_relation_to_table",
      `Relation target table "${nextTo}" does not exist.`,
      "relations[].to",
    );
  }

  if (!currentJunctionTableName || !junctionTable) {
    pushIssue(
      issues,
      "visual_missing_junction_table",
      `Relation "${relation.name}" does not have a visible junction table to edit.`,
      "relations[].junctionTable",
    );
  }

  if (!nextJunctionTableName) {
    pushIssue(
      issues,
      "visual_junction_table_name_required",
      "Junction table name is required.",
      "relations[].junctionTable",
    );
  } else if (
    nextJunctionTableName !== currentJunctionTableName &&
    document.tables.some((table) => table.name === nextJunctionTableName)
  ) {
    pushIssue(
      issues,
      "visual_duplicate_table_name",
      `Table name "${nextJunctionTableName}" is already used.`,
      "relations[].junctionTable",
    );
  }

  if (!nextFromColumn) {
    pushIssue(
      issues,
      "visual_column_name_required",
      "Source reference column name is required.",
      "relations[].fromColumn",
    );
  }

  if (!nextToColumn) {
    pushIssue(
      issues,
      "visual_column_name_required",
      "Target reference column name is required.",
      "relations[].toColumn",
    );
  }

  if (nextFromColumn && nextToColumn && nextFromColumn === nextToColumn) {
    pushIssue(
      issues,
      "visual_duplicate_column_name",
      "Junction columns must be distinct.",
      "relations[].toColumn",
    );
  }

  const currentFromColumn = relation.fromColumn ?? "";
  const currentToColumn = relation.toColumn ?? "";
  if (junctionTable) {
    const existingNames = junctionTable.columns.map((column) => column.name);
    if (
      nextFromColumn !== currentFromColumn &&
      existingNames.some(
        (name) => name !== currentFromColumn && name === nextFromColumn,
      )
    ) {
      pushIssue(
        issues,
        "visual_duplicate_column_name",
        `Column name "${nextFromColumn}" is already used in "${junctionTable.name}".`,
        "relations[].fromColumn",
      );
    }

    if (
      nextToColumn !== currentToColumn &&
      existingNames.some((name) => name !== currentToColumn && name === nextToColumn)
    ) {
      pushIssue(
        issues,
        "visual_duplicate_column_name",
        `Column name "${nextToColumn}" is already used in "${junctionTable.name}".`,
        "relations[].toColumn",
      );
    }
  }

  if (!nextFromAccessor) {
    pushIssue(
      issues,
      "visual_relation_from_accessor_required",
      "Source-table accessor is required.",
      "relations[].fromAccessor",
    );
  } else if (fromTable && relationUsesAccessor(document, nextFrom, nextFromAccessor, relation.name)) {
    pushIssue(
      issues,
      "visual_relation_accessor_collision",
      `Accessor "${nextFromAccessor}" is already used on table "${nextFrom}".`,
      "relations[].fromAccessor",
    );
  }

  if (!nextToAccessor) {
    pushIssue(
      issues,
      "visual_relation_to_accessor_required",
      "Target-table accessor is required.",
      "relations[].toAccessor",
    );
  } else if (toTable && relationUsesAccessor(document, nextTo, nextToAccessor, relation.name)) {
    pushIssue(
      issues,
      "visual_relation_accessor_collision",
      `Accessor "${nextToAccessor}" is already used on table "${nextTo}".`,
      "relations[].toAccessor",
    );
  }

  if (
    nextFrom === nextTo &&
    nextFromAccessor &&
    nextToAccessor &&
    nextFromAccessor === nextToAccessor
  ) {
    pushIssue(
      issues,
      "visual_relation_accessor_collision",
      `Self-referential relation accessors must be distinct on table "${nextFrom}".`,
      "relations[].toAccessor",
    );
  }

  if (issues.length > 0 || !junctionTable || !currentJunctionTableName) {
    return { document, issues, relationName: relation.name };
  }

  let nextDocument = document;
  if (nextJunctionTableName !== currentJunctionTableName) {
    nextDocument = renameTable(
      nextDocument,
      currentJunctionTableName,
      nextJunctionTableName,
      relation.renamedFrom as string | null | undefined,
    );
  }

  if (nextFromColumn !== currentFromColumn && currentFromColumn) {
    nextDocument = renameColumnInTable(
      nextDocument,
      nextJunctionTableName,
      currentFromColumn,
      nextFromColumn,
    );
  }

  if (nextToColumn !== currentToColumn && currentToColumn) {
    nextDocument = renameColumnInTable(
      nextDocument,
      nextJunctionTableName,
      currentToColumn,
      nextToColumn,
    );
  }

  nextDocument = updateColumn(nextDocument, nextJunctionTableName, nextFromColumn, {
    type: "reference",
    references: nextFrom,
    nullable: false,
    primaryKey: true,
  });
  nextDocument = updateColumn(nextDocument, nextJunctionTableName, nextToColumn, {
    type: "reference",
    references: nextTo,
    nullable: false,
    primaryKey: true,
  });
  nextDocument = updateRelation(nextDocument, relation.name, (current) => ({
    ...current,
    from: nextFrom,
    to: nextTo,
    fromColumn: nextFromColumn,
    toColumn: nextToColumn,
    junctionTable: nextJunctionTableName,
    fromAccessor: nextFromAccessor,
    toAccessor: nextToAccessor,
  }));

  return { document: nextDocument, issues: [], relationName: relation.name };
}

export function switchManyToManyRelationDirection(
  document: SchemaModelDocument,
  relationName: string,
): VisualRelationMutationResult {
  const relation = document.relations.find(
    (candidate) => candidate.name === relationName,
  );

  if (!relation) {
    return {
      document,
      issues: [
        {
          code: "visual_missing_relation",
          message: `Relation "${relationName}" does not exist.`,
          path: "relations[]",
          severity: "error",
        },
      ],
      relationName,
    };
  }

  if (relation.type !== "N:M") {
    return {
      document,
      issues: [
        {
          code: "visual_invalid_relation_type",
          message: `Relation "${relationName}" is not an N:M relation.`,
          path: "relations[]",
          severity: "error",
        },
      ],
      relationName,
    };
  }

  const junctionTableName = getVisibleJunctionTableName(relation);
  if (!junctionTableName || !findTable(document, junctionTableName)) {
    return {
      document,
      issues: [
        {
          code: "visual_missing_junction_table",
          message: `Relation "${relation.name}" does not have a visible junction table to edit.`,
          path: "relations[].junctionTable",
          severity: "error",
        },
      ],
      relationName,
    };
  }

  let nextDocument = updateColumn(
    document,
    junctionTableName,
    relation.toColumn ?? "",
    {
      type: "reference",
      references: relation.to,
      nullable: false,
      primaryKey: true,
    },
  );
  nextDocument = updateColumn(
    nextDocument,
    junctionTableName,
    relation.fromColumn ?? "",
    {
      type: "reference",
      references: relation.from,
      nullable: false,
      primaryKey: true,
    },
  );
  nextDocument = updateRelation(nextDocument, relation.name, {
    from: relation.to,
    to: relation.from,
    fromColumn: relation.toColumn,
    toColumn: relation.fromColumn,
    fromAccessor: relation.toAccessor,
    toAccessor: relation.fromAccessor,
  });

  return { document: nextDocument, issues: [], relationName };
}

export function switchOneToManyRelationDirection(
  document: SchemaModelDocument,
  relationName: string,
  baselineDocument?: SchemaModelDocument | null,
): VisualRelationMutationResult {
  const relation = document.relations.find(
    (candidate) => candidate.name === relationName,
  );
  if (!relation) {
    return {
      document,
      issues: [
        {
          code: "visual_missing_relation",
          message: `Relation "${relationName}" does not exist.`,
          path: "relations[]",
          severity: "error",
        },
      ],
      relationName,
    };
  }

  if (relation.type !== "1:N") {
    return updateOneToManyRelation(document, {
      baselineDocument,
      relationName,
      fromTableName: relation.to,
      toTableName: relation.from,
      fromAccessor: relation.toAccessor,
      toAccessor: relation.fromAccessor,
    });
  }

  const defaults = getDefaultOneToManyRelationDraft(
    document,
    relation.to,
    relation.from,
  );

  return updateOneToManyRelation(document, {
    baselineDocument,
    relationName,
    fromTableName: relation.to,
    toTableName: relation.from,
    columnName: defaults.columnName,
    fromAccessor: relation.toAccessor ?? defaults.fromAccessor,
    toAccessor: relation.fromAccessor ?? defaults.toAccessor,
  });
}

export function addIndex(
  document: SchemaModelDocument,
  tableName: string,
  index: SchemaIndex,
  insertIndex?: number,
): SchemaModelDocument {
  return cloneTables(document, (table) => {
    if (table.name !== tableName) {
      return table;
    }

    const indexes = (table.indexes ?? []).slice();
    indexes.splice(clampInsertIndex(insertIndex, indexes.length), 0, index);
    return { ...table, indexes };
  });
}

export function updateIndex(
  document: SchemaModelDocument,
  tableName: string,
  indexName: string,
  update: Partial<SchemaIndex> | ((index: SchemaIndex) => SchemaIndex),
): SchemaModelDocument {
  return cloneTables(document, (table) => {
    if (table.name !== tableName) {
      return table;
    }

    return {
      ...table,
      indexes: (table.indexes ?? []).map((index) => {
        if (index.name !== indexName) {
          return index;
        }

        return typeof update === "function" ? update(index) : { ...index, ...update };
      }),
    };
  });
}

export function removeIndex(
  document: SchemaModelDocument,
  tableName: string,
  indexName: string,
): SchemaModelDocument {
  return cloneTables(document, (table) =>
    table.name === tableName
      ? {
          ...table,
          indexes: (table.indexes ?? []).filter((index) => index.name !== indexName),
        }
      : table,
  );
}

export function createOneToManyRelation(
  document: SchemaModelDocument,
  options: CreateOneToManyRelationOptions,
): VisualRelationMutationResult {
  const issues: VisualSchemaDraftIssue[] = [];
  const fromTable = findTable(document, options.fromTableName);
  const toTable = findTable(document, options.toTableName);
  const defaults = getDefaultOneToManyRelationDraft(
    document,
    options.fromTableName,
    options.toTableName,
  );
  const relationName = (options.relationName ?? defaults.relationName).trim();
  const columnName = (
    options.reuseColumnName ??
    options.columnName ??
    defaults.columnName
  ).trim();
  const fromAccessor = (options.fromAccessor ?? defaults.fromAccessor).trim();
  const toAccessor = (options.toAccessor ?? defaults.toAccessor).trim();
  const indexName = (options.indexName ?? defaults.indexName).trim();
  const shouldReuseColumn = Boolean(options.reuseColumnName);

  if (!fromTable) {
    pushIssue(
      issues,
      "visual_missing_relation_from_table",
      `Relation parent table "${options.fromTableName}" does not exist.`,
      "relations[].from",
    );
  }

  if (!toTable) {
    pushIssue(
      issues,
      "visual_missing_relation_to_table",
      `Relation child table "${options.toTableName}" does not exist.`,
      "relations[].to",
    );
  }

  if (!relationName) {
    pushIssue(
      issues,
      "visual_relation_name_required",
      "Relation name is required.",
      "relations[].name",
    );
  } else if (
    document.relations.some((relation) => relation.name === relationName)
  ) {
    pushIssue(
      issues,
      "visual_duplicate_relation_name",
      `Relation name "${relationName}" is already used.`,
      "relations[].name",
    );
  }

  if (!columnName) {
    pushIssue(
      issues,
      "visual_column_name_required",
      "Reference column name is required.",
      "relations[].column",
    );
  }

  if (!fromAccessor) {
    pushIssue(
      issues,
      "visual_relation_from_accessor_required",
      "Parent-table accessor is required.",
      "relations[].fromAccessor",
    );
  } else if (
    fromTable &&
    relationUsesAccessor(document, options.fromTableName, fromAccessor)
  ) {
    pushIssue(
      issues,
      "visual_relation_accessor_collision",
      `Accessor "${fromAccessor}" is already used on table "${options.fromTableName}".`,
      "relations[].fromAccessor",
    );
  }

  if (!toAccessor) {
    pushIssue(
      issues,
      "visual_relation_to_accessor_required",
      "Child-table accessor is required.",
      "relations[].toAccessor",
    );
  } else if (
    toTable &&
    relationUsesAccessor(document, options.toTableName, toAccessor)
  ) {
    pushIssue(
      issues,
      "visual_relation_accessor_collision",
      `Accessor "${toAccessor}" is already used on table "${options.toTableName}".`,
      "relations[].toAccessor",
    );
  }

  if (
    options.fromTableName === options.toTableName &&
    fromAccessor &&
    toAccessor &&
    fromAccessor === toAccessor
  ) {
    pushIssue(
      issues,
      "visual_relation_accessor_collision",
      `Self-referential relation accessors must be distinct on table "${options.fromTableName}".`,
      "relations[].toAccessor",
    );
  }

  if (toTable && columnName) {
    const existingColumn = toTable.columns.find(
      (column) => column.name === columnName,
    );

    if (shouldReuseColumn) {
      if (!existingColumn) {
        pushIssue(
          issues,
          "visual_missing_relation_column",
          `Reference column "${options.toTableName}.${columnName}" does not exist.`,
          "relations[].column",
        );
      } else if (
        existingColumn.type !== "reference" ||
        existingColumn.references !== options.fromTableName
      ) {
        pushIssue(
          issues,
          "visual_invalid_relation_reference_target",
          `Column "${options.toTableName}.${columnName}" must be a reference to "${options.fromTableName}".`,
          "relations[].column",
        );
      }
    } else if (existingColumn) {
      pushIssue(
        issues,
        "visual_duplicate_column_name",
        `Column name "${columnName}" is already used in "${options.toTableName}".`,
        "relations[].column",
      );
    }
  }

  const hasSupportingIndex = Boolean(
    toTable?.indexes?.some(
      (index) => index.columns.length === 1 && index.columns[0] === columnName,
    ),
  );

  if (toTable && indexName && !hasSupportingIndex) {
    const duplicateIndex = (toTable.indexes ?? []).some(
      (index) => index.name === indexName,
    );
    if (duplicateIndex) {
      pushIssue(
        issues,
        "visual_duplicate_index_name",
        `Index name "${indexName}" is already used in "${options.toTableName}".`,
        "relations[].index",
      );
    }
  }

  if (issues.length > 0 || !fromTable || !toTable) {
    return { document, issues, relationName: relationName || null };
  }

  let nextDocument = document;
  if (!shouldReuseColumn) {
    nextDocument = addColumn(nextDocument, options.toTableName, {
      name: columnName,
      type: "reference",
      references: options.fromTableName,
      nullable: true,
      relationName,
    });
  }

  if (indexName && !hasSupportingIndex) {
    nextDocument = addIndex(nextDocument, options.toTableName, {
      name: indexName,
      columns: [columnName],
      unique: false,
    });
  }

  nextDocument = addRelation(nextDocument, {
    name: relationName,
    type: "1:N",
    from: options.fromTableName,
    to: options.toTableName,
    column: columnName,
    fromAccessor,
    toAccessor,
  });

  return {
    document: nextDocument,
    issues: [],
    relationName,
  };
}

export function createManyToManyRelation(
  document: SchemaModelDocument,
  options: CreateManyToManyRelationOptions,
): VisualRelationMutationResult {
  const issues: VisualSchemaDraftIssue[] = [];
  const fromTable = findTable(document, options.fromTableName);
  const toTable = findTable(document, options.toTableName);
  const defaults = getDefaultManyToManyRelationDraft(
    document,
    options.fromTableName,
    options.toTableName,
  );
  const relationName = (options.relationName ?? defaults.relationName).trim();
  const junctionTableName = (
    options.junctionTableName ?? defaults.junctionTableName
  ).trim();
  const fromColumnName = (
    options.fromColumnName ?? defaults.fromColumnName
  ).trim();
  const toColumnName = (options.toColumnName ?? defaults.toColumnName).trim();
  const fromAccessor = (options.fromAccessor ?? defaults.fromAccessor).trim();
  const toAccessor = (options.toAccessor ?? defaults.toAccessor).trim();

  if (!fromTable) {
    pushIssue(
      issues,
      "visual_missing_relation_from_table",
      `Relation source table "${options.fromTableName}" does not exist.`,
      "relations[].from",
    );
  }

  if (!toTable) {
    pushIssue(
      issues,
      "visual_missing_relation_to_table",
      `Relation target table "${options.toTableName}" does not exist.`,
      "relations[].to",
    );
  }

  if (!relationName) {
    pushIssue(
      issues,
      "visual_relation_name_required",
      "Relation name is required.",
      "relations[].name",
    );
  } else if (document.relations.some((relation) => relation.name === relationName)) {
    pushIssue(
      issues,
      "visual_duplicate_relation_name",
      `Relation name "${relationName}" is already used.`,
      "relations[].name",
    );
  }

  if (!junctionTableName) {
    pushIssue(
      issues,
      "visual_junction_table_name_required",
      "Junction table name is required.",
      "tables[].name",
    );
  } else if (document.tables.some((table) => table.name === junctionTableName)) {
    pushIssue(
      issues,
      "visual_duplicate_table_name",
      `Table name "${junctionTableName}" is already used.`,
      "tables[].name",
    );
  }

  if (!fromColumnName) {
    pushIssue(
      issues,
      "visual_column_name_required",
      "Source reference column name is required.",
      "relations[].fromColumn",
    );
  }

  if (!toColumnName) {
    pushIssue(
      issues,
      "visual_column_name_required",
      "Target reference column name is required.",
      "relations[].toColumn",
    );
  }

  if (fromColumnName && toColumnName && fromColumnName === toColumnName) {
    pushIssue(
      issues,
      "visual_duplicate_column_name",
      `Junction columns must be distinct.`,
      "relations[].toColumn",
    );
  }

  if (!fromAccessor) {
    pushIssue(
      issues,
      "visual_relation_from_accessor_required",
      "Source-table accessor is required.",
      "relations[].fromAccessor",
    );
  } else if (
    fromTable &&
    relationUsesAccessor(document, options.fromTableName, fromAccessor)
  ) {
    pushIssue(
      issues,
      "visual_relation_accessor_collision",
      `Accessor "${fromAccessor}" is already used on table "${options.fromTableName}".`,
      "relations[].fromAccessor",
    );
  }

  if (!toAccessor) {
    pushIssue(
      issues,
      "visual_relation_to_accessor_required",
      "Target-table accessor is required.",
      "relations[].toAccessor",
    );
  } else if (
    toTable &&
    relationUsesAccessor(document, options.toTableName, toAccessor)
  ) {
    pushIssue(
      issues,
      "visual_relation_accessor_collision",
      `Accessor "${toAccessor}" is already used on table "${options.toTableName}".`,
      "relations[].toAccessor",
    );
  }

  if (
    options.fromTableName === options.toTableName &&
    fromAccessor &&
    toAccessor &&
    fromAccessor === toAccessor
  ) {
    pushIssue(
      issues,
      "visual_relation_accessor_collision",
      `Self-referential relation accessors must be distinct on table "${options.fromTableName}".`,
      "relations[].toAccessor",
    );
  }

  if (issues.length > 0 || !fromTable || !toTable) {
    return { document, issues, relationName: relationName || null };
  }

  let nextDocument = addTable(document, {
    name: junctionTableName,
    columns: [
      {
        name: fromColumnName,
        type: "reference",
        references: options.fromTableName,
        nullable: false,
        primaryKey: true,
      },
      {
        name: toColumnName,
        type: "reference",
        references: options.toTableName,
        nullable: false,
        primaryKey: true,
      },
      {
        name: "created_at",
        type: "datetime",
        nullable: true,
      },
    ],
    indexes: [],
    ...(options.layout ? { [visualTableLayoutField]: options.layout } : {}),
  });

  nextDocument = addRelation(nextDocument, {
    name: relationName,
    type: "N:M",
    from: options.fromTableName,
    to: options.toTableName,
    fromColumn: fromColumnName,
    toColumn: toColumnName,
    junctionTable: junctionTableName,
    fromAccessor,
    toAccessor,
  });

  return {
    document: nextDocument,
    issues: [],
    relationName,
  };
}

export function convertOneToManyRelationToManyToMany(
  document: SchemaModelDocument,
  options: ConvertOneToManyRelationToManyToManyOptions,
): VisualRelationMutationResult {
  const relation = document.relations.find(
    (candidate) => candidate.name === options.relationName,
  );
  if (!relation) {
    return {
      document,
      issues: [
        {
          code: "visual_missing_relation",
          message: `Relation "${options.relationName}" does not exist.`,
          path: "relations[]",
          severity: "error",
        },
      ],
      relationName: options.relationName,
    };
  }

  if (relation.type !== "1:N") {
    return {
      document,
      issues: [
        {
          code: "visual_invalid_relation_type",
          message: `Relation "${options.relationName}" is not a 1:N relation.`,
          path: "relations[]",
          severity: "error",
        },
      ],
      relationName: options.relationName,
    };
  }

  const issues: VisualSchemaDraftIssue[] = [];
  const fromTable = findTable(document, relation.from);
  const toTable = findTable(document, relation.to);
  const defaults = getDefaultManyToManyRelationDraft(
    document,
    relation.from,
    relation.to,
  );
  const junctionTableName = (
    options.junctionTableName ?? defaults.junctionTableName
  ).trim();
  const fromColumnName = (
    options.fromColumnName ?? relation.column ?? defaults.fromColumnName
  ).trim();
  const toColumnName = (options.toColumnName ?? defaults.toColumnName).trim();
  const fromAccessor = (
    options.fromAccessor ?? relation.fromAccessor ?? defaults.fromAccessor
  ).trim();
  const toAccessor = (options.toAccessor ?? defaults.toAccessor).trim();

  if (!fromTable) {
    pushIssue(
      issues,
      "visual_missing_relation_from_table",
      `Relation source table "${relation.from}" does not exist.`,
      "relations[].from",
    );
  }

  if (!toTable) {
    pushIssue(
      issues,
      "visual_missing_relation_to_table",
      `Relation target table "${relation.to}" does not exist.`,
      "relations[].to",
    );
  }

  if (!junctionTableName) {
    pushIssue(
      issues,
      "visual_junction_table_name_required",
      "Junction table name is required.",
      "tables[].name",
    );
  } else if (
    document.tables.some((table) => table.name === junctionTableName)
  ) {
    pushIssue(
      issues,
      "visual_duplicate_table_name",
      `Table name "${junctionTableName}" is already used.`,
      "tables[].name",
    );
  }

  if (!fromColumnName) {
    pushIssue(
      issues,
      "visual_column_name_required",
      "Source reference column name is required.",
      "relations[].fromColumn",
    );
  }

  if (!toColumnName) {
    pushIssue(
      issues,
      "visual_column_name_required",
      "Target reference column name is required.",
      "relations[].toColumn",
    );
  }

  if (fromColumnName && toColumnName && fromColumnName === toColumnName) {
    pushIssue(
      issues,
      "visual_duplicate_column_name",
      "Junction columns must be distinct.",
      "relations[].toColumn",
    );
  }

  if (!fromAccessor) {
    pushIssue(
      issues,
      "visual_relation_from_accessor_required",
      "Source-table accessor is required.",
      "relations[].fromAccessor",
    );
  } else if (
    relationUsesAccessor(document, relation.from, fromAccessor, relation.name)
  ) {
    pushIssue(
      issues,
      "visual_relation_accessor_collision",
      `Accessor "${fromAccessor}" is already used on table "${relation.from}".`,
      "relations[].fromAccessor",
    );
  }

  if (!toAccessor) {
    pushIssue(
      issues,
      "visual_relation_to_accessor_required",
      "Target-table accessor is required.",
      "relations[].toAccessor",
    );
  } else if (
    relationUsesAccessor(document, relation.to, toAccessor, relation.name)
  ) {
    pushIssue(
      issues,
      "visual_relation_accessor_collision",
      `Accessor "${toAccessor}" is already used on table "${relation.to}".`,
      "relations[].toAccessor",
    );
  }

  if (
    relation.from === relation.to &&
    fromAccessor &&
    toAccessor &&
    fromAccessor === toAccessor
  ) {
    pushIssue(
      issues,
      "visual_relation_accessor_collision",
      `Self-referential relation accessors must be distinct on table "${relation.from}".`,
      "relations[].toAccessor",
    );
  }

  if (issues.length > 0 || !fromTable || !toTable) {
    return { document, issues, relationName: relation.name };
  }

  let nextDocument = document;
  if (relation.column) {
    nextDocument = removeColumn(nextDocument, relation.to, relation.column);
    nextDocument = cloneTables(nextDocument, (table) =>
      table.name === relation.to
        ? {
            ...table,
            indexes: removeMissingIndexColumns(
              table.indexes,
              new Set([relation.column!]),
            ),
          }
        : table,
    );
  }

  nextDocument = addTable(nextDocument, {
    name: junctionTableName,
    columns: [
      {
        name: fromColumnName,
        type: "reference",
        references: relation.from,
        nullable: false,
        primaryKey: true,
      },
      {
        name: toColumnName,
        type: "reference",
        references: relation.to,
        nullable: false,
        primaryKey: true,
      },
      {
        name: "created_at",
        type: "datetime",
        nullable: true,
      },
    ],
    indexes: [],
    ...(options.layout ? { [visualTableLayoutField]: options.layout } : {}),
  });

  nextDocument = updateRelation(nextDocument, relation.name, (current) => {
    const {
      column: _column,
      resolvedFromAccessor: _resolvedFromAccessor,
      resolvedJunctionTableName: _resolvedJunctionTableName,
      resolvedToAccessor: _resolvedToAccessor,
      ...rest
    } = current;

    return {
      ...rest,
      type: "N:M",
      fromColumn: fromColumnName,
      toColumn: toColumnName,
      junctionTable: junctionTableName,
      fromAccessor,
      toAccessor,
    };
  });

  return {
    document: nextDocument,
    issues: [],
    relationName: relation.name,
  };
}

export function convertManyToManyRelationToOneToMany(
  document: SchemaModelDocument,
  options: ConvertManyToManyRelationToOneToManyOptions,
): VisualRelationMutationResult {
  const relation = document.relations.find(
    (candidate) => candidate.name === options.relationName,
  );
  if (!relation) {
    return {
      document,
      issues: [
        {
          code: "visual_missing_relation",
          message: `Relation "${options.relationName}" does not exist.`,
          path: "relations[]",
          severity: "error",
        },
      ],
      relationName: options.relationName,
    };
  }

  if (relation.type !== "N:M") {
    return {
      document,
      issues: [
        {
          code: "visual_invalid_relation_type",
          message: `Relation "${options.relationName}" is not an N:M relation.`,
          path: "relations[]",
          severity: "error",
        },
      ],
      relationName: options.relationName,
    };
  }

  const issues: VisualSchemaDraftIssue[] = [];
  const fromTable = findTable(document, relation.from);
  const toTable = findTable(document, relation.to);
  const defaults = getDefaultOneToManyRelationDraft(
    document,
    relation.from,
    relation.to,
  );
  const columnName = (
    options.columnName ?? relation.fromColumn ?? defaults.columnName
  ).trim();
  const fromAccessor = (
    options.fromAccessor ?? relation.fromAccessor ?? defaults.fromAccessor
  ).trim();
  const toAccessor = (options.toAccessor ?? defaults.toAccessor).trim();

  if (!fromTable) {
    pushIssue(
      issues,
      "visual_missing_relation_from_table",
      `Relation parent table "${relation.from}" does not exist.`,
      "relations[].from",
    );
  }

  if (!toTable) {
    pushIssue(
      issues,
      "visual_missing_relation_to_table",
      `Relation child table "${relation.to}" does not exist.`,
      "relations[].to",
    );
  }

  if (!columnName) {
    pushIssue(
      issues,
      "visual_column_name_required",
      "Reference column name is required.",
      "relations[].column",
    );
  }

  if (!fromAccessor) {
    pushIssue(
      issues,
      "visual_relation_from_accessor_required",
      "Parent-table accessor is required.",
      "relations[].fromAccessor",
    );
  } else if (
    relationUsesAccessor(document, relation.from, fromAccessor, relation.name)
  ) {
    pushIssue(
      issues,
      "visual_relation_accessor_collision",
      `Accessor "${fromAccessor}" is already used on table "${relation.from}".`,
      "relations[].fromAccessor",
    );
  }

  if (!toAccessor) {
    pushIssue(
      issues,
      "visual_relation_to_accessor_required",
      "Child-table accessor is required.",
      "relations[].toAccessor",
    );
  } else if (
    relationUsesAccessor(document, relation.to, toAccessor, relation.name)
  ) {
    pushIssue(
      issues,
      "visual_relation_accessor_collision",
      `Accessor "${toAccessor}" is already used on table "${relation.to}".`,
      "relations[].toAccessor",
    );
  }

  if (
    relation.from === relation.to &&
    fromAccessor &&
    toAccessor &&
    fromAccessor === toAccessor
  ) {
    pushIssue(
      issues,
      "visual_relation_accessor_collision",
      `Self-referential relation accessors must be distinct on table "${relation.from}".`,
      "relations[].toAccessor",
    );
  }

  const existingColumn = toTable?.columns.find((column) => column.name === columnName);
  if (existingColumn) {
    if (
      existingColumn.type !== "reference" ||
      existingColumn.references !== relation.from
    ) {
      pushIssue(
        issues,
        "visual_invalid_relation_reference_target",
        `Column "${relation.to}.${columnName}" must be a reference to "${relation.from}".`,
        "relations[].column",
      );
    }
  }

  if (issues.length > 0 || !fromTable || !toTable) {
    return { document, issues, relationName: relation.name };
  }

  let nextDocument = document;
  if (!existingColumn) {
    nextDocument = addColumn(nextDocument, relation.to, {
      name: columnName,
      type: "reference",
      references: relation.from,
      nullable: true,
      relationName: relation.name,
    });
  }

  const updatedToTable = findTable(nextDocument, relation.to);
  const hasSupportingIndex = Boolean(
    updatedToTable?.indexes?.some(
      (index) => index.columns.length === 1 && index.columns[0] === columnName,
    ),
  );
  if (updatedToTable && !hasSupportingIndex) {
    nextDocument = addIndex(nextDocument, relation.to, {
      name: getUniqueSchemaObjectName(
        updatedToTable.indexes?.map((index) => index.name) ?? [],
        `idx_${relation.to}_${columnName}`,
      ),
      columns: [columnName],
      unique: false,
    });
  }

  const junctionTableName =
    relation.junctionTable?.trim() || relation.resolvedJunctionTableName?.trim();
  if (
    junctionTableName &&
    nextDocument.tables.some((table) => table.name === junctionTableName)
  ) {
    nextDocument = removeTable(nextDocument, junctionTableName);
  }

  nextDocument = updateRelation(nextDocument, relation.name, (current) => {
    const {
      fromColumn: _fromColumn,
      junctionTable: _junctionTable,
      resolvedFromAccessor: _resolvedFromAccessor,
      resolvedJunctionTableName: _resolvedJunctionTableName,
      resolvedToAccessor: _resolvedToAccessor,
      toColumn: _toColumn,
      ...rest
    } = current;

    return {
      ...rest,
      type: "1:N",
      column: columnName,
      fromAccessor,
      toAccessor,
    };
  });

  return {
    document: nextDocument,
    issues: [],
    relationName: relation.name,
  };
}

export function validateVisualSchemaDraft(
  document: SchemaModelDocument,
): VisualSchemaDraftIssue[] {
  const issues: VisualSchemaDraftIssue[] = [];
  const tableNames = new Set(document.tables.map((table) => table.name));
  const duplicateTableNames = getDuplicateNames(document.tables);
  const duplicateRelationNames = getDuplicateNames(document.relations);

  document.tables.forEach((table, tableIndex) => {
    if (!table.name.trim()) {
      pushIssue(
        issues,
        "visual_table_name_required",
        "Table name is required.",
        `tables[${tableIndex}].name`,
      );
    }

    if (duplicateTableNames.has(table.name.trim())) {
      pushIssue(
        issues,
        "visual_duplicate_table_name",
        `Table name "${table.name}" is used more than once.`,
        `tables[${tableIndex}].name`,
      );
    }

    const layout = table[visualTableLayoutField];
    if (layout !== undefined) {
      if (!isRecord(layout)) {
        pushIssue(
          issues,
          "visual_invalid_table_layout",
          "Table layout metadata must be an object.",
          `tables[${tableIndex}].${visualTableLayoutField}`,
        );
      } else {
        if (typeof layout.x !== "number" || typeof layout.y !== "number") {
          pushIssue(
            issues,
            "visual_invalid_table_layout_position",
            "Table layout metadata must include numeric x and y coordinates.",
            `tables[${tableIndex}].${visualTableLayoutField}`,
          );
        }

        if (
          layout.color !== undefined &&
          layout.color !== null &&
          typeof layout.color !== "string"
        ) {
          pushIssue(
            issues,
            "visual_invalid_table_layout_color",
            "Table layout color must be a string or null.",
            `tables[${tableIndex}].${visualTableLayoutField}.color`,
          );
        }
      }
    }

    const duplicateColumnNames = getDuplicateNames(table.columns);
    table.columns.forEach((column, columnIndex) => {
      if (!column.name.trim()) {
        pushIssue(
          issues,
          "visual_column_name_required",
          "Column name is required.",
          `tables[${tableIndex}].columns[${columnIndex}].name`,
        );
      }

      if (duplicateColumnNames.has(column.name.trim())) {
        pushIssue(
          issues,
          "visual_duplicate_column_name",
          `Column name "${column.name}" is used more than once in "${table.name}".`,
          `tables[${tableIndex}].columns[${columnIndex}].name`,
        );
      }

      if (
        column.type === "reference" &&
        (!column.references || !tableNames.has(column.references))
      ) {
        pushIssue(
          issues,
          "visual_missing_reference_target",
          `Reference column "${table.name}.${column.name}" must target an existing table.`,
          `tables[${tableIndex}].columns[${columnIndex}].references`,
        );
      }

      if (
        column.type === "enum" &&
        (!Array.isArray(column.values) || column.values.length === 0)
      ) {
        pushIssue(
          issues,
          "visual_enum_values_required",
          `Enum column "${table.name}.${column.name}" must define at least one value.`,
          `tables[${tableIndex}].columns[${columnIndex}].values`,
        );
      }
    });

    const duplicateIndexNames = getDuplicateNames(table.indexes ?? []);
    (table.indexes ?? []).forEach((index, indexIndex) => {
      if (!index.name.trim()) {
        pushIssue(
          issues,
          "visual_index_name_required",
          "Index name is required.",
          `tables[${tableIndex}].indexes[${indexIndex}].name`,
        );
      }

      if (duplicateIndexNames.has(index.name.trim())) {
        pushIssue(
          issues,
          "visual_duplicate_index_name",
          `Index name "${index.name}" is used more than once in "${table.name}".`,
          `tables[${tableIndex}].indexes[${indexIndex}].name`,
        );
      }

      index.columns.forEach((columnName, indexColumnIndex) => {
        if (findColumnIndex(table, columnName) === -1) {
          pushIssue(
            issues,
            "visual_missing_index_column",
            `Index "${index.name}" references missing column "${columnName}".`,
            `tables[${tableIndex}].indexes[${indexIndex}].columns[${indexColumnIndex}]`,
          );
        }
      });
    });
  });

  document.relations.forEach((relation, relationIndex) => {
    if (!relation.name.trim()) {
      pushIssue(
        issues,
        "visual_relation_name_required",
        "Relation name is required.",
        `relations[${relationIndex}].name`,
      );
    }

    if (duplicateRelationNames.has(relation.name.trim())) {
      pushIssue(
        issues,
        "visual_duplicate_relation_name",
        `Relation name "${relation.name}" is used more than once.`,
        `relations[${relationIndex}].name`,
      );
    }

    const fromTable = findTable(document, relation.from);
    const toTable = findTable(document, relation.to);
    const fromAccessor = resolveExistingFromAccessor(relation);
    const toAccessor = resolveExistingToAccessor(relation);

    if (!fromTable) {
      pushIssue(
        issues,
        "visual_missing_relation_from_table",
        `Relation "${relation.name}" starts at missing table "${relation.from}".`,
        `relations[${relationIndex}].from`,
      );
    }

    if (!toTable) {
      pushIssue(
        issues,
        "visual_missing_relation_to_table",
        `Relation "${relation.name}" ends at missing table "${relation.to}".`,
        `relations[${relationIndex}].to`,
      );
    }

    if (!fromAccessor.trim()) {
      pushIssue(
        issues,
        "visual_relation_from_accessor_required",
        `Relation "${relation.name}" requires a parent-table accessor.`,
        `relations[${relationIndex}].fromAccessor`,
      );
    } else if (
      fromTable &&
      relationUsesAccessor(document, relation.from, fromAccessor, relation.name)
    ) {
      pushIssue(
        issues,
        "visual_relation_accessor_collision",
        `Accessor "${fromAccessor}" is already used on table "${relation.from}".`,
        `relations[${relationIndex}].fromAccessor`,
      );
    }

    if (!toAccessor.trim()) {
      pushIssue(
        issues,
        "visual_relation_to_accessor_required",
        `Relation "${relation.name}" requires a child-table accessor.`,
        `relations[${relationIndex}].toAccessor`,
      );
    } else if (
      toTable &&
      relationUsesAccessor(document, relation.to, toAccessor, relation.name)
    ) {
      pushIssue(
        issues,
        "visual_relation_accessor_collision",
        `Accessor "${toAccessor}" is already used on table "${relation.to}".`,
        `relations[${relationIndex}].toAccessor`,
      );
    }

    if (
      relation.from === relation.to &&
      fromAccessor.trim() &&
      toAccessor.trim() &&
      fromAccessor === toAccessor
    ) {
      pushIssue(
        issues,
        "visual_relation_accessor_collision",
        `Self-referential relation accessors must be distinct on table "${relation.from}".`,
        `relations[${relationIndex}].toAccessor`,
      );
    }

    if (relation.type === "1:N" && relation.column && toTable) {
      const column = toTable.columns.find((candidate) => candidate.name === relation.column);
      if (!column) {
        pushIssue(
          issues,
          "visual_missing_relation_column",
          `Relation "${relation.name}" references missing column "${relation.to}.${relation.column}".`,
          `relations[${relationIndex}].column`,
        );
      } else if (column.type === "reference" && column.references !== relation.from) {
        pushIssue(
          issues,
          "visual_invalid_relation_reference_target",
          `Relation "${relation.name}" uses a reference column that does not target "${relation.from}".`,
          `relations[${relationIndex}].column`,
        );
      }
    }
  });

  return issues;
}

export function getVisualDraftPathForTable(
  document: SchemaModelDocument,
  tableName: string,
): string | null {
  const tableIndex = findTableIndex(document, tableName);
  return tableIndex === -1 ? null : `tables[${tableIndex}]`;
}

export function getVisualDraftPathForColumn(
  document: SchemaModelDocument,
  tableName: string,
  columnName: string,
): string | null {
  const tableIndex = findTableIndex(document, tableName);
  if (tableIndex === -1) {
    return null;
  }

  const columnIndex = findColumnIndex(document.tables[tableIndex], columnName);
  return columnIndex === -1 ? null : `tables[${tableIndex}].columns[${columnIndex}]`;
}

export function getVisualDraftPathForRelation(
  document: SchemaModelDocument,
  relationName: string,
): string | null {
  const relationIndex = findRelationIndex(document, relationName);
  return relationIndex === -1 ? null : `relations[${relationIndex}]`;
}

export function getVisualDraftPathForIndex(
  document: SchemaModelDocument,
  tableName: string,
  indexName: string,
): string | null {
  const tableIndex = findTableIndex(document, tableName);
  if (tableIndex === -1) {
    return null;
  }

  const indexIndex = findIndexIndex(document.tables[tableIndex], indexName);
  return indexIndex === -1 ? null : `tables[${tableIndex}].indexes[${indexIndex}]`;
}
