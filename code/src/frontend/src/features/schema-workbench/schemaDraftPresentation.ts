import type {
  SchemaColumn,
  SchemaIndex,
  SchemaModelDocument,
  SchemaRelation,
  SchemaTable,
} from "../../types/api";

export function formatSchemaDraftValueForEditor(value: unknown): string {
  const document = asSchemaModelDocument(value);

  return JSON.stringify(
    document ? prepareSchemaDraftForEditor(document) : value,
    null,
    2,
  );
}

export function prepareSchemaDraftForEditor(
  document: SchemaModelDocument,
): SchemaModelDocument {
  return addVisibleRenameHintFields(stripRuntimeRelationMetadata(document));
}

export function addVisibleRenameHintFields(
  document: SchemaModelDocument,
): SchemaModelDocument {
  return {
    ...document,
    tables: document.tables.map(addTableRenameHintField),
  };
}

export function stripRuntimeRelationMetadata(
  document: SchemaModelDocument,
): SchemaModelDocument {
  return {
    ...document,
    relations: document.relations.map(stripRelationRuntimeMetadata),
  };
}

function stripRelationRuntimeMetadata(relation: SchemaRelation): SchemaRelation {
  const canonicalRelation = { ...relation };
  delete canonicalRelation.resolvedFromAccessor;
  delete canonicalRelation.resolvedToAccessor;
  delete canonicalRelation.resolvedJunctionTableName;

  return canonicalRelation;
}

function addTableRenameHintField(table: SchemaTable): SchemaTable {
  const { name, renamedFrom, columns, indexes, ...rest } = table;
  const nextTable: SchemaTable = {
    name,
    renamedFrom: renamedFrom === undefined ? name : renamedFrom,
    ...rest,
    columns: columns.map(addColumnRenameHintField),
  };

  if (Array.isArray(indexes)) {
    nextTable.indexes = indexes.map(addIndexRenameHintField);
  }

  return nextTable;
}

function addColumnRenameHintField(column: SchemaColumn): SchemaColumn {
  const { name, renamedFrom, ...rest } = column;

  return {
    name,
    renamedFrom: renamedFrom === undefined ? name : renamedFrom,
    ...rest,
  };
}

function addIndexRenameHintField(index: SchemaIndex): SchemaIndex {
  const { name, renamedFrom, ...rest } = index;

  return {
    name,
    renamedFrom: renamedFrom === undefined ? name : renamedFrom,
    ...rest,
  };
}

function asSchemaModelDocument(value: unknown): SchemaModelDocument | null {
  if (!value || typeof value !== "object") {
    return null;
  }

  const candidate = value as Partial<SchemaModelDocument>;
  if (!Array.isArray(candidate.tables) || !Array.isArray(candidate.relations)) {
    return null;
  }

  return candidate as SchemaModelDocument;
}
