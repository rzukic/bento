import type { SchemaModelDocument } from "../../../types/api";
import { countLabel } from "../schemaWorkbenchHelpers";
import type { VisualCanvasSelection, WorkbenchDiagnostic } from "../types";

type VisualDiagnosticTarget = {
  label: string;
  selection: VisualCanvasSelection | null;
};

function getVisualDiagnosticTarget(
  document: SchemaModelDocument,
  path: string | null,
): VisualDiagnosticTarget {
  if (!path) {
    return { label: "Canvas", selection: null };
  }

  const tableMatch = /^tables\[(\d+)\](?:\.columns\[(\d+)\])?(?:\.indexes\[(\d+)\])?/.exec(
    path,
  );
  if (tableMatch) {
    const table = document.tables[Number(tableMatch[1])];
    if (!table) {
      return { label: "Canvas", selection: null };
    }

    if (tableMatch[2] !== undefined) {
      const column = table.columns[Number(tableMatch[2])];
      if (column) {
        return {
          label: `${table.name}.${column.name}`,
          selection: {
            type: "column",
            tableName: table.name,
            columnName: column.name,
          },
        };
      }
    }

    return {
      label: table.name,
      selection: { type: "table", tableName: table.name },
    };
  }

  const relationMatch = /^relations\[(\d+)\]/.exec(path);
  if (relationMatch) {
    const relation = document.relations[Number(relationMatch[1])];
    if (relation) {
      return {
        label: relation.name,
        selection: {
          type: "relation",
          relationName: relation.name,
        },
      };
    }
  }

  return { label: "Canvas", selection: null };
}

type VisualPreviewDiagnosticsProps = {
  activeDiagnosticId: string | null;
  diagnostics: WorkbenchDiagnostic[];
  document: SchemaModelDocument;
  onSelect: (
    diagnostic: WorkbenchDiagnostic,
    selection: VisualCanvasSelection | null,
  ) => void;
};

export function VisualPreviewDiagnostics({
  activeDiagnosticId,
  diagnostics,
  document,
  onSelect,
}: VisualPreviewDiagnosticsProps) {
  return (
    <section
      className="schema-workbench__visual-diagnostics"
      aria-label="Visual preview diagnostics"
    >
      <h3>Preview diagnostics</h3>
      <div className="schema-workbench__diagnostic-list">
        {diagnostics.slice(0, 5).map((diagnostic) => {
          const target = getVisualDiagnosticTarget(document, diagnostic.path);
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
              onClick={() => onSelect(diagnostic, target.selection)}
            >
              <div className="schema-workbench__diagnostic-meta">
                <span className="schema-workbench__diagnostic-severity">
                  {diagnostic.severity === "warning" ? "Warning" : "Error"}
                </span>
                <code className="schema-workbench__diagnostic-code">
                  {diagnostic.code}
                </code>
                <span className="schema-workbench__diagnostic-location">
                  {target.label}
                </span>
              </div>
              <p className="schema-workbench__diagnostic-message">
                {diagnostic.message}
              </p>
              {diagnostic.path ? (
                <code className="schema-workbench__diagnostic-path">
                  {diagnostic.path}
                </code>
              ) : null}
            </button>
          );
        })}
      </div>
      {diagnostics.length > 5 ? (
        <p>
          {countLabel(diagnostics.length - 5, "additional diagnostic")} hidden.
        </p>
      ) : null}
    </section>
  );
}
