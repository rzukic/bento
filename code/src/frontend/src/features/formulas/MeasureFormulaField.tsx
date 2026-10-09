import { useEffect, useMemo, useState } from "react";
import { loadFormulaFunctions, validateFormula, type FormulaFunction, type FormulaValidation } from "../../api/formulas";
import type { VariableValue } from "../../api/query";
import type { ModelSnapshot } from "../../types/api";
import { FormulaEditor } from "./FormulaEditor";
import { describeFormulaType, formulaLinks, type FormulaColumn } from "./formulaLanguage";
import "./Formulas.css";
import { DescribeFormula } from "../ai/DescribeFormula";

type MeasureFormulaFieldProps = {
  /** The table the measure runs over; none = a formula over variables only. */
  table?: string;
  snapshot: Pick<ModelSnapshot, "tables" | "relations"> | null;
  value: string;
  onChange: (value: string) => void;
  /** Values at the end of each period: BUCKET_START()/BUCKET_END() are available. */
  asOf?: boolean;
  /** Dashboard variables usable by name, with their current values. */
  variables?: Record<string, VariableValue>;
  ariaLabel?: string;
  placeholder?: string;
  /** Skip the server check (variables are checked by evaluating them). */
  validate?: boolean;
};

const CHECK_DELAY_MS = 350;

/** The snapshot's links, in the shape the completions need. */
function linkModel(snapshot: Pick<ModelSnapshot, "tables" | "relations">) {
  return {
    tables: snapshot.tables,
    relations: (snapshot.relations ?? [])
      .filter((r) => r.from && r.to && r.type)
      .map((r) => ({ ...r, type: r.type!, from: r.from!, to: r.to! })),
  };
}

/**
 * A dashboard measure (Step 3.6): aggregates over the rows of each group,
 * e.g. `SUM(amount WHERE amount > 0) / COUNT()`, checked by the server as
 * you type. Completions offer the table's columns, its links, the
 * dashboard's variables and the functions.
 */
export function MeasureFormulaField({
  table,
  snapshot,
  value,
  onChange,
  asOf = false,
  variables = {},
  ariaLabel = "Measure formula",
  placeholder = "SUM(amount WHERE amount > 0)",
  validate = true,
}: MeasureFormulaFieldProps) {
  const [functions, setFunctions] = useState<FormulaFunction[]>([]);
  const [check, setCheck] = useState<FormulaValidation | null>(null);
  const variablesKey = JSON.stringify(variables);

  useEffect(() => {
    let cancelled = false;
    loadFormulaFunctions()
      .then((loaded) => !cancelled && setFunctions(loaded))
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, []);

  const { columns, links } = useMemo(() => {
    const own = snapshot?.tables.find((t) => t.name === table)?.columns ?? [];
    const variableColumns: FormulaColumn[] = Object.entries(JSON.parse(variablesKey) as Record<string, VariableValue>).map(([name, current]) => ({
      name,
      type: typeof current === "number" ? "variable · number" : typeof current === "boolean" ? "variable · yes/no" : "variable",
    }));
    return {
      columns: [
        ...own.filter((c) => !c.primaryKey).map((c) => ({ name: c.name, displayName: c.displayName, type: c.type, resultType: c.resultType })),
        ...variableColumns,
      ],
      links: snapshot && table ? formulaLinks(linkModel(snapshot), table) : [],
    };
  }, [snapshot, table, variablesKey]);

  useEffect(() => {
    if (!validate || !table || value.trim() === "") {
      setCheck(null);
      return;
    }
    let cancelled = false;
    const timer = window.setTimeout(() => {
      validateFormula({ table, formula: value, mode: "measure", asOf, variables: JSON.parse(variablesKey) })
        .then((result) => !cancelled && setCheck(result))
        .catch(() => !cancelled && setCheck(null));
    }, CHECK_DELAY_MS);
    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [asOf, table, validate, value, variablesKey]);

  return (
    <div className="formula-field">
      <FormulaEditor
        value={value}
        onChange={onChange}
        columns={columns}
        functions={functions}
        links={links}
        errors={check?.errors ?? []}
        ariaLabel={ariaLabel}
        placeholder={placeholder}
      />
      <DescribeFormula table={table} kind="measure" current={value} onFormula={onChange} />
      {check === null ? null : check.valid ? (
        <p className="formula-field__status formula-field__status--ok">
          Gives {describeFormulaType(check.type ?? "float")}
          {check.running ? " · running total" : ""}
        </p>
      ) : (
        <ul className="formula-field__errors" role="alert">
          {check.errors.map((error) => (
            <li key={`${error.start}-${error.message}`}>{error.message}</li>
          ))}
        </ul>
      )}
    </div>
  );
}
