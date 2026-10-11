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
import type { SchemaModelDocument } from "../../types/api";
import { FormulaEditor } from "./FormulaEditor";
import { describeFormulaType, formulaLinks, usableColumns } from "./formulaLanguage";
import "./Formulas.css";
import { DescribeFormula } from "../ai/DescribeFormula";

type FormulaFieldProps = {
  /** The table the formula belongs to. */
  table: string;
  /** The computed column the formula belongs to: using itself is a cycle. */
  column?: string;
  /** The drafted model: columns, other tables and links as in the designer. */
  model: Pick<SchemaModelDocument, "tables" | "relations">;
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
 * A formula with live feedback (Steps 3.4, 3.5): the server checks it against
 * the drafted model as you type, names its result type, underlines errors and
 * shows its values on the first rows of the applied table. Completions offer
 * columns, links ("account.name", "SUM(transactions.amount)") and functions.
 */
export function FormulaField({ table, column, model, value, onChange, previewRows = 5 }: FormulaFieldProps) {
  const [functions, setFunctions] = useState<FormulaFunction[]>([]);
  const [check, setCheck] = useState<CheckState>({ status: "idle" });
  const modelKey = JSON.stringify({ tables: model.tables, relations: model.relations });
  const { editorColumns, links } = useMemo(() => {
    const draft = JSON.parse(modelKey) as Pick<SchemaModelDocument, "tables" | "relations">;
    const own = draft.tables.find((t) => t.name === table)?.columns ?? [];
    return {
      editorColumns: own
        .filter((c) => c.name !== column)
        .map((c) => ({ name: c.name, displayName: c.displayName, type: c.type, resultType: c.resultType })),
      links: formulaLinks(draft, table),
    };
  }, [column, modelKey, table]);

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
        const validation = await validateFormula({ table, formula: value, column, model: JSON.parse(modelKey) });
        if (cancelled) return;
        if (!validation.valid) {
          setCheck({ status: "done", validation, preview: null, previewNote: null });
          return;
        }
        let preview: FormulaPreview | null = null;
        let previewNote: string | null = null;
        try {
          preview = await previewFormula({ table, formula: value, column, limit: previewRows });
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
  }, [column, modelKey, previewRows, table, value]);

  const errors = check.status === "done" ? check.validation.errors : [];
  const usable = usableColumns(editorColumns);

  return (
    <div className="formula-field">
      <FormulaEditor
        value={value}
        onChange={onChange}
        columns={editorColumns}
        functions={functions}
        links={links}
        errors={errors}
      />
      <DescribeFormula table={table} kind="column" current={value} onFormula={onChange} />
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
