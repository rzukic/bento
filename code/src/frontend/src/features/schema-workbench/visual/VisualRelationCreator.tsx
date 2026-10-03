import { Button } from "../../../components/ui/Button";
import { SelectField } from "../../../components/ui/SelectField";
import { TextField } from "../../../components/ui/TextField";
import type { SchemaModelDocument } from "../../../types/api";
import {
  getDefaultManyToManyRelationDraft,
  getDefaultOneToManyRelationDraft,
  type VisualSchemaDraftIssue,
} from "../visualSchemaDraft";
import {
  createVisualRelationDraft,
  getUniqueVisualName,
  type VisualRelationDraft,
} from "./visualDraftHelpers";

function getReusableRelationColumnOptions(
  document: SchemaModelDocument,
  fromTableName: string,
  toTableName: string,
) {
  const toTable = document.tables.find((table) => table.name === toTableName);
  return (
    toTable?.columns.filter(
      (column) =>
        column.type === "reference" && column.references === fromTableName,
    ) ?? []
  );
}

type VisualRelationCreatorProps = {
  document: SchemaModelDocument;
  draft: VisualRelationDraft;
  issues: VisualSchemaDraftIssue[];
  onCancel: () => void;
  onChange: (draft: VisualRelationDraft) => void;
  onCreate: () => void;
};

export function VisualRelationCreator({
  document,
  draft,
  issues,
  onCancel,
  onChange,
  onCreate,
}: VisualRelationCreatorProps) {
  const tableOptions = document.tables.map((table) => ({
    label: table.name,
    value: table.name,
  }));
  const reusableColumns = getReusableRelationColumnOptions(
    document,
    draft.fromTableName,
    draft.toTableName,
  );
  const columnOptions = [
    { label: "Create new reference column", value: "__new__" },
    ...reusableColumns.map((column) => ({
      label: column.name,
      value: column.name,
    })),
  ];

  function updateToTable(toTableName: string) {
    const nextDraft = createVisualRelationDraft(
      document,
      draft.fromTableName,
      toTableName,
    );
    if (draft.relationType === "N:M") {
      const manyDefaults = getDefaultManyToManyRelationDraft(
        document,
        draft.fromTableName,
        toTableName,
      );
      onChange({
        ...nextDraft,
        relationType: "N:M",
        ...manyDefaults,
      });
      return;
    }

    onChange(nextDraft);
  }

  function updateRelationType(relationType: "1:N" | "N:M") {
    if (relationType === draft.relationType) {
      return;
    }

    const nextDraft = createVisualRelationDraft(
      document,
      draft.fromTableName,
      draft.toTableName,
    );
    if (relationType === "N:M") {
      const manyDefaults = getDefaultManyToManyRelationDraft(
        document,
        draft.fromTableName,
        draft.toTableName,
      );
      onChange({
        ...nextDraft,
        relationType,
        ...manyDefaults,
      });
      return;
    }

    onChange(nextDraft);
  }

  function updateColumnMode(value: string) {
    if (value === "__new__") {
      const defaults = getDefaultOneToManyRelationDraft(
        document,
        draft.fromTableName,
        draft.toTableName,
      );
      onChange({
        ...draft,
        columnMode: "new",
        columnName: defaults.columnName,
        indexName: defaults.indexName,
      });
      return;
    }

    const indexName = getUniqueVisualName(
      document.tables
        .find((table) => table.name === draft.toTableName)
        ?.indexes?.map((index) => index.name) ?? [],
      `idx_${draft.toTableName}_${value}`,
    );
    onChange({
      ...draft,
      columnMode: "existing",
      columnName: value,
      indexName,
    });
  }

  return (
    <div className="schema-workbench__visual-relation-creator">
      <SelectField
        label="Relation type"
        options={[
          { label: "One-to-many", value: "1:N" },
          { label: "Many-to-many", value: "N:M" },
        ]}
        value={draft.relationType}
        onChange={(event) =>
          updateRelationType(event.currentTarget.value as "1:N" | "N:M")
        }
      />
      <SelectField
        label={draft.relationType === "N:M" ? "Target table" : "Child table"}
        options={tableOptions}
        value={draft.toTableName}
        onChange={(event) => updateToTable(event.currentTarget.value)}
      />
      <TextField
        label="Relation name"
        value={draft.relationName}
        onChange={(event) =>
          onChange({ ...draft, relationName: event.currentTarget.value })
        }
      />
      {draft.relationType === "N:M" ? (
        <>
          <TextField
            label="Junction table"
            value={draft.junctionTableName}
            onChange={(event) =>
              onChange({
                ...draft,
                junctionTableName: event.currentTarget.value,
              })
            }
          />
          <TextField
            label="Source column"
            value={draft.fromColumnName}
            onChange={(event) =>
              onChange({ ...draft, fromColumnName: event.currentTarget.value })
            }
          />
          <TextField
            label="Target column"
            value={draft.toColumnName}
            onChange={(event) =>
              onChange({ ...draft, toColumnName: event.currentTarget.value })
            }
          />
        </>
      ) : (
        <>
          <SelectField
            label="Reference column"
            options={columnOptions}
            value={draft.columnMode === "new" ? "__new__" : draft.columnName}
            onChange={(event) => updateColumnMode(event.currentTarget.value)}
          />
          <TextField
            label="Column name"
            disabled={draft.columnMode === "existing"}
            value={draft.columnName}
            onChange={(event) =>
              onChange({ ...draft, columnName: event.currentTarget.value })
            }
          />
        </>
      )}
      <TextField
        label="Name on parent records"
        value={draft.fromAccessor}
        onChange={(event) =>
          onChange({ ...draft, fromAccessor: event.currentTarget.value })
        }
      />
      <TextField
        label="Name on child records"
        value={draft.toAccessor}
        onChange={(event) =>
          onChange({ ...draft, toAccessor: event.currentTarget.value })
        }
      />
      {draft.relationType === "1:N" ? (
        <TextField
          label="Index name"
          value={draft.indexName}
          onChange={(event) =>
            onChange({ ...draft, indexName: event.currentTarget.value })
          }
        />
      ) : null}
      {issues.length > 0 ? (
        <ol className="schema-workbench__visual-relation-errors">
          {issues.map((issue) => (
            <li key={`${issue.code}:${issue.path}`}>
              <span>{issue.message}</span>
            </li>
          ))}
        </ol>
      ) : null}
      <div className="schema-workbench__visual-inspector-actions">
        <Button size="sm" variant="primary" onClick={onCreate}>
          Create relation
        </Button>
        <Button size="sm" onClick={onCancel}>
          Cancel
        </Button>
      </div>
    </div>
  );
}
