import { Icon } from "../../components/common/Icon";
import { EmptyState } from "../../components/ui/EmptyState";
import type {
  SchemaChange,
  SchemaColumn,
  SchemaModelDocument,
  SchemaRelation,
  SchemaTable,
} from "../../types/api";
import {
  formatMiniErdTypeValue,
  getRelationEndpoint,
} from "./schemaWorkbenchHelpers";

type MiniErdChangeTone = "added" | "changed" | "removed";

type MiniErdChangeAnnotation = {
  destructive: boolean;
  label: string;
  tone: MiniErdChangeTone;
  typeTransition?: {
    from: string;
    to: string;
  };
};

export type MiniErdChangeMap = {
  columns: Map<string, MiniErdChangeAnnotation>;
  hasChanges: boolean;
  relations: Map<string, MiniErdChangeAnnotation>;
  tables: Map<string, MiniErdChangeAnnotation>;
};

function getColumnBadgeLabels(column: SchemaColumn): string[] {
  const labels: string[] = [];

  if (column.unique) {
    labels.push("Unique");
  }

  if (column.nullable === false) {
    labels.push("Required");
  }

  return labels;
}

function getColumnChangeKey(tableName: string, columnName: string): string {
  return `${tableName}.${columnName}`;
}

function createEmptyMiniErdChangeMap(): MiniErdChangeMap {
  return {
    columns: new Map(),
    hasChanges: false,
    relations: new Map(),
    tables: new Map(),
  };
}

function setMiniErdChangeAnnotation(
  map: Map<string, MiniErdChangeAnnotation>,
  key: string | null | undefined,
  annotation: MiniErdChangeAnnotation,
) {
  if (!key) {
    return;
  }

  const current = map.get(key);
  if (
    current?.tone === "removed" ||
    (current?.destructive && !annotation.destructive)
  ) {
    return;
  }

  map.set(key, annotation);
}

function getChangedAnnotation(change: SchemaChange): MiniErdChangeAnnotation {
  return {
    tone: "changed",
    label: change.isDestructive ? "Destructive" : "Changed",
    destructive: change.isDestructive,
  };
}

export function buildMiniErdChangeMap(changes: SchemaChange[]): MiniErdChangeMap {
  const changeMap = createEmptyMiniErdChangeMap();
  changeMap.hasChanges = changes.length > 0;

  for (const change of changes) {
    switch (change.changeType) {
      case "AddTable":
        setMiniErdChangeAnnotation(changeMap.tables, change.table, {
          tone: "added",
          label: "Added",
          destructive: false,
        });
        break;
      case "DropTable":
        setMiniErdChangeAnnotation(changeMap.tables, change.table, {
          tone: "removed",
          label: "Removed",
          destructive: true,
        });
        break;
      case "RenameTable":
        setMiniErdChangeAnnotation(changeMap.tables, change.table, {
          tone: "changed",
          label: "Renamed",
          destructive: change.isDestructive,
        });
        break;
      case "AddColumn":
        setMiniErdChangeAnnotation(
          changeMap.columns,
          change.column
            ? getColumnChangeKey(change.table, change.column)
            : undefined,
          {
            tone: "added",
            label: "Added",
            destructive: false,
          },
        );
        break;
      case "DropColumn":
        setMiniErdChangeAnnotation(
          changeMap.columns,
          change.column
            ? getColumnChangeKey(change.table, change.column)
            : undefined,
          {
            tone: "removed",
            label: "Removed",
            destructive: true,
          },
        );
        break;
      case "RenameColumn":
        setMiniErdChangeAnnotation(
          changeMap.columns,
          change.column
            ? getColumnChangeKey(change.table, change.column)
            : undefined,
          {
            tone: "changed",
            label: "Renamed",
            destructive: change.isDestructive,
          },
        );
        break;
      case "AlterColumnType":
        setMiniErdChangeAnnotation(
          changeMap.columns,
          change.column
            ? getColumnChangeKey(change.table, change.column)
            : undefined,
          {
            tone: "changed",
            label: change.isDestructive ? "Destructive" : "Type changed",
            destructive: change.isDestructive,
            typeTransition: {
              from: formatMiniErdTypeValue(change.oldValue),
              to: formatMiniErdTypeValue(change.newValue),
            },
          },
        );
        break;
      case "AlterColumnNullability":
      case "AlterColumnDefault":
      case "AlterColumnUnique":
        setMiniErdChangeAnnotation(
          changeMap.columns,
          change.column
            ? getColumnChangeKey(change.table, change.column)
            : undefined,
          getChangedAnnotation(change),
        );
        break;
      case "AddRelation":
        setMiniErdChangeAnnotation(changeMap.relations, change.relation, {
          tone: "added",
          label: "Added",
          destructive: false,
        });
        break;
      case "DropRelation":
        setMiniErdChangeAnnotation(changeMap.relations, change.relation, {
          tone: "removed",
          label: "Removed",
          destructive: true,
        });
        break;
      case "AlterRelation":
        setMiniErdChangeAnnotation(
          changeMap.relations,
          change.relation,
          getChangedAnnotation(change),
        );
        break;
      case "AddIndex":
      case "DropIndex":
      case "RenameIndex":
      case "AlterIndex":
        setMiniErdChangeAnnotation(
          changeMap.tables,
          change.table,
          getChangedAnnotation(change),
        );
        break;
      default:
        break;
    }
  }

  return changeMap;
}

function getMiniErdAnnotationClass(
  baseClass: string,
  annotation: MiniErdChangeAnnotation | null | undefined,
): string {
  if (!annotation) {
    return baseClass;
  }

  return [
    baseClass,
    `${baseClass}--${annotation.tone}`,
    annotation.destructive ? `${baseClass}--destructive` : "",
  ]
    .filter(Boolean)
    .join(" ");
}

function findTableByName(
  document: SchemaModelDocument | null,
  tableName: string,
): SchemaTable | null {
  return document?.tables.find((table) => table.name === tableName) ?? null;
}

function findRelationByName(
  document: SchemaModelDocument | null,
  relationName: string,
): SchemaRelation | null {
  return (
    document?.relations.find((relation) => relation.name === relationName) ??
    null
  );
}

function buildMiniErdTables(
  document: SchemaModelDocument,
  baselineDocument: SchemaModelDocument | null,
  changeMap: MiniErdChangeMap,
): SchemaTable[] {
  const tables = [...document.tables];
  const names = new Set(tables.map((table) => table.name));

  changeMap.tables.forEach((annotation, tableName) => {
    if (annotation.tone !== "removed" || names.has(tableName)) {
      return;
    }

    const baselineTable = findTableByName(baselineDocument, tableName);
    if (baselineTable) {
      tables.push(baselineTable);
      names.add(tableName);
    }
  });

  return tables;
}

function buildMiniErdColumns(
  table: SchemaTable,
  baselineTable: SchemaTable | null,
  changeMap: MiniErdChangeMap,
): SchemaColumn[] {
  const columns = [...table.columns];
  const names = new Set(columns.map((column) => column.name));

  changeMap.columns.forEach((annotation, key) => {
    if (annotation.tone !== "removed" || !key.startsWith(`${table.name}.`)) {
      return;
    }

    const columnName = key.slice(table.name.length + 1);
    if (names.has(columnName)) {
      return;
    }

    const baselineColumn = baselineTable?.columns.find(
      (column) => column.name === columnName,
    );
    if (baselineColumn) {
      columns.push(baselineColumn);
      names.add(columnName);
    }
  });

  return columns;
}

function buildMiniErdRelations(
  document: SchemaModelDocument,
  baselineDocument: SchemaModelDocument | null,
  changeMap: MiniErdChangeMap,
): SchemaRelation[] {
  const relations = [...document.relations];
  const names = new Set(relations.map((relation) => relation.name));

  changeMap.relations.forEach((annotation, relationName) => {
    if (annotation.tone !== "removed" || names.has(relationName)) {
      return;
    }

    const baselineRelation = findRelationByName(baselineDocument, relationName);
    if (baselineRelation) {
      relations.push(baselineRelation);
      names.add(relationName);
    }
  });

  return relations;
}

function getMiniErdColumnTypeLabel(
  column: SchemaColumn,
  annotation: MiniErdChangeAnnotation | null | undefined,
): string {
  if (annotation?.typeTransition) {
    return `${annotation.typeTransition.from} -> ${annotation.typeTransition.to}`;
  }

  return formatMiniErdTypeValue(column.type);
}

type SchemaMiniErdProps = {
  baselineDocument: SchemaModelDocument | null;
  changeMap: MiniErdChangeMap;
  document: SchemaModelDocument | null;
  syntaxInvalid: boolean;
};

export function SchemaMiniErd({
  baselineDocument,
  changeMap,
  document,
  syntaxInvalid,
}: SchemaMiniErdProps) {
  if (syntaxInvalid) {
    return (
      <EmptyState
        title="Fix JSON syntax to update the schema map"
        description="The map stays empty while the draft is not valid JSON, so it never shows a stale structure."
        icon="schema"
        bullets={[
          "The JSON editor remains editable",
          "The schema map updates again as soon as the draft parses",
        ]}
      />
    );
  }

  if (!document) {
    return (
      <EmptyState
        title="Draft structure is not ready for a schema map"
        description="The JSON parses, but the draft does not expose the tables and relations arrays the workbench needs for a read-only topology preview."
        icon="schema"
        bullets={[
          "Add a tables array",
          "Add a relations array, even when it is empty",
        ]}
      />
    );
  }

  const tables = buildMiniErdTables(document, baselineDocument, changeMap);
  const relations = buildMiniErdRelations(
    document,
    baselineDocument,
    changeMap,
  );

  if (tables.length === 0) {
    return (
      <EmptyState
        title="No tables in this draft"
        description="The schema map will appear here once the draft includes at least one table."
        icon="schema"
        bullets={[
          "Empty schemas are valid for a new workspace",
          "Relations appear after both endpoints exist in the draft",
        ]}
      />
    );
  }

  return (
    <div
      className="schema-workbench__mini-erd"
      aria-label="Read-only schema map"
      data-testid="schema-mini-erd"
    >
      <div className="schema-workbench__mini-erd-stats">
        <div className="schema-workbench__mini-erd-stat">
          <Icon name="table" size={14} />
          <strong>{tables.length}</strong>
          <span>{tables.length === 1 ? "table" : "tables"}</span>
        </div>
        <div className="schema-workbench__mini-erd-stat">
          <Icon name="link" size={14} />
          <strong>{relations.length}</strong>
          <span>{relations.length === 1 ? "relation" : "relations"}</span>
        </div>
      </div>

      <div className="schema-workbench__mini-erd-table-grid">
        {tables.map((table) => (
          <MiniErdTable
            key={table.name}
            annotation={changeMap.tables.get(table.name)}
            baselineTable={findTableByName(baselineDocument, table.name)}
            changeMap={changeMap}
            table={table}
          />
        ))}
      </div>

      {relations.length > 0 ? (
        <section
          className="schema-workbench__mini-erd-relations"
          aria-label="Schema relations"
        >
          <h3>Relations</h3>
          <ol className="schema-workbench__mini-erd-relation-list">
            {relations.map((relation) => (
              <MiniErdRelation
                key={relation.name}
                annotation={changeMap.relations.get(relation.name)}
                relation={relation}
              />
            ))}
          </ol>
        </section>
      ) : null}
    </div>
  );
}

type MiniErdTableProps = {
  annotation?: MiniErdChangeAnnotation;
  baselineTable: SchemaTable | null;
  changeMap: MiniErdChangeMap;
  table: SchemaTable;
};

function MiniErdTable({
  annotation,
  baselineTable,
  changeMap,
  table,
}: MiniErdTableProps) {
  const columns = buildMiniErdColumns(table, baselineTable, changeMap);
  const visibleColumns = columns.slice(0, 6);
  const hiddenColumnCount = columns.length - visibleColumns.length;

  return (
    <section
      className={getMiniErdAnnotationClass(
        "schema-workbench__mini-erd-table",
        annotation,
      )}
      aria-label={table.name}
    >
      <header className="schema-workbench__mini-erd-table-header">
        <Icon name="table" size={14} />
        <strong>{table.displayName ?? table.name}</strong>
        {annotation ? (
          <span
            className={getMiniErdAnnotationClass(
              "schema-workbench__mini-erd-change-badge",
              annotation,
            )}
          >
            {annotation.label}
          </span>
        ) : null}
      </header>

      <ol className="schema-workbench__mini-erd-column-list">
        {visibleColumns.map((column) => {
          const badges = getColumnBadgeLabels(column);
          const columnAnnotation = changeMap.columns.get(
            getColumnChangeKey(table.name, column.name),
          );

          return (
            <li
              key={column.name}
              className={getMiniErdAnnotationClass(
                "schema-workbench__mini-erd-column",
                columnAnnotation,
              )}
            >
              <span
                className={
                  column.primaryKey
                    ? "schema-workbench__mini-erd-column-name schema-workbench__mini-erd-column-name--primary-key"
                    : "schema-workbench__mini-erd-column-name"
                }
              >
                {column.name}
              </span>
              <span className="schema-workbench__mini-erd-column-type">
                {getMiniErdColumnTypeLabel(column, columnAnnotation)}
              </span>
              {badges.length > 0 || columnAnnotation ? (
                <span className="schema-workbench__mini-erd-column-badges">
                  {columnAnnotation ? (
                    <span
                      className={getMiniErdAnnotationClass(
                        "schema-workbench__mini-erd-change-badge",
                        columnAnnotation,
                      )}
                    >
                      {columnAnnotation.label}
                    </span>
                  ) : null}
                  {badges.map((badge) => (
                    <span
                      key={badge}
                      className="schema-workbench__mini-erd-column-badge"
                    >
                      {badge}
                    </span>
                  ))}
                </span>
              ) : null}
            </li>
          );
        })}
      </ol>

      {hiddenColumnCount > 0 ? (
        <div className="schema-workbench__mini-erd-more">
          +{hiddenColumnCount} more
        </div>
      ) : null}
    </section>
  );
}

type MiniErdRelationProps = {
  annotation?: MiniErdChangeAnnotation;
  relation: SchemaRelation;
};

function MiniErdRelation({ annotation, relation }: MiniErdRelationProps) {
  const from = getRelationEndpoint(relation, "from");
  const to = getRelationEndpoint(relation, "to");

  return (
    <li
      className={getMiniErdAnnotationClass(
        "schema-workbench__mini-erd-relation",
        annotation,
      )}
    >
      <span className="schema-workbench__mini-erd-relation-name">
        {relation.name}
      </span>
      {annotation ? (
        <span
          className={getMiniErdAnnotationClass(
            "schema-workbench__mini-erd-change-badge",
            annotation,
          )}
        >
          {annotation.label}
        </span>
      ) : null}
      <span className="schema-workbench__mini-erd-relation-path">
        <span>{from}</span>
        <Icon name="arrow-right" size={13} />
        <span>{to}</span>
      </span>
      {relation.type ? (
        <span className="schema-workbench__mini-erd-relation-type">
          {relation.type}
        </span>
      ) : null}
    </li>
  );
}
