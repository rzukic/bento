import { useEffect, useMemo, useState } from "react";
import {
  loadFormulaFunctions,
  previewFormula,
  validateFormula,
  type FormulaFunction,
  type FormulaPreview,
  type FormulaValidation,
} from "../../api/formulas";
import { formatAmount, formatNumber, toNumber } from "../../lib/format";
import type { SchemaColumn } from "../../types/api";
import { FormulaEditor } from "./FormulaEditor";
import { describeFormulaType, usableColumns } from "./formulaLanguage";
import "./Formulas.css";

type FormulaFieldProps = {
  /** The table the formula belongs to. */
  table: string;
  /** The table's columns as drafted (without the formula's own column). */
  columns: SchemaColumn[];
  value: string;
  onChange: (value: string) => void;
  /** Rows shown in the preview. */
  previewRows?: number;
};

const CHECK_DELAY_MS = 350;

type CheckState =
  | { status: "idle" }
  | { status: "checking" }
  | { status: "done"; validation: FormulaValidation; preview: FormulaPreview | null; previewNote: string | null }
  | { status: "failed"; message: string };

/**
 * A formula with live feedback (Step 3.4): the server checks it against the
 * drafted columns as you type, names its result type, underlines errors and
 * shows its values on the first rows of the applied table.
 */
export function FormulaField({ table, columns, value, onChange, previewRows = 5 }: FormulaFieldProps) {
  const [functions, setFunctions] = useState<FormulaFunction[]>([]);
  const [check, setCheck] = useState<CheckState>({ status: "idle" });
  const columnsKey = JSON.stringify(columns);
  const editorColumns = useMemo(
    () => columns.map((column) => ({ name: column.name, displayName: column.displayName, type: column.type })),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [columnsKey],
  );

  useEffect(() => {
    let cancelled = false;
    loadFormulaFunctions()
      .then((loaded) => {
        if (!cancelled) setFunctions(loaded);
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    if (value.trim() === "" || value.trim() === "=") {
      setCheck({ status: "idle" });
      return;
    }

    let cancelled = false;
    setCheck((current) => (current.status === "done" ? current : { status: "checking" }));
    const timer = window.setTimeout(async () => {
      try {
        const validation = await validateFormula({ table, formula: value, columns: JSON.parse(columnsKey) });
        if (cancelled) return;
        if (!validation.valid) {
          setCheck({ status: "done", validation, preview: null, previewNote: null });
          return;
        }
        let preview: FormulaPreview | null = null;
        let previewNote: string | null = null;
        try {
          preview = await previewFormula({ table, formula: value, limit: previewRows });
          if (preview.rows.length === 0) previewNote = "The table has no rows yet.";
        } catch {
          previewNote = "Apply the draft to see values from your data.";
        }
        if (!cancelled) setCheck({ status: "done", validation, preview, previewNote });
      } catch (error) {
        if (!cancelled)
          setCheck({ status: "failed", message: error instanceof Error ? error.message : "The formula could not be checked." });
      }
    }, CHECK_DELAY_MS);

    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [columnsKey, previewRows, table, value]);

  const errors = check.status === "done" ? check.validation.errors : [];
  const usable = usableColumns(editorColumns);

  return (
    <div className="formula-field">
      <FormulaEditor
        value={value}
        onChange={onChange}
        columns={editorColumns}
        functions={functions}
        errors={errors}
      />
      <FormulaStatus check={check} hasColumns={usable.length > 0} />
      {check.status === "done" && check.preview && check.preview.rows.length > 0 ? (
        <FormulaPreviewTable
          preview={check.preview}
          types={Object.fromEntries(editorColumns.map((column) => [column.name, column.type]))}
        />
      ) : null}
      {check.status === "done" && check.previewNote ? (
        <p className="formula-field__note">{check.previewNote}</p>
      ) : null}
    </div>
  );
}

function FormulaStatus({ check, hasColumns }: { check: CheckState; hasColumns: boolean }) {
  if (check.status === "idle") {
    return (
      <p className="formula-field__status">
        {hasColumns
          ? "Use column names, functions like IF(…) or ROUND(…), and + - * / & for text. Start typing for suggestions."
          : "Add columns to the table to use them in the formula."}
      </p>
    );
  }
  if (check.status === "checking") {
    return <p className="formula-field__status">Checking…</p>;
  }
  if (check.status === "failed") {
    return <p className="formula-field__status formula-field__status--error" role="alert">{check.message}</p>;
  }

  const { validation } = check;
  if (!validation.valid) {
    return (
      <ul className="formula-field__errors" role="alert">
        {validation.errors.map((error) => (
          <li key={`${error.start}-${error.end}-${error.message}`}>{error.message}</li>
        ))}
      </ul>
    );
  }
  return (
    <p className="formula-field__status formula-field__status--ok">
      Gives {describeFormulaType(validation.type ?? "text")}
      {validation.columns.length > 0 ? ` · uses ${validation.columns.join(", ")}` : ""}
    </p>
  );
}

function FormulaPreviewTable({ preview, types }: { preview: FormulaPreview; types: Record<string, string> }) {
  return (
    <div className="formula-field__preview">
      <table>
        <caption>Values on the first rows</caption>
        <thead>
          <tr>
            {preview.columns.map((column) => (
              <th key={column} scope="col">{column}</th>
            ))}
            <th scope="col" className="formula-field__result">Result</th>
          </tr>
        </thead>
        <tbody>
          {preview.rows.map((row, index) => (
            <tr key={String(row.id ?? index)}>
              {preview.columns.map((column) => (
                <td key={column}>{formatPreviewValue(row.inputs[column], types[column] ?? null)}</td>
              ))}
              <td className="formula-field__result">{formatPreviewValue(row.value, preview.type)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function formatPreviewValue(value: string | number | boolean | null | undefined, type: string | null): string {
  if (value === null || value === undefined) return "—";
  if (typeof value === "boolean") return value ? "Yes" : "No";
  if (value === "") return "“”";
  const number = typeof value === "number" ? value : null;
  if (number !== null && type === "currency") return formatAmount(number);
  if (number !== null && type === "float") return formatNumber(number);
  if (number !== null && type === "integer") return formatNumber(number, "integer");
  if (typeof value === "number") return String(toNumber(value) ?? value);
  return value;
}
