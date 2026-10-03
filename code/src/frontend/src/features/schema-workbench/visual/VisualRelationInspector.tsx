import { useEffect, useState } from "react";
import { Icon } from "../../../components/common/Icon";
import { Button } from "../../../components/ui/Button";
import { SelectField } from "../../../components/ui/SelectField";
import { TextField } from "../../../components/ui/TextField";
import type { SchemaModelDocument, SchemaRelation } from "../../../types/api";
import type { VisualCanvasSelection } from "../types";
import {
  convertManyToManyRelationToOneToMany,
  convertOneToManyRelationToManyToMany,
  deleteRelationWithArtifacts,
  getDefaultOneToManyRelationDraft,
  renameRelation,
  switchManyToManyRelationDirection,
  switchOneToManyRelationDirection,
  updateManyToManyRelation,
  updateOneToManyRelation,
  type VisualSchemaDraftIssue,
} from "../visualSchemaDraft";
import { getSuggestedJunctionLayout } from "./erdRouting";

type VisualRelationInspectorProps = {
  baselineDocument: SchemaModelDocument | null;
  document: SchemaModelDocument;
  issues: VisualSchemaDraftIssue[];
  relation: SchemaRelation;
  onChange: (document: SchemaModelDocument) => void;
  onIssues: (issues: VisualSchemaDraftIssue[]) => void;
  onSelectionChange: (selection: VisualCanvasSelection | null) => void;
};

export function VisualRelationInspector({
  baselineDocument,
  document,
  issues,
  relation,
  onChange,
  onIssues,
  onSelectionChange,
}: VisualRelationInspectorProps) {
  const [pendingName, setPendingName] = useState(relation.name);

  useEffect(() => {
    setPendingName(relation.name);
  }, [relation.name]);

  const tableOptions = document.tables.map((table) => ({
    label: table.name,
    value: table.name,
  }));
  const relationTypeOptions =
    [
      { label: "One-to-many", value: "1:N" },
      { label: "Many-to-many", value: "N:M" },
    ];

  function commitRename() {
    const trimmed = pendingName.trim();
    if (trimmed === relation.name) {
      setPendingName(relation.name);
      return;
    }

    const result = renameRelation(document, relation.name, trimmed);
    if (result.issues.length > 0 || !result.relationName) {
      onIssues(result.issues);
      setPendingName(relation.name);
      return;
    }

    onChange(result.document);
    onSelectionChange({ type: "relation", relationName: result.relationName });
  }

  function commitOneToMany(update: {
    columnName?: string | null;
    fromAccessor?: string | null;
    fromTableName?: string;
    toAccessor?: string | null;
    toTableName?: string;
  }) {
    const result = updateOneToManyRelation(document, {
      baselineDocument,
      relationName: relation.name,
      fromTableName: update.fromTableName ?? relation.from,
      toTableName: update.toTableName ?? relation.to,
      columnName: update.columnName ?? relation.column ?? null,
      fromAccessor: update.fromAccessor ?? relation.fromAccessor ?? null,
      toAccessor: update.toAccessor ?? relation.toAccessor ?? null,
    });

    if (result.issues.length > 0 || !result.relationName) {
      onIssues(result.issues);
      return;
    }

    onChange(result.document);
    onSelectionChange({ type: "relation", relationName: result.relationName });
  }

  function commitManyToMany(update: {
    fromAccessor?: string | null;
    fromColumnName?: string | null;
    fromTableName?: string;
    junctionTableName?: string | null;
    toAccessor?: string | null;
    toColumnName?: string | null;
    toTableName?: string;
  }) {
    const result = updateManyToManyRelation(document, {
      relationName: relation.name,
      fromTableName: update.fromTableName ?? relation.from,
      toTableName: update.toTableName ?? relation.to,
      fromColumnName: update.fromColumnName ?? relation.fromColumn ?? null,
      toColumnName: update.toColumnName ?? relation.toColumn ?? null,
      junctionTableName:
        update.junctionTableName ??
        relation.junctionTable ??
        relation.resolvedJunctionTableName ??
        null,
      fromAccessor: update.fromAccessor ?? relation.fromAccessor ?? null,
      toAccessor: update.toAccessor ?? relation.toAccessor ?? null,
    });

    if (result.issues.length > 0 || !result.relationName) {
      onIssues(result.issues);
      return;
    }

    onChange(result.document);
    onSelectionChange({ type: "relation", relationName: result.relationName });
  }

  function commitEndpoint(update: {
    fromTableName?: string;
    toTableName?: string;
  }) {
    const fromTableName = update.fromTableName ?? relation.from;
    const toTableName = update.toTableName ?? relation.to;

    if (relation.type === "1:N") {
      const defaults = getDefaultOneToManyRelationDraft(
        document,
        fromTableName,
        toTableName,
      );
      commitOneToMany({
        fromTableName,
        toTableName,
        columnName: defaults.columnName,
        fromAccessor: defaults.fromAccessor,
        toAccessor: defaults.toAccessor,
      });
      return;
    }

    commitManyToMany({ fromTableName, toTableName });
  }

  function switchDirection() {
    if (relation.type === "1:N") {
      const result = switchOneToManyRelationDirection(
        document,
        relation.name,
        baselineDocument,
      );
      if (result.issues.length > 0 || !result.relationName) {
        onIssues(result.issues);
        return;
      }

      onChange(result.document);
      onSelectionChange({
        type: "relation",
        relationName: result.relationName,
      });
      return;
    }

    const result = switchManyToManyRelationDirection(document, relation.name);
    if (result.issues.length > 0 || !result.relationName) {
      onIssues(result.issues);
      return;
    }

    onChange(result.document);
    onSelectionChange({ type: "relation", relationName: result.relationName });
  }

  function updateRelationType(nextType: "1:N" | "N:M") {
    if (nextType === relation.type) {
      return;
    }

    const result =
      relation.type === "1:N" && nextType === "N:M"
        ? convertOneToManyRelationToManyToMany(document, {
            relationName: relation.name,
            layout: getSuggestedJunctionLayout(document, relation.from, relation.to),
          })
        : relation.type === "N:M" && nextType === "1:N"
          ? convertManyToManyRelationToOneToMany(document, {
              relationName: relation.name,
            })
          : null;

    if (!result) {
      return;
    }

    if (result.issues.length > 0 || !result.relationName) {
      onIssues(result.issues);
      return;
    }

    onChange(result.document);
    onSelectionChange({ type: "relation", relationName: result.relationName });
  }

  function deleteRelation() {
    const result = deleteRelationWithArtifacts(document, relation.name);
    if (result.issues.length > 0) {
      onIssues(result.issues);
      return;
    }

    onChange(result.document);
    onSelectionChange(null);
  }

  const advancedRelationFields = getAdvancedRelationFields(relation);

  return (
    <section
      className="schema-workbench__visual-inspector-section"
      data-testid="visual-relation-inspector"
    >
      <header className="schema-workbench__visual-relation-summary-header">
        <span className="schema-workbench__visual-relation-summary-type">
          {relation.type === "N:M" ? "Many-to-many" : "One-to-many"}
        </span>
        <span className="schema-workbench__visual-relation-summary-arrow">
          {`${relation.from} → ${relation.to}`}
        </span>
      </header>
      <TextField
        underline
        label="Relation name"
        value={pendingName}
        onBlur={commitRename}
        onChange={(event) => setPendingName(event.currentTarget.value)}
        onKeyDown={(event) => {
          if (event.key === "Enter") {
            event.preventDefault();
            (event.currentTarget as HTMLInputElement).blur();
          }
        }}
      />
      <SelectField
        underline
        label="Relation type"
        options={relationTypeOptions}
        value={relation.type}
        onChange={(event) =>
          updateRelationType(event.currentTarget.value as "1:N" | "N:M")
        }
      />
      <SelectField
        underline
        label="From"
        options={tableOptions}
        value={relation.from}
        onChange={(event) =>
          commitEndpoint({ fromTableName: event.currentTarget.value })
        }
      />
      <SelectField
        underline
        label="To"
        options={tableOptions}
        value={relation.to}
        onChange={(event) =>
          commitEndpoint({ toTableName: event.currentTarget.value })
        }
      />
      <Button
        size="sm"
        leadingIcon={<Icon name="arrow-up-down" size={14} />}
        onClick={switchDirection}
      >
        Switch direction
      </Button>
      <Button
        size="sm"
        variant="danger"
        leadingIcon={<Icon name="trash" size={14} />}
        onClick={deleteRelation}
      >
        Delete relation
      </Button>
      {relation.type === "1:N" ? (
        <TextField
          underline
          label="Reference column"
          value={relation.column ?? ""}
          onChange={(event) =>
            commitOneToMany({ columnName: event.currentTarget.value })
          }
        />
      ) : (
        <>
          <TextField
            underline
            label="Junction table"
            value={relation.junctionTable ?? relation.resolvedJunctionTableName ?? ""}
            onChange={(event) =>
              commitManyToMany({
                junctionTableName: event.currentTarget.value,
              })
            }
          />
          <TextField
            underline
            label="Source column"
            value={relation.fromColumn ?? ""}
            onChange={(event) =>
              commitManyToMany({
                fromColumnName: event.currentTarget.value,
              })
            }
          />
          <TextField
            underline
            label="Target column"
            value={relation.toColumn ?? ""}
            onChange={(event) =>
              commitManyToMany({
                toColumnName: event.currentTarget.value,
              })
            }
          />
        </>
      )}
      <TextField
        underline
        label="Name on parent records"
        value={relation.fromAccessor ?? ""}
        placeholder={relation.resolvedFromAccessor ?? undefined}
        onChange={(event) =>
          relation.type === "1:N"
            ? commitOneToMany({ fromAccessor: event.currentTarget.value })
            : commitManyToMany({ fromAccessor: event.currentTarget.value })
        }
      />
      <TextField
        underline
        label="Name on child records"
        value={relation.toAccessor ?? ""}
        placeholder={relation.resolvedToAccessor ?? undefined}
        onChange={(event) =>
          relation.type === "1:N"
            ? commitOneToMany({ toAccessor: event.currentTarget.value })
            : commitManyToMany({ toAccessor: event.currentTarget.value })
        }
      />
      {issues.length > 0 ? (
        <ol
          className="schema-workbench__visual-relation-errors"
          data-testid="visual-relation-inspector-errors"
        >
          {issues.map((issue) => (
            <li key={`${issue.code}:${issue.path}`}>
              <span>{issue.message}</span>
            </li>
          ))}
        </ol>
      ) : null}
      {advancedRelationFields.length > 0 ? (
        <details
          className="schema-workbench__visual-relation-advanced"
          data-testid="visual-relation-advanced"
        >
          <summary>Advanced</summary>
          <p className="schema-workbench__visual-muted">
            Edit these in JSON mode.
          </p>
          <dl>
            {advancedRelationFields.map((field) => (
              <div key={field.key}>
                <dt>
                  <code>{field.key}</code>
                </dt>
                <dd>
                  <code>{field.value}</code>
                </dd>
              </div>
            ))}
          </dl>
        </details>
      ) : null}
    </section>
  );
}

const knownRelationFieldKeys = new Set([
  "name",
  "type",
  "from",
  "to",
  "fromAccessor",
  "toAccessor",
  "column",
  "fromColumn",
  "toColumn",
  "junctionTable",
  "resolvedFromAccessor",
  "resolvedToAccessor",
  "resolvedJunctionTableName",
]);

function getAdvancedRelationFields(
  relation: SchemaRelation,
): Array<{ key: string; value: string }> {
  const result: Array<{ key: string; value: string }> = [];
  for (const [key, value] of Object.entries(relation)) {
    if (knownRelationFieldKeys.has(key)) {
      continue;
    }
    if (value === undefined || value === null) {
      continue;
    }
    let formatted: string;
    try {
      formatted =
        typeof value === "string" ? value : JSON.stringify(value);
    } catch {
      formatted = String(value);
    }
    result.push({ key, value: formatted });
  }
  return result;
}
