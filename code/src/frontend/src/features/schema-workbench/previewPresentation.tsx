import { Icon } from "../../components/common/Icon";
import { EmptyState } from "../../components/ui/EmptyState";
import type {
  SchemaCastExample,
  SchemaChange,
  SchemaChangeImpact,
  SchemaPreviewResponse,
} from "../../types/api";
import { countLabel, formatPreviewValue } from "./schemaWorkbenchHelpers";
import type { PreviewChangeGroup, WorkbenchDiagnostic } from "./types";

export function buildPreviewGroups(changes: SchemaChange[]): PreviewChangeGroup[] {
  const groups: PreviewChangeGroup[] = [
    {
      id: "tables",
      title: "Tables",
      changes: changes.filter(
        (change) =>
          change.changeType === "AddTable" ||
          change.changeType === "DropTable" ||
          change.changeType === "RenameTable" ||
          change.changeType === "AlterTableMetadata",
      ),
    },
    {
      id: "columns",
      title: "Columns",
      changes: changes.filter(
        (change) =>
          change.changeType === "AddColumn" ||
          change.changeType === "DropColumn" ||
          change.changeType === "RenameColumn" ||
          change.changeType === "AlterColumnMetadata" ||
          change.changeType === "AlterColumnType" ||
          change.changeType === "AlterColumnNullability" ||
          change.changeType === "AlterColumnDefault" ||
          change.changeType === "AlterColumnUnique",
      ),
    },
    {
      id: "relations",
      title: "Relations",
      changes: changes.filter(
        (change) =>
          change.changeType === "AddRelation" ||
          change.changeType === "DropRelation" ||
          change.changeType === "AlterRelation",
      ),
    },
    {
      id: "indexes",
      title: "Indexes",
      changes: changes.filter(
        (change) =>
          change.changeType === "AddIndex" ||
          change.changeType === "DropIndex" ||
          change.changeType === "RenameIndex" ||
          change.changeType === "AlterIndex",
      ),
    },
  ];

  return groups.filter((group) => group.changes.length > 0);
}

function getQualifiedColumnName(change: SchemaChange): string {
  return change.column ? `${change.table}.${change.column}` : change.table;
}

function describePreviewChange(change: SchemaChange): {
  detail?: string;
  title: string;
} {
  switch (change.changeType) {
    case "AddTable":
      return { title: `Add table ${change.table}` };
    case "DropTable":
      return { title: `Drop table ${change.table}` };
    case "RenameTable":
      return {
        title: `Rename table ${change.previousTable ?? change.table} to ${change.table}`,
      };
    case "AlterTableMetadata":
      return { title: `Update table details for ${change.table}` };
    case "AddColumn":
      return { title: `Add column ${getQualifiedColumnName(change)}` };
    case "DropColumn":
      return { title: `Drop column ${getQualifiedColumnName(change)}` };
    case "RenameColumn":
      return {
        title: `Rename column ${change.table}.${change.previousColumn ?? change.column} to ${change.column ?? change.previousColumn}`,
      };
    case "AlterColumnMetadata":
      return {
        title: `Update column details for ${getQualifiedColumnName(change)}`,
      };
    case "AlterColumnType":
      return {
        title: `Change type of ${getQualifiedColumnName(change)} from ${change.oldValue} to ${change.newValue}`,
        detail:
          change.coercionKind === "Lossy"
            ? "Lossy conversion. Existing values may be truncated or rejected depending on provider behavior."
            : change.coercionKind
              ? `${change.coercionKind} conversion`
              : undefined,
      };
    case "AlterColumnNullability":
      return {
        title:
          change.newValue === false
            ? `Require values in ${getQualifiedColumnName(change)}`
            : `Allow null values in ${getQualifiedColumnName(change)}`,
      };
    case "AlterColumnDefault":
      return { title: `Change default for ${getQualifiedColumnName(change)}` };
    case "AlterColumnUnique":
      return {
        title:
          change.newValue === true
            ? `Make ${getQualifiedColumnName(change)} unique`
            : `Remove uniqueness from ${getQualifiedColumnName(change)}`,
      };
    case "AddRelation":
      return { title: `Add relation ${change.relation ?? change.table}` };
    case "DropRelation":
      return { title: `Drop relation ${change.relation ?? change.table}` };
    case "AlterRelation":
      return { title: `Change relation ${change.relation ?? change.table}` };
    case "AddIndex":
      return { title: `Add index ${change.index ?? change.table}` };
    case "DropIndex":
      return { title: `Drop index ${change.index ?? change.table}` };
    case "RenameIndex":
      return {
        title: `Rename index ${String(change.oldValue ?? change.index)} to ${String(change.newValue ?? change.index)}`,
      };
    case "AlterIndex":
      return { title: `Change index ${change.index ?? change.table}` };
    default:
      return { title: change.changeType };
  }
}

type PreviewChangeCardProps = {
  change: SchemaChange;
};

type PreviewSummaryProps = {
  errorCount: number;
  warningCount: number;
  isLayoutOnlyDraft?: boolean;
  response: SchemaPreviewResponse;
  groups: PreviewChangeGroup[];
};

export function PreviewSummary({
  errorCount,
  warningCount,
  isLayoutOnlyDraft = false,
  response,
  groups,
}: PreviewSummaryProps) {
  return (
    <div className="schema-workbench__preview-summary">
      <p className="schema-workbench__preview-counts">
        {countLabel(errorCount, "error")} {countLabel(warningCount, "warning")}{" "}
        {countLabel(response.changes.length, "change")}
      </p>

      {response.hasChanges ? (
        <div className="schema-workbench__preview-groups">
          {groups.map((group) => (
            <section
              key={group.id}
              className="schema-workbench__preview-group"
              aria-label={group.title}
            >
              <div className="schema-workbench__preview-group-heading">
                <h3>{group.title}</h3>
                <span className="schema-workbench__preview-group-count">
                  {countLabel(group.changes.length, "change")}
                </span>
              </div>

              <ol className="schema-workbench__preview-change-list">
                {group.changes.map((change, index) => (
                  <PreviewChangeCard
                    key={`${group.id}:${change.changeType}:${change.table}:${change.column ?? index}`}
                    change={change}
                  />
                ))}
              </ol>
            </section>
          ))}
        </div>
      ) : response.isValid ? (
        <div
          className="schema-workbench__preview-no-changes"
          data-testid={
            isLayoutOnlyDraft
              ? "schema-preview-layout-only"
              : "schema-preview-no-changes"
          }
        >
          {isLayoutOnlyDraft ? (
            <EmptyState
              title="Layout-only changes"
              description="No database migration is required. Apply will save the updated layout/metadata in place without bumping the schema version."
              icon="check"
              bullets={[
                "Migration plan is empty so no SQL will run",
                "Layout color, position, and other inert metadata will be persisted",
              ]}
            />
          ) : (
            <EmptyState
              title="No schema changes"
              description="Preview completed successfully. This draft matches the current schema, so there is nothing to apply."
              icon="check"
              bullets={[
                "Apply stays disabled until the preview contains schema changes",
              ]}
            />
          )}
        </div>
      ) : null}
    </div>
  );
}

function PreviewChangeCard({ change }: PreviewChangeCardProps) {
  const description = describePreviewChange(change);
  const isDropChange =
    change.changeType === "DropTable" ||
    change.changeType === "DropColumn" ||
    change.changeType === "DropRelation" ||
    change.changeType === "DropIndex";

  return (
    <li
      className={[
        "schema-workbench__preview-change",
        change.isDestructive && isDropChange
          ? "schema-workbench__preview-change--destructive schema-workbench__preview-change--drop-destructive"
          : "",
      ]
        .filter(Boolean)
        .join(" ")}
    >
      <div className="schema-workbench__preview-change-header">
        <div className="schema-workbench__preview-change-copy">
          <strong>{description.title}</strong>
          {description.detail ? (
            <p className="schema-workbench__preview-change-detail">
              {description.detail}
            </p>
          ) : null}
        </div>

        <div className="schema-workbench__preview-change-badges">
          {change.coercionKind ? (
            <span className="schema-workbench__preview-badge schema-workbench__preview-badge--muted">
              {change.coercionKind}
            </span>
          ) : null}
          {change.isDestructive ? (
            <span
              className={[
                "schema-workbench__preview-badge",
                isDropChange
                  ? "schema-workbench__preview-badge--destructive"
                  : "schema-workbench__preview-badge--warning",
              ].join(" ")}
            >
              Destructive
            </span>
          ) : null}
        </div>
      </div>

      {change.coercionKind === "Lossy" &&
      change.castExamples &&
      change.castExamples.length > 0 ? (
        <CastExamples examples={change.castExamples} />
      ) : null}
      {change.impact ? <RelationImpact impact={change.impact} /> : null}
    </li>
  );
}

type CastExamplesProps = {
  examples: SchemaCastExample[];
};

function CastExamples({ examples }: CastExamplesProps) {
  return (
    <div className="schema-workbench__cast-examples">
      <h4>Example Casts (first 5 rows)</h4>
      <ol className="schema-workbench__cast-example-list">
        {examples.map((example, index) => (
          <li
            key={`${formatPreviewValue(example.sourceValue)}:${formatPreviewValue(example.castValue)}:${index}`}
            className="schema-workbench__cast-example"
          >
            <code className="mono">
              {formatPreviewValue(example.sourceValue)}
              {" -> "}
              {example.isValid
                ? formatPreviewValue(example.castValue)
                : "invalid cast"}
            </code>
            {!example.isValid && example.error ? (
              <span className="schema-workbench__cast-example-error">
                {example.error}
              </span>
            ) : null}
          </li>
        ))}
      </ol>
    </div>
  );
}

type RelationImpactProps = {
  impact: SchemaChangeImpact;
};

function RelationImpact({ impact }: RelationImpactProps) {
  const sampleRows = impact.sampleRows ?? [];
  const sampleValues = impact.sampleValues ?? [];
  const sampleCount =
    sampleRows.length > 0 ? sampleRows.length : sampleValues.length;
  const hasMoreSamples =
    typeof impact.rowCount === "number" && impact.rowCount > sampleCount;
  const isDropImpact =
    impact.kind === "column_drop" ||
    impact.kind === "many_to_many_junction_drop" ||
    impact.kind === "table_drop";

  return (
    <div
      className={[
        "schema-workbench__relation-impact",
        isDropImpact ? "schema-workbench__relation-impact--drop" : "",
      ]
        .filter(Boolean)
        .join(" ")}
    >
      <h4>
        {impact.objectType === "column"
          ? "Column Impact"
          : impact.objectType === "table"
            ? "Table Impact"
            : "Relation Impact"}
      </h4>
      <p>{impact.message}</p>
      <dl className="schema-workbench__relation-impact-summary">
        <div>
          <dt>
            {impact.objectType === "table"
              ? "Table"
              : impact.objectType === "column"
                ? "Column"
                : "Object"}
          </dt>
          <dd>{impact.objectName}</dd>
        </div>
        {typeof impact.rowCount === "number" ? (
          <div>
            <dt>Rows at risk</dt>
            <dd>{impact.rowCount}</dd>
          </div>
        ) : null}
      </dl>
      {sampleRows.length > 0 ? (
        <ol className="schema-workbench__relation-impact-rows">
          {sampleRows.map((row, index) => (
            <li key={`${impact.objectName}:${index}`}>
              <Icon name="trash" size={12} aria-hidden="true" />
              <code className="mono">
                {Object.entries(row)
                  .map(([key, value]) => `${key}: ${formatPreviewValue(value)}`)
                  .join(", ")}
              </code>
            </li>
          ))}
          {hasMoreSamples ? (
            <li className="schema-workbench__relation-impact-more">
              <Icon name="trash" size={12} aria-hidden="true" />
              <span>...</span>
            </li>
          ) : null}
        </ol>
      ) : null}
      {sampleValues.length > 0 ? (
        <ol className="schema-workbench__relation-impact-rows">
          {sampleValues.map((value, index) => (
            <li key={`${impact.objectName}:value:${index}`}>
              <Icon name="trash" size={12} aria-hidden="true" />
              <code className="mono">{formatPreviewValue(value)}</code>
            </li>
          ))}
          {hasMoreSamples ? (
            <li className="schema-workbench__relation-impact-more">
              <Icon name="trash" size={12} aria-hidden="true" />
              <span>...</span>
            </li>
          ) : null}
        </ol>
      ) : null}
    </div>
  );
}

type DiagnosticsSectionProps = {
  activeDiagnosticId: string | null;
  diagnostics: WorkbenchDiagnostic[];
  onSelect: (diagnostic: WorkbenchDiagnostic) => void;
  singularLabel: "error" | "warning";
};

type PreviewDiagnosticsProps = {
  activeDiagnosticId: string | null;
  errorDiagnostics: WorkbenchDiagnostic[];
  onSelect: (diagnostic: WorkbenchDiagnostic) => void;
  warningDiagnostics: WorkbenchDiagnostic[];
};

export function PreviewDiagnostics({
  activeDiagnosticId,
  errorDiagnostics,
  onSelect,
  warningDiagnostics,
}: PreviewDiagnosticsProps) {
  if (errorDiagnostics.length === 0 && warningDiagnostics.length === 0) {
    return null;
  }

  return (
    <div className="schema-workbench__preview-diagnostics">
      <DiagnosticsSection
        singularLabel="error"
        diagnostics={errorDiagnostics}
        activeDiagnosticId={activeDiagnosticId}
        onSelect={onSelect}
      />

      <DiagnosticsSection
        singularLabel="warning"
        diagnostics={warningDiagnostics}
        activeDiagnosticId={activeDiagnosticId}
        onSelect={onSelect}
      />
    </div>
  );
}

function DiagnosticsSection({
  activeDiagnosticId,
  diagnostics,
  onSelect,
  singularLabel,
}: DiagnosticsSectionProps) {
  if (diagnostics.length === 0) {
    return null;
  }

  const label = countLabel(diagnostics.length, singularLabel);

  return (
    <section
      className="schema-workbench__diagnostic-section"
      aria-label={label}
    >
      <div className="schema-workbench__diagnostic-heading">
        <h3>{label}</h3>
      </div>

      <div className="schema-workbench__diagnostic-list">
        {diagnostics.map((diagnostic) => {
          const locationLabel = diagnostic.location
            ? `${diagnostic.locationExact ? "Line" : "Near line"} ${diagnostic.location.line}`
            : null;

          return (
            <button
              key={diagnostic.id}
              type="button"
              className={[
                "schema-workbench__diagnostic-card",
                diagnostic.severity === "warning"
                  ? "schema-workbench__diagnostic-card--warning"
                  : "schema-workbench__diagnostic-card--error",
                activeDiagnosticId === diagnostic.id
                  ? "schema-workbench__diagnostic-card--active"
                  : "",
              ]
                .filter(Boolean)
                .join(" ")}
              onMouseDown={(event) => {
                event.preventDefault();
                onSelect(diagnostic);
              }}
              onClick={(event) => {
                if (event.detail === 0) {
                  onSelect(diagnostic);
                }
              }}
            >
              <div className="schema-workbench__diagnostic-meta">
                <span className="schema-workbench__diagnostic-severity">
                  {diagnostic.source === "syntax"
                    ? "Syntax"
                    : diagnostic.source === "preview"
                      ? "Preview"
                      : diagnostic.severity === "error"
                        ? "Error"
                        : "Warning"}
                </span>
                <code className="schema-workbench__diagnostic-code">
                  {diagnostic.code}
                </code>
                {diagnostic.path ? (
                  <span className="schema-workbench__diagnostic-path mono">
                    {diagnostic.path}
                  </span>
                ) : null}
                {locationLabel ? (
                  <span className="schema-workbench__diagnostic-location">
                    {locationLabel}
                  </span>
                ) : null}
              </div>
              <p className="schema-workbench__diagnostic-message">
                {diagnostic.message}
              </p>
              {diagnostic.detail ? (
                <div className="schema-workbench__diagnostic-detail mono">
                  {diagnostic.detail}
                </div>
              ) : null}
            </button>
          );
        })}
      </div>
    </section>
  );
}
