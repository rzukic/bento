import { useState } from "react";
import { Icon } from "../../../components/common/Icon";
import { Button } from "../../../components/ui/Button";
import { EmptyState } from "../../../components/ui/EmptyState";
import { SelectField } from "../../../components/ui/SelectField";
import { TextField } from "../../../components/ui/TextField";
import type {
  SchemaColumn,
  SchemaColumnType,
  SchemaModelDocument,
  SchemaTable,
} from "../../../types/api";
import {
  countLabel,
  formatMiniErdTypeValue,
} from "../schemaWorkbenchHelpers";
import type { VisualCanvasSelection } from "../types";
import {
  addColumn,
  createManyToManyRelation,
  createOneToManyRelation,
  deleteTableWithDependents,
  duplicateTable,
  getTableLayout,
  setTableLayout,
  removeColumn,
  renameTable,
  reorderColumn,
  updateColumn,
  updateTable,
  type VisualSchemaDraftIssue,
} from "../visualSchemaDraft";
import {
  getNextVisualTableLayout,
  getSuggestedJunctionLayout,
} from "./erdRouting";
import {
  createVisualRelationDraft,
  getTableDeleteImpact,
  getUniqueVisualName,
  type VisualRelationDraft,
} from "./visualDraftHelpers";
import { VisualRelationCreator } from "./VisualRelationCreator";
import { VisualRelationInspector } from "./VisualRelationInspector";

const visualColumnTypes: SchemaColumnType[] = [
  "text",
  "integer",
  "float",
  "boolean",
  "date",
  "datetime",
  "json",
  "reference",
  "computed",
  "currency",
  "enum",
  "uuid",
  "file",
];

const visualColumnTypeOptions = visualColumnTypes.map((type) => ({
  label: type,
  value: type,
}));

type VisualSchemaInspectorProps = {
  baselineDocument: SchemaModelDocument | null;
  document: SchemaModelDocument;
  selection: VisualCanvasSelection | null;
  onDocumentChange: (document: SchemaModelDocument) => void;
  onSelectionChange: (selection: VisualCanvasSelection | null) => void;
};

type VisualTableColorFieldProps = {
  table: SchemaTable;
  onChange: (color: string | null) => void;
};

const erdDefaultLayoutColor = "#2563eb";

function VisualTableColorField({ table, onChange }: VisualTableColorFieldProps) {
  const layout = getTableLayout(table);
  const currentColor =
    typeof layout?.color === "string" && layout.color.length > 0
      ? layout.color
      : null;

  return (
    <div className="schema-workbench__visual-color-field">
      <div className="schema-workbench__visual-color-row">
        <span
          className="schema-workbench__visual-color-swatch"
          title="Table color"
        >
          <input
            type="color"
            aria-label="Table color"
            value={currentColor ?? erdDefaultLayoutColor}
            onChange={(event) => onChange(event.currentTarget.value)}
          />
          <span
            aria-hidden="true"
            style={{ backgroundColor: currentColor ?? "transparent" }}
          />
        </span>
        <span className="schema-workbench__visual-color-value">
          {currentColor ?? "No color"}
        </span>
        {currentColor ? (
          <Button
            size="sm"
            variant="ghost"
            onClick={() => onChange(null)}
            aria-label="Clear table color"
          >
            Clear
          </Button>
        ) : null}
      </div>
      <span className="schema-workbench__visual-color-hint">
        Only changes how the table looks in the designer.
      </span>
    </div>
  );
}

export function VisualSchemaInspector({
  baselineDocument,
  document,
  selection,
  onDocumentChange,
  onSelectionChange,
}: VisualSchemaInspectorProps) {
  const [relationDraft, setRelationDraft] =
    useState<VisualRelationDraft | null>(null);
  const [relationDraftIssues, setRelationDraftIssues] = useState<
    VisualSchemaDraftIssue[]
  >([]);

  function commit(nextDocument: SchemaModelDocument) {
    onDocumentChange(nextDocument);
  }

  if (!selection) {
    return (
      <aside
        className="schema-workbench__visual-inspector"
        aria-label="Visual schema inspector"
      >
        <EmptyState
          title="Select a table, column, or relation"
          description="Click a table, column or relation on the canvas to edit it."
          icon="pencil"
        />
      </aside>
    );
  }

  if (selection.type === "relation") {
    const relation = document.relations.find(
      (candidate) => candidate.name === selection.relationName,
    );

    return (
      <aside
        className="schema-workbench__visual-inspector"
        aria-label="Visual schema inspector"
      >
        {relation ? (
          <VisualRelationInspector
            baselineDocument={baselineDocument}
            document={document}
            issues={relationDraftIssues}
            relation={relation}
            onChange={(nextDocument) => {
              commit(nextDocument);
              setRelationDraftIssues([]);
            }}
            onIssues={setRelationDraftIssues}
            onSelectionChange={onSelectionChange}
          />
        ) : null}
      </aside>
    );
  }

  const selectedTable = document.tables.find(
    (candidate) => candidate.name === selection.tableName,
  );

  if (!selectedTable) {
    return (
      <aside
        className="schema-workbench__visual-inspector"
        aria-label="Visual schema inspector"
      >
        <EmptyState
          title="Selection no longer exists"
          description="Choose another table or column from the canvas."
          icon="schema"
        />
      </aside>
    );
  }

  const activeTable = selectedTable;
  const selectedColumn =
    selection.type === "column"
      ? (activeTable.columns.find(
          (candidate) => candidate.name === selection.columnName,
        ) ?? null)
      : null;

  function updateSelectedTable(update: Partial<SchemaTable>) {
    if (typeof update.name === "string" && update.name !== activeTable.name) {
      const baselineHasTable = Boolean(
        baselineDocument?.tables.some(
          (table) => table.name === activeTable.name,
        ),
      );
      const renameHint =
        activeTable.renamedFrom ??
        (baselineHasTable ? activeTable.name : undefined);
      const nextRenameHint =
        renameHint && renameHint !== update.name ? renameHint : null;

      commit(
        renameTable(document, activeTable.name, update.name, nextRenameHint),
      );
      onSelectionChange({ type: "table", tableName: update.name });
      return;
    }

    const nextName =
      typeof update.name === "string" ? update.name : activeTable.name;
    commit(updateTable(document, activeTable.name, update));
    onSelectionChange({ type: "table", tableName: nextName });
  }

  function duplicateSelectedTable() {
    const { document: nextDocument, tableName } = duplicateTable(
      document,
      activeTable.name,
    );
    commit(nextDocument);
    onSelectionChange({ type: "table", tableName });
  }

  function deleteSelectedTable() {
    const impact = getTableDeleteImpact(document, activeTable.name);
    const details = [
      `Delete table "${activeTable.name}"?`,
      impact.relationCount > 0
        ? `${countLabel(impact.relationCount, "relation")} will be removed.`
        : null,
      impact.referenceColumnCount > 0
        ? `${countLabel(impact.referenceColumnCount, "reference column")} will be removed.`
        : null,
    ]
      .filter(Boolean)
      .join(" ");

    const confirmed = window.confirm(details);
    if (!confirmed) {
      return;
    }

    commit(deleteTableWithDependents(document, activeTable.name));
    onSelectionChange(null);
  }

  function updateSelectedColumn(
    column: SchemaColumn,
    update: Partial<SchemaColumn> | ((current: SchemaColumn) => SchemaColumn),
  ) {
    const nextDocument = updateColumn(
      document,
      activeTable.name,
      column.name,
      update,
    );
    const nextColumn =
      typeof update === "function" ? update(column) : { ...column, ...update };
    commit(nextDocument);
    onSelectionChange({
      type: "column",
      tableName: activeTable.name,
      columnName: nextColumn.name,
    });
  }

  function addNewColumn() {
    const columnName = getUniqueVisualName(
      activeTable.columns.map((column) => column.name),
      "new_column",
    );
    const nextColumn: SchemaColumn = {
      name: columnName,
      type: "text",
      nullable: true,
      renamedFrom: columnName,
    };

    commit(addColumn(document, activeTable.name, nextColumn));
    onSelectionChange({
      type: "column",
      tableName: activeTable.name,
      columnName,
    });
  }

  function moveSelectedColumn(column: SchemaColumn, direction: -1 | 1) {
    const currentIndex = activeTable.columns.findIndex(
      (candidate) => candidate.name === column.name,
    );
    if (currentIndex === -1) {
      return;
    }

    commit(
      reorderColumn(
        document,
        activeTable.name,
        column.name,
        currentIndex + direction,
      ),
    );
  }

  function deleteSelectedColumn(column: SchemaColumn) {
    const confirmed = window.confirm(
      `Delete column "${activeTable.name}.${column.name}"?`,
    );
    if (!confirmed) {
      return;
    }

    commit(removeColumn(document, activeTable.name, column.name));
    onSelectionChange({ type: "table", tableName: activeTable.name });
  }

  const activeRelationDraft =
    relationDraft?.fromTableName === activeTable.name ? relationDraft : null;
  // A junction table belongs to the N:M relation it stores the links for.
  const activeTableRelations = document.relations.filter(
    (relation) =>
      relation.from === activeTable.name ||
      relation.to === activeTable.name ||
      (relation.type === "N:M" && relation.junctionTable === activeTable.name),
  );

  function startRelationDraft() {
    const defaultToTable =
      document.tables.find((table) => table.name !== activeTable.name)?.name ??
      activeTable.name;
    setRelationDraft(
      createVisualRelationDraft(document, activeTable.name, defaultToTable),
    );
    setRelationDraftIssues([]);
  }

  function submitRelationDraft(draft: VisualRelationDraft) {
    const junctionLayout =
      draft.relationType === "N:M"
        ? getSuggestedJunctionLayout(
            document,
            draft.fromTableName,
            draft.toTableName,
          )
        : null;
    const result =
      draft.relationType === "N:M"
        ? createManyToManyRelation(document, {
            fromTableName: draft.fromTableName,
            toTableName: draft.toTableName,
            relationName: draft.relationName,
            junctionTableName: draft.junctionTableName,
            fromColumnName: draft.fromColumnName,
            toColumnName: draft.toColumnName,
            fromAccessor: draft.fromAccessor,
            toAccessor: draft.toAccessor,
            layout: junctionLayout,
          })
        : createOneToManyRelation(document, {
            fromTableName: draft.fromTableName,
            toTableName: draft.toTableName,
            relationName: draft.relationName,
            fromAccessor: draft.fromAccessor,
            toAccessor: draft.toAccessor,
            indexName: draft.indexName,
            columnName: draft.columnMode === "new" ? draft.columnName : null,
            reuseColumnName:
              draft.columnMode === "existing" ? draft.columnName : null,
          });

    if (result.issues.length > 0 || !result.relationName) {
      setRelationDraftIssues(result.issues);
      return;
    }

    commit(result.document);
    setRelationDraft(null);
    setRelationDraftIssues([]);
    onSelectionChange({ type: "relation", relationName: result.relationName });
  }

  return (
    <aside
      className="schema-workbench__visual-inspector"
      aria-label="Visual schema inspector"
    >
      {selectedColumn ? (
        <VisualColumnInspector
          column={selectedColumn}
          document={document}
          table={activeTable}
          onBackToTable={() =>
            onSelectionChange({ type: "table", tableName: activeTable.name })
          }
          onChange={(update) => updateSelectedColumn(selectedColumn, update)}
          onDelete={() => deleteSelectedColumn(selectedColumn)}
          onMoveDown={() => moveSelectedColumn(selectedColumn, 1)}
          onMoveUp={() => moveSelectedColumn(selectedColumn, -1)}
        />
      ) : (
        <>
          <section className="schema-workbench__visual-inspector-section">
            <h3>Table</h3>
            <TextField
              underline
              label="Table name"
              value={activeTable.name}
              onChange={(event) =>
                updateSelectedTable({ name: event.currentTarget.value })
              }
            />
            <TextField
              underline
              label="Table display name"
              value={activeTable.displayName ?? ""}
              onChange={(event) =>
                updateSelectedTable({
                  displayName: optionalVisualText(event.currentTarget.value),
                })
              }
            />
            {/* Renames are tracked automatically; the engine field stays
                reachable for the rare manual case. */}
            <details className="schema-workbench__advanced">
              <summary>Advanced</summary>
              <TextField
                underline
                label="Previous name"
                message="Set this when renaming so existing data is kept."
                value={activeTable.renamedFrom ?? ""}
                onChange={(event) =>
                  updateSelectedTable({
                    renamedFrom: optionalVisualText(event.currentTarget.value),
                  })
                }
              />
            </details>
            <VisualTableColorField
              table={activeTable}
              onChange={(nextColor) => {
                const layout = getTableLayout(activeTable);
                const fallback = getNextVisualTableLayout(document);
                commit(
                  setTableLayout(document, activeTable.name, {
                    x: layout?.x ?? fallback.x,
                    y: layout?.y ?? fallback.y,
                    color: nextColor,
                  }),
                );
              }}
            />
            <div className="schema-workbench__visual-inspector-actions schema-workbench__visual-inspector-actions--wrap">
              <Button
                size="sm"
                leadingIcon={<Icon name="copy" size={14} />}
                onClick={duplicateSelectedTable}
              >
                Duplicate table
              </Button>
              <Button
                size="sm"
                variant="danger"
                leadingIcon={<Icon name="trash" size={14} />}
                onClick={deleteSelectedTable}
              >
                Delete table
              </Button>
            </div>
          </section>
          <section className="schema-workbench__visual-inspector-section">
            <div className="schema-workbench__visual-inspector-subhead">
              <h3>Columns</h3>
              <Button
                size="sm"
                leadingIcon={<Icon name="plus" size={14} />}
                onClick={addNewColumn}
              >
                Add column
              </Button>
            </div>
            <ol className="schema-workbench__visual-column-picker">
              {activeTable.columns.map((column) => (
                <li key={column.name}>
                  <button
                    type="button"
                    aria-label={`Edit column ${column.name}`}
                    onClick={() =>
                      onSelectionChange({
                        type: "column",
                        tableName: activeTable.name,
                        columnName: column.name,
                      })
                    }
                  >
                    <span>{column.name}</span>
                    <code>{formatMiniErdTypeValue(column.type)}</code>
                  </button>
                </li>
              ))}
            </ol>
          </section>
          <section className="schema-workbench__visual-inspector-section">
            <div className="schema-workbench__visual-inspector-subhead">
              <h3>Relations</h3>
              <Button
                size="sm"
                leadingIcon={<Icon name="plus" size={14} />}
                onClick={startRelationDraft}
              >
                Add relation
              </Button>
            </div>
            {activeRelationDraft ? (
              <VisualRelationCreator
                document={document}
                draft={activeRelationDraft}
                issues={relationDraftIssues}
                onCancel={() => {
                  setRelationDraft(null);
                  setRelationDraftIssues([]);
                }}
                onChange={(nextDraft) => {
                  setRelationDraft(nextDraft);
                  setRelationDraftIssues([]);
                }}
                onCreate={() => submitRelationDraft(activeRelationDraft)}
              />
            ) : activeTableRelations.length > 0 ? (
              <ol className="schema-workbench__visual-column-picker">
                {activeTableRelations.map((relation) => (
                  <li key={relation.name}>
                    <button
                      type="button"
                      aria-label={`Edit relation ${relation.name}`}
                      onClick={() =>
                        onSelectionChange({
                          type: "relation",
                          relationName: relation.name,
                        })
                      }
                    >
                      <span>
                        {relation.type === "N:M" &&
                        relation.junctionTable === activeTable.name
                          ? `${relation.name} (links ${relation.from} and ${relation.to})`
                          : `${relation.name} (${
                              relation.type === "N:M" ? "many-to-many" : "one-to-many"
                            })`}
                      </span>
                    </button>
                  </li>
                ))}
              </ol>
            ) : (
              <p className="schema-workbench__visual-muted">
                No relations attached to this table.
              </p>
            )}
          </section>
        </>
      )}
    </aside>
  );
}

type VisualColumnInspectorProps = {
  column: SchemaColumn;
  document: SchemaModelDocument;
  table: SchemaTable;
  onBackToTable: () => void;
  onChange: (
    update: Partial<SchemaColumn> | ((current: SchemaColumn) => SchemaColumn),
  ) => void;
  onDelete: () => void;
  onMoveDown: () => void;
  onMoveUp: () => void;
};

function VisualColumnInspector({
  column,
  document,
  table,
  onBackToTable,
  onChange,
  onDelete,
  onMoveDown,
  onMoveUp,
}: VisualColumnInspectorProps) {
  const columnIndex = table.columns.findIndex(
    (candidate) => candidate.name === column.name,
  );

  return (
    <section className="schema-workbench__visual-inspector-section">
      <div className="schema-workbench__visual-inspector-subhead">
        <div className="schema-workbench__visual-inspector-title-group">
          <Button
            size="sm"
            variant="ghost"
            leadingIcon={<Icon name="chevron-left" size={14} />}
            onClick={onBackToTable}
          >
            Table columns
          </Button>
          <h3>Column</h3>
        </div>
        <div className="schema-workbench__visual-inspector-actions">
          <Button
            size="icon"
            aria-label="Move column up"
            title="Move column up"
            disabled={columnIndex <= 0}
            onClick={onMoveUp}
          >
            <Icon name="chevron-up" size={14} />
          </Button>
          <Button
            size="icon"
            aria-label="Move column down"
            title="Move column down"
            disabled={
              columnIndex === -1 || columnIndex >= table.columns.length - 1
            }
            onClick={onMoveDown}
          >
            <Icon name="chevron-down" size={14} />
          </Button>
          <Button
            size="icon"
            variant="danger"
            aria-label="Delete column"
            title="Delete column"
            onClick={onDelete}
          >
            <Icon name="trash" size={14} />
          </Button>
        </div>
      </div>
      <TextField
        underline
        label="Column name"
        value={column.name}
        onChange={(event) => onChange({ name: event.currentTarget.value })}
      />
      <TextField
        underline
        label="Column display name"
        value={column.displayName ?? ""}
        onChange={(event) =>
          onChange({
            displayName: optionalVisualText(event.currentTarget.value),
          })
        }
      />
      <SelectField
        underline
        label="Column type"
        options={visualColumnTypeOptions}
        value={column.type}
        onChange={(event) =>
          onChange((current) =>
            normalizeVisualColumnType(current, event.currentTarget.value),
          )
        }
      />
      {column.type === "reference" ? (
        <SelectField
          underline
          label="Reference target"
          options={[
            { label: "Select table", value: "" },
            ...document.tables.map((candidate) => ({
              label: candidate.name,
              value: candidate.name,
            })),
          ]}
          value={typeof column.references === "string" ? column.references : ""}
          onChange={(event) =>
            onChange({
              references: event.currentTarget.value || null,
            })
          }
        />
      ) : null}
      <div className="schema-workbench__visual-checkbox-grid">
        <VisualCheckbox
          label="Primary key"
          checked={Boolean(column.primaryKey)}
          onChange={(checked) => onChange({ primaryKey: checked || undefined })}
        />
        <VisualCheckbox
          label="Required"
          checked={column.nullable === false}
          onChange={(checked) => onChange({ nullable: checked ? false : true })}
        />
        <VisualCheckbox
          label="Unique"
          checked={Boolean(column.unique)}
          onChange={(checked) => onChange({ unique: checked || undefined })}
        />
      </div>
      <TextField
        underline
        label="Default value"
        value={stringifyVisualDefault(column.default)}
        onChange={(event) =>
          onChange((current) => ({
            ...current,
            default: optionalVisualText(event.currentTarget.value),
          }))
        }
      />
      <details className="schema-workbench__advanced">
        <summary>Advanced</summary>
        <TextField
          underline
          label="Previous name"
          message="Set this when renaming so existing data is kept."
          value={column.renamedFrom ?? ""}
          onChange={(event) =>
            onChange({
              renamedFrom: optionalVisualText(event.currentTarget.value),
            })
          }
        />
      </details>
      {column.type === "enum" ? (
        <TextField
          underline
          label="Enum values"
          value={(column.values ?? []).join(", ")}
          message="Comma-separated values"
          onChange={(event) =>
            onChange({
              values: parseVisualEnumValues(event.currentTarget.value),
            })
          }
        />
      ) : null}
    </section>
  );
}

type VisualCheckboxProps = {
  checked: boolean;
  label: string;
  onChange: (checked: boolean) => void;
};

function VisualCheckbox({ checked, label, onChange }: VisualCheckboxProps) {
  return (
    <label className="schema-workbench__visual-checkbox">
      <input
        type="checkbox"
        checked={checked}
        onChange={(event) => onChange(event.currentTarget.checked)}
      />
      <span>{label}</span>
    </label>
  );
}

function optionalVisualText(value: string): string | undefined {
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : undefined;
}

function parseVisualEnumValues(value: string): string[] {
  return value
    .split(",")
    .map((item) => item.trim())
    .filter(Boolean);
}

function stringifyVisualDefault(value: unknown): string {
  if (value === undefined || value === null) {
    return "";
  }

  return typeof value === "string" ? value : JSON.stringify(value);
}

function normalizeVisualColumnType(
  column: SchemaColumn,
  typeValue: string,
): SchemaColumn {
  const nextType = typeValue as SchemaColumnType;
  const nextColumn: SchemaColumn = {
    ...column,
    type: nextType,
  };

  if (nextType !== "enum") {
    delete nextColumn.values;
  } else if (!Array.isArray(nextColumn.values)) {
    nextColumn.values = [];
  }

  if (nextType !== "reference") {
    delete nextColumn.references;
  } else {
    nextColumn.references =
      typeof nextColumn.references === "string" ? nextColumn.references : null;
  }

  return nextColumn;
}
