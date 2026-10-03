import type { SchemaModelDocument } from "../../../types/api";
import {
  getDefaultManyToManyRelationDraft,
  getDefaultOneToManyRelationDraft,
} from "../visualSchemaDraft";

export type VisualRelationDraft = {
  columnName: string;
  columnMode: "new" | "existing";
  fromAccessor: string;
  fromColumnName: string;
  fromTableName: string;
  indexName: string;
  junctionTableName: string;
  relationName: string;
  relationType: "1:N" | "N:M";
  toAccessor: string;
  toColumnName: string;
  toTableName: string;
};

export function getTableDeleteImpact(
  document: SchemaModelDocument,
  tableName: string,
) {
  const relationCount = document.relations.filter(
    (relation) => relation.from === tableName || relation.to === tableName,
  ).length;
  const referenceColumnCount = document.tables.reduce((count, table) => {
    if (table.name === tableName) {
      return count;
    }

    return (
      count +
      table.columns.filter(
        (column) =>
          column.type === "reference" && column.references === tableName,
      ).length
    );
  }, 0);

  return { referenceColumnCount, relationCount };
}

export function createVisualRelationDraft(
  document: SchemaModelDocument,
  fromTableName: string,
  toTableName = document.tables[0]?.name ?? fromTableName,
): VisualRelationDraft {
  const defaults = getDefaultOneToManyRelationDraft(
    document,
    fromTableName,
    toTableName,
  );
  const manyToManyDefaults = getDefaultManyToManyRelationDraft(
    document,
    fromTableName,
    toTableName,
  );

  return {
    columnMode: "new",
    relationType: "1:N",
    fromTableName,
    toTableName,
    fromColumnName: manyToManyDefaults.fromColumnName,
    junctionTableName: manyToManyDefaults.junctionTableName,
    toColumnName: manyToManyDefaults.toColumnName,
    ...defaults,
  };
}

export function getUniqueVisualName(
  existingNames: string[],
  baseName: string,
): string {
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
