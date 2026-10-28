import { useEffect } from "react";
import { createPortal } from "react-dom";
import type { VariableInput, DashboardVariable, DashboardWidget } from "../../api/dashboards";
import { Icon } from "../../components/common/Icon";
import { Button } from "../../components/ui/Button";
import { formatKindForType, formatNumber, toNumber } from "../../lib/format";
import { valueTypeOf } from "../../lib/columns";
import type { ModelSnapshot, ModelTable } from "../../types/api";
import { MeasureFormulaField } from "../formulas/MeasureFormulaField";
import { variableValues, type VariableResults } from "./formulaQueries";

/** "burn_rate" → "burn_rate_2" until it is free. */
function freeName(base: string, taken: string[]): string {
  if (!taken.includes(base)) return base;
  let n = 2;
  while (taken.includes(`${base}_${n}`)) n++;
  return `${base}_${n}`;
}

/**
 * Where a new variable starts: the table (and date column) of the last
 * variable that has one, else the table most of the dashboard's widgets
 * use with their date column, else the first table.
 */
export function newVariableSource(
  variables: DashboardVariable[],
  widgets: DashboardWidget[],
  tables: ModelTable[],
): Pick<DashboardVariable, "source" | "dateColumn"> {
  const known = (name: string | undefined) => (name && tables.some((t) => t.name === name) ? name : undefined);
  const last = [...variables].reverse().find((v) => known(v.source));
  if (last) return { source: last.source, dateColumn: last.dateColumn };
  const counts = new Map<string, number>();
  for (const widget of widgets) {
    const source = known(widget.data?.query?.source);
    if (source) counts.set(source, (counts.get(source) ?? 0) + 1);
  }
  const [source] = [...counts.entries()].sort((a, b) => b[1] - a[1])[0] ?? [];
  if (!source) return { source: tables[0]?.name };
  const dated = widgets.find((w) => w.data?.query?.source === source && (w.data.dateColumn || w.data.query.groupBy?.some((g) => g.bucket)));
  const dateColumn = dated?.data?.dateColumn || dated?.data?.query?.groupBy?.find((g) => g.bucket)?.column;
  return { source, dateColumn: dateColumn ?? undefined };
}

function formatValue(result: VariableResults[string] | undefined): string {
  if (!result) return "…";
  if (result.error) return result.error;
  const number = toNumber(result.value);
  if (number !== null) return formatNumber(number, formatKindForType(result.type ?? undefined));
  return result.value === null ? "—" : String(result.value);
}

/**
 * Dashboard variables (Step 3.6, Part 9 R9.10): named values such as
 * `burn_rate` or `runway`, computed from a table's rows or from other
 * variables, for KPI cards and widget formulas. Their values update as you
 * type, with the dashboard's range on the chosen date column.
 */
export function VariablesEditor({
  variables,
  widgets = [],
  tables,
  snapshot,
  results,
  onChange,
  onClose,
}: {
  variables: DashboardVariable[];
  /** The dashboard's widgets, for where a new variable starts. */
  widgets?: DashboardWidget[];
  tables: ModelTable[];
  snapshot: ModelSnapshot | null;
  results?: VariableResults;
  onChange: (variables: DashboardVariable[]) => void;
  onClose: () => void;
}) {
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const update = (index: number, patch: Partial<DashboardVariable>) =>
    onChange(variables.map((v, i) => (i === index ? { ...v, ...patch } : v)));
  const values = variableValues(results);

  const panel = (
    <aside className="widget-editor" aria-label="Dashboard variables">
      <header className="widget-editor__header">
        <h2>Variables</h2>
        <Button type="button" variant="ghost" size="sm" onClick={onClose} aria-label="Close variables">
          <Icon name="x" size={14} />
        </Button>
      </header>
      <div className="widget-editor__body">
        <p className="widget-editor__hint">
          Named values for key figures and formulas, e.g. <code>hours</code> from a table and <code>per_week = hours / weeks</code> from other variables.
        </p>
        {variables.map((variable, index) => {
          const table = tables.find((t) => t.name === variable.source);
          const dateColumns = (table?.columns ?? []).filter((c) => ["date", "datetime"].includes(valueTypeOf(c)));
          const others = Object.fromEntries(Object.entries(values).filter(([name]) => name !== variable.name));
          const result = results?.[variable.name];
          return (
            <section key={index} className="widget-editor__section variables-editor__item">
              <div className="widget-editor__row">
                <label className="widget-editor__field">
                  <span className="widget-editor__label">Name</span>
                  <input
                    className="field__control field__control--compact mono"
                    value={variable.name}
                    aria-label={`Name of variable ${index + 1}`}
                    onChange={(event) => update(index, { name: event.target.value.replace(/[^A-Za-z0-9_]/g, "_") })}
                  />
                </label>
                <label className="widget-editor__field">
                  <span className="widget-editor__label">Label</span>
                  <input
                    className="field__control field__control--compact"
                    value={variable.label ?? ""}
                    placeholder="Shown under a key figure"
                    aria-label={`Label of variable ${index + 1}`}
                    onChange={(event) => update(index, { label: event.target.value || undefined })}
                  />
                </label>
                <label className="widget-editor__field">
                  <span className="widget-editor__label">From</span>
                  <select
                    className="field__control field__control--compact"
                    value={variable.source ?? ""}
                    onChange={(event) => update(index, { source: event.target.value || undefined, dateColumn: undefined })}
                  >
                    <option value="">Other variables</option>
                    {tables.map((t) => (
                      <option key={t.name} value={t.name}>
                        {t.displayName ?? t.name}
                      </option>
                    ))}
                  </select>
                </label>
                <Button type="button" variant="ghost" size="sm" aria-label={`Remove variable ${variable.name}`} onClick={() => onChange(variables.filter((_, i) => i !== index))}>
                  <Icon name="trash" size={13} />
                </Button>
              </div>
              <div className="widget-editor__field">
                <span className="widget-editor__label">Formula</span>
                <MeasureFormulaField
                  table={variable.source}
                  snapshot={snapshot}
                  value={variable.formula}
                  onChange={(formula) => update(index, { formula })}
                  variables={others}
                  validate={false}
                  ariaLabel={`Formula of ${variable.name}`}
                  placeholder={variable.source ? 'SUM(hours WHERE status = "billable")' : "hours / weeks"}
                />
              </div>
              {table ? (
                <label className="widget-editor__field">
                  <span className="widget-editor__label">Date range filters</span>
                  <select
                    className="field__control field__control--compact"
                    value={variable.dateColumn ?? ""}
                    onChange={(event) => update(index, { dateColumn: event.target.value || undefined })}
                  >
                    <option value="">Nothing — all rows</option>
                    {dateColumns.map((c) => (
                      <option key={c.name} value={c.name}>
                        {c.displayName ?? c.name}
                      </option>
                    ))}
                  </select>
                </label>
              ) : null}
              {!variable.source ? <InputSettings variable={variable} onChange={(input) => update(index, { input })} /> : null}
              <p
                className={result?.error ? "formula-field__status formula-field__status--error" : "variables-editor__value"}
                role={result?.error ? "alert" : undefined}
              >
                {result?.error ? formatValue(result) : <>= {formatValue(result)}</>}
              </p>
            </section>
          );
        })}
        <Button
          type="button"
          variant="ghost"
          size="sm"
          leadingIcon={<Icon name="plus" size={13} />}
          onClick={() =>
            onChange([...variables, { name: freeName("value", variables.map((v) => v.name)), ...newVariableSource(variables, widgets, tables), formula: "COUNT()" }])
          }
        >
          Add variable
        </Button>
      </div>
      <footer className="widget-editor__footer">
        <Button type="button" variant="primary" onClick={onClose}>
          Done
        </Button>
      </footer>
    </aside>
  );
  return createPortal(panel, document.body);
}

/** A variable without a table can be an assumption viewers change; the formula is then its default. */
function InputSettings({ variable, onChange }: { variable: DashboardVariable; onChange: (input: VariableInput | undefined) => void }) {
  const input = variable.input;
  const number = (value: string) => (value.trim() === "" ? undefined : Number(value));
  return (
    <div className="widget-editor__field variables-editor__input">
      <label className="lens-check">
        <input
          type="checkbox"
          checked={!!input}
          aria-label={`Viewers can change ${variable.name}`}
          onChange={(event) => onChange(event.target.checked ? { kind: "number" } : undefined)}
        />
        Viewers can change it (the formula is the default, e.g. 0.07)
      </label>
      {input ? (
        <div className="widget-editor__row">
          <label className="widget-editor__field">
            <span className="widget-editor__label">As</span>
            <select className="field__control field__control--compact" value={input.kind} onChange={(event) => onChange({ ...input, kind: event.target.value as VariableInput["kind"] })}>
              <option value="number">Number</option>
              <option value="slider">Slider</option>
              <option value="select">Choice</option>
            </select>
          </label>
          {input.kind === "select" ? (
            <label className="widget-editor__field">
              <span className="widget-editor__label">Values (comma-separated)</span>
              <input
                className="field__control field__control--compact"
                value={(input.options ?? []).join(", ")}
                placeholder="base, optimistic, pessimistic"
                onChange={(event) =>
                  onChange({
                    ...input,
                    options: event.target.value
                      .split(",")
                      .map((v) => v.trim())
                      .filter(Boolean)
                      .map((v) => (/^-?\d+(\.\d+)?$/.test(v) ? Number(v) : v)),
                  })
                }
              />
            </label>
          ) : (
            <>
              {(["min", "max", "step"] as const).map((key) => (
                <label key={key} className="widget-editor__field">
                  <span className="widget-editor__label">{key === "min" ? "From" : key === "max" ? "To" : "Step"}</span>
                  <input className="field__control field__control--compact" type="number" step="any" value={input[key] ?? ""} onChange={(event) => onChange({ ...input, [key]: number(event.target.value) })} />
                </label>
              ))}
            </>
          )}
        </div>
      ) : null}
    </div>
  );
}
