import { useEffect, useMemo, useState } from "react";
import { valueTypeOf } from "../../lib/columns";
import { createPortal } from "react-dom";
import type { DashboardVariable, DashboardWidget, WidgetDisplay, WidgetType } from "../../api/dashboards";
import { MeasureFormulaField } from "../formulas/MeasureFormulaField";
import { variableValues, type VariableResults } from "./formulaQueries";
import { listSavedQueries, type AggregationQuery, type QueryBucket, type QueryFunction, type QueryMeasure, type SavedQuery } from "../../api/query";
import { Icon } from "../../components/common/Icon";
import { Button } from "../../components/ui/Button";
import type { ModelColumn, ModelSnapshot, ModelTable } from "../../types/api";
import { WIDGET_LABELS, dataTables, defaultQuery } from "./starter";

const BUCKETS: { value: QueryBucket; label: string }[] = [
  { value: "day", label: "Day" },
  { value: "week", label: "Week" },
  { value: "month", label: "Month" },
  { value: "quarter", label: "Quarter" },
  { value: "year", label: "Year" },
];

const FUNCTIONS: { value: QueryFunction; label: string }[] = [
  { value: "sum", label: "Sum" },
  { value: "count", label: "Count" },
  { value: "avg", label: "Average" },
  { value: "min", label: "Smallest" },
  { value: "max", label: "Largest" },
  { value: "count_distinct", label: "Distinct" },
];

const OPERATORS: { value: string; label: string; needsValue: boolean }[] = [
  { value: "eq", label: "is", needsValue: true },
  { value: "neq", label: "is not", needsValue: true },
  { value: "gt", label: ">", needsValue: true },
  { value: "gte", label: "≥", needsValue: true },
  { value: "lt", label: "<", needsValue: true },
  { value: "lte", label: "≤", needsValue: true },
  { value: "like", label: "contains", needsValue: true },
  { value: "null", label: "is empty", needsValue: false },
  { value: "notnull", label: "is not empty", needsValue: false },
];

const FORMATS: { value: NonNullable<WidgetDisplay["format"]>; label: string }[] = [
  { value: "auto", label: "Automatic" },
  { value: "currency", label: "Amount" },
  { value: "number", label: "Number" },
  { value: "integer", label: "Whole number" },
  { value: "percent", label: "Percent" },
];

type FilterRow = { column: string; op: string; value: string };

/** `{ amount: { lt: 0 }, status: "paid" }` ⇄ editable rows. */
export function filterToRows(filter: AggregationQuery["filter"]): FilterRow[] {
  const rows: FilterRow[] = [];
  for (const [column, condition] of Object.entries(filter ?? {})) {
    if (condition !== null && typeof condition === "object" && !Array.isArray(condition)) {
      for (const [op, value] of Object.entries(condition as Record<string, unknown>)) {
        rows.push({ column, op, value: Array.isArray(value) ? value.join(",") : value === true || value === null ? "" : String(value) });
      }
    } else {
      rows.push({ column, op: condition === null ? "null" : "eq", value: condition === null ? "" : String(condition) });
    }
  }
  return rows;
}

export function rowsToFilter(rows: FilterRow[]): AggregationQuery["filter"] {
  const filter: Record<string, Record<string, unknown>> = {};
  for (const row of rows) {
    if (!row.column) continue;
    const needsValue = OPERATORS.find((o) => o.value === row.op)?.needsValue ?? true;
    if (needsValue && row.value === "") continue;
    filter[row.column] = { ...(filter[row.column] ?? {}), [row.op]: needsValue ? row.value : true };
  }
  return Object.keys(filter).length > 0 ? filter : undefined;
}

const isNumberColumn = (c: ModelColumn) => c.type === "integer" || c.type === "float" || c.type === "currency";
const isDateColumn = (c: ModelColumn) => c.type === "date" || c.type === "datetime";

function Select({ label, value, onChange, children, className }: { label: string; value: string; onChange: (value: string) => void; children: React.ReactNode; className?: string }) {
  return (
    <label className={["widget-editor__field", className].filter(Boolean).join(" ")}>
      <span className="widget-editor__label">{label}</span>
      <select className="field__control field__control--compact" value={value} onChange={(event) => onChange(event.target.value)}>
        {children}
      </select>
    </label>
  );
}

/** A plain measure as the formula it stands for (formulas are needed by period end). */
function measureAsFormula(measure: QueryMeasure, dateColumn: string | null): string {
  if (measure.formula) return measure.formula;
  const until = dateColumn ? ` WHERE ${dateColumn} <= BUCKET_END()` : "";
  const fn = measure.fn === "count_distinct" ? "COUNT" : (measure.fn ?? "sum").toUpperCase();
  return measure.column && fn !== "COUNT" ? `${fn}(${measure.column}${until})` : `COUNT(${until.trim()})`;
}

export function WidgetEditor({
  widget,
  tables,
  snapshot = null,
  variables = [],
  variableResults,
  onChange,
  onClose,
}: {
  widget: DashboardWidget;
  tables: ModelTable[];
  /** The schema with relations, for formula completions. */
  snapshot?: ModelSnapshot | null;
  /** The dashboard's variables: KPI cards can show one, formulas can use them. */
  variables?: DashboardVariable[];
  variableResults?: VariableResults;
  onChange: (widget: DashboardWidget) => void;
  onClose: () => void;
}) {
  const values = useMemo(() => variableValues(variableResults), [variableResults]);
  const [saved, setSaved] = useState<SavedQuery[]>([]);
  useEffect(() => {
    listSavedQueries().then(setSaved).catch(() => setSaved([]));
  }, []);
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const sources = dataTables(tables);
  const query = widget.data?.query;
  const savedId = widget.data?.savedQueryId;
  const table = useMemo(() => sources.find((t) => t.name === (query?.source ?? saved.find((s) => s.id === savedId)?.source)), [sources, query, saved, savedId]);
  // The generated key is neither a useful group nor a number to add up.
  // Computed columns take part like stored ones, typed by their result.
  const columns = (table?.columns ?? [])
    .filter((c) => !c.primaryKey)
    .map((c) => (c.type === "computed" ? { ...c, type: valueTypeOf(c) } : c));

  const set = (patch: Partial<DashboardWidget>) => onChange({ ...widget, ...patch });
  const setQuery = (next: AggregationQuery) => set({ data: { ...widget.data, query: next, savedQueryId: undefined } });
  const setDisplay = (patch: Partial<WidgetDisplay>) => set({ display: { ...widget.display, ...patch } });

  const groups = query?.groupBy ?? [];
  const measures = query?.measures ?? [];
  const asOf = Boolean(widget.data?.asOf);
  const dateGroup = groups.find((g) => g.bucket);
  const filterRows = filterToRows(query?.filter);
  const hasDateGroup = groups.some((g) => g.bucket);
  const outputs = [...groups.map((g) => g.as ?? g.column), ...measures.map((m) => m.name)];

  const updateGroup = (index: number, column: string, bucket?: QueryBucket) => {
    if (!query) return;
    const next = [...groups];
    const def = columns.find((c) => c.name === column);
    if (!column) next.splice(index, 1);
    else {
      const withBucket = def && isDateColumn(def) ? (bucket ?? next[index]?.bucket ?? "month") : undefined;
      next[index] = { column, ...(withBucket ? { bucket: withBucket, as: withBucket } : {}) };
    }
    // Keep the order unless it named a group that is gone.
    const outputsAfter = new Set([...next.map((g) => g.as ?? g.column), ...measures.map((m) => m.name)]);
    const sort = query.sort?.filter((s) => outputsAfter.has(s.replace(/^-/, "")));
    setQuery({ ...query, groupBy: next, sort: sort && sort.length > 0 ? sort : undefined });
  };

  const updateMeasure = (index: number, patch: Partial<QueryMeasure>) => {
    if (!query) return;
    const next = measures.map((m, i) => (i === index ? { ...m, ...patch } : m));
    const measure = next[index];
    if (measure.formula !== undefined) {
      delete measure.fn;
      delete measure.column;
    }
    if (measure.fn === "count" && patch.fn) delete measure.column;
    if (measure.fn !== "sum" && measure.fn !== "count") {
      delete measure.running;
      delete measure.carryIn;
    }
    if (!measure.running) delete measure.carryIn;
    setQuery({ ...query, measures: next });
  };

  const panel = (
    <aside className="widget-editor" aria-label="Widget settings">
      <header className="widget-editor__header">
        <h2>Widget</h2>
        <Button type="button" variant="ghost" size="sm" onClick={onClose} aria-label="Close widget settings">
          <Icon name="x" size={14} />
        </Button>
      </header>

      <div className="widget-editor__body">
        <label className="widget-editor__field">
          <span className="widget-editor__label">Title</span>
          <input className="field__control field__control--compact" value={widget.title} onChange={(event) => set({ title: event.target.value })} />
        </label>

        <Select
          label="Shows as"
          value={widget.type}
          onChange={(value) => {
            const type = value as WidgetType;
            if (type === "text") set({ type, data: undefined, display: { text: widget.display?.text ?? "" } });
            else if (!widget.data && sources[0]) set({ type, data: { query: defaultQuery(type, sources[0]) } });
            else set({ type });
          }}
        >
          {(Object.keys(WIDGET_LABELS) as WidgetType[]).map((type) => (
            <option key={type} value={type}>
              {WIDGET_LABELS[type]}
            </option>
          ))}
        </Select>

        {widget.type === "text" ? (
          <label className="widget-editor__field">
            <span className="widget-editor__label">Text</span>
            <textarea
              className="field__control widget-editor__textarea"
              value={widget.display?.text ?? ""}
              onChange={(event) => setDisplay({ text: event.target.value })}
              rows={8}
            />
            <span className="widget-editor__hint"># heading, - list item</span>
          </label>
        ) : (
          <>
            <Select
              label="Data from"
              value={widget.data?.variable ? `var:${widget.data.variable}` : savedId !== undefined ? `saved:${savedId}` : query ? `table:${query.source}` : ""}
              onChange={(value) => {
                if (value.startsWith("var:")) set({ data: { variable: value.slice(4) } });
                else if (value.startsWith("saved:")) set({ data: { savedQueryId: Number(value.slice(6)), dateColumn: widget.data?.dateColumn } });
                else {
                  const next = sources.find((t) => t.name === value.slice(6));
                  if (next) set({ data: { query: defaultQuery(widget.type, next) } });
                }
              }}
            >
              <option value="" disabled>
                Choose…
              </option>
              <optgroup label="Table">
                {sources.map((t) => (
                  <option key={t.name} value={`table:${t.name}`}>
                    {t.displayName ?? t.name}
                  </option>
                ))}
              </optgroup>
              {saved.length > 0 ? (
                <optgroup label="Saved query">
                  {saved.map((s) => (
                    <option key={s.id} value={`saved:${s.id}`}>
                      {s.name}
                    </option>
                  ))}
                </optgroup>
              ) : null}
              {widget.type === "kpi" && variables.length > 0 ? (
                <optgroup label="Dashboard variable">
                  {variables.map((v) => (
                    <option key={v.name} value={`var:${v.name}`}>
                      {v.label || v.name}
                    </option>
                  ))}
                </optgroup>
              ) : null}
            </Select>
            {widget.data?.variable ? (
              <p className="widget-editor__hint">Shows the variable's current value. Variables are edited under "Variables".</p>
            ) : null}

            {query ? (
              <>
                <section className="widget-editor__section">
                  <h3>Group by</h3>
                  {[0, 1].map((index) => {
                    const group = groups[index];
                    if (index === 1 && !groups[0]) return null;
                    const def = columns.find((c) => c.name === group?.column);
                    return (
                      <div key={index} className="widget-editor__row">
                        <Select label={index === 0 ? "Axis / rows" : "Split by"} value={group?.column ?? ""} onChange={(value) => updateGroup(index, value)}>
                          <option value="">{index === 0 ? "Nothing (one value)" : "No split"}</option>
                          {columns.map((c) => (
                            <option key={c.name} value={c.name}>
                              {c.displayName ?? c.name}
                            </option>
                          ))}
                        </Select>
                        {def && isDateColumn(def) ? (
                          <Select label="Per" value={group?.bucket ?? "month"} onChange={(value) => updateGroup(index, group!.column, value as QueryBucket)}>
                            {BUCKETS.map((b) => (
                              <option key={b.value} value={b.value}>
                                {b.label}
                              </option>
                            ))}
                          </Select>
                        ) : null}
                      </div>
                    );
                  })}
                  {dateGroup ? (
                    <div className="widget-editor__checks">
                      <label title="Each period shows the value at its last day, e.g. a balance or net worth">
                        <input
                          type="checkbox"
                          checked={asOf}
                          onChange={(event) => {
                            const on = event.target.checked;
                            // By period end, values are formulas with BUCKET_END().
                            const nextMeasures = on
                              ? measures.map((m) => ({ name: m.name, formula: measureAsFormula(m, dateGroup.column) }))
                              : measures;
                            set({ data: { ...widget.data, asOf: on || undefined, query: { ...query, measures: nextMeasures } } });
                          }}
                        />
                        Value at the end of each period
                      </label>
                    </div>
                  ) : null}
                  {asOf ? (
                    <p className="widget-editor__hint">
                      The range sets the periods; each value is computed over all rows with BUCKET_END(), the period's last day.
                    </p>
                  ) : null}
                </section>

                <section className="widget-editor__section">
                  <h3>Values</h3>
                  {measures.map((measure, index) => {
                    const candidates = measure.fn === "sum" || measure.fn === "avg" ? columns.filter(isNumberColumn) : columns;
                    const isFormula = measure.formula !== undefined;
                    return (
                      <div key={index} className="widget-editor__measure">
                        <div className="widget-editor__row">
                          <Select
                            label="Function"
                            value={isFormula ? "formula" : (measure.fn ?? "sum")}
                            onChange={(value) => {
                              if (value === "formula") {
                                const next = { name: measure.name, formula: measureAsFormula(measure, asOf ? (dateGroup?.column ?? null) : null) };
                                setQuery({ ...query, measures: measures.map((m, i) => (i === index ? next : m)) });
                              } else {
                                const next = { name: measure.name, fn: value as QueryFunction, column: value === "count" ? undefined : (measure.column ?? candidates[0]?.name) };
                                setQuery({ ...query, measures: measures.map((m, i) => (i === index ? next : m)) });
                              }
                            }}
                          >
                            {FUNCTIONS.map((f) => (
                              <option key={f.value} value={f.value} disabled={asOf}>
                                {f.label}
                              </option>
                            ))}
                            <option value="formula">Formula…</option>
                          </Select>
                          {isFormula ? null : (
                          <Select label="Of" value={measure.column ?? ""} onChange={(value) => updateMeasure(index, { column: value || undefined })}>
                            {measure.fn === "count" ? <option value="">Rows</option> : null}
                            {candidates.map((c) => (
                              <option key={c.name} value={c.name}>
                                {c.displayName ?? c.name}
                              </option>
                            ))}
                          </Select>
                          )}
                          <Button
                            type="button"
                            variant="ghost"
                            size="sm"
                            aria-label={`Remove value ${measure.name}`}
                            disabled={measures.length === 1}
                            onClick={() => setQuery({ ...query, measures: measures.filter((_, i) => i !== index), sort: undefined })}
                          >
                            <Icon name="trash" size={13} />
                          </Button>
                        </div>
                        {isFormula ? (
                          <div className="widget-editor__field">
                            <span className="widget-editor__label">Formula</span>
                            <MeasureFormulaField
                              table={query.source}
                              snapshot={snapshot}
                              value={measure.formula ?? ""}
                              onChange={(formula) => updateMeasure(index, { formula })}
                              asOf={asOf}
                              variables={values}
                              ariaLabel={`Formula of ${measure.name}`}
                            />
                          </div>
                        ) : null}
                        <label className="widget-editor__field">
                          <span className="widget-editor__label">Name</span>
                          <input
                            className="field__control field__control--compact"
                            value={measure.name}
                            onChange={(event) => updateMeasure(index, { name: event.target.value })}
                          />
                        </label>
                        {(measure.fn === "sum" || measure.fn === "count") && hasDateGroup ? (
                          <div className="widget-editor__checks">
                            <label>
                              <input type="checkbox" checked={measure.running ?? false} onChange={(event) => updateMeasure(index, { running: event.target.checked || undefined })} />
                              Running total
                            </label>
                            {measure.running ? (
                              <label title="Start from everything before the date range — for balances">
                                <input type="checkbox" checked={measure.carryIn ?? false} onChange={(event) => updateMeasure(index, { carryIn: event.target.checked || undefined })} />
                                Include earlier rows
                              </label>
                            ) : null}
                          </div>
                        ) : null}
                      </div>
                    );
                  })}
                  <Button
                    type="button"
                    variant="ghost"
                    size="sm"
                    leadingIcon={<Icon name="plus" size={13} />}
                    onClick={() => {
                      const number = columns.find(isNumberColumn);
                      const name = outputs.includes("value") ? `value_${measures.length + 1}` : "value";
                      const plain: QueryMeasure = number ? { name, fn: "sum", column: number.name } : { name, fn: "count" };
                      const added = asOf ? { name, formula: measureAsFormula(plain, dateGroup?.column ?? null) } : plain;
                      setQuery({ ...query, measures: [...measures, added] });
                    }}
                  >
                    Add value
                  </Button>
                </section>

                <section className="widget-editor__section">
                  <h3>Only rows where</h3>
                  {filterRows.map((row, index) => {
                    const operator = OPERATORS.find((o) => o.value === row.op);
                    const update = (patch: Partial<FilterRow>) =>
                      setQuery({ ...query, filter: rowsToFilter(filterRows.map((r, i) => (i === index ? { ...r, ...patch } : r))) });
                    return (
                      <div key={index} className="widget-editor__row">
                        <Select label="Column" value={row.column} onChange={(value) => update({ column: value })}>
                          {columns.map((c) => (
                            <option key={c.name} value={c.name}>
                              {c.displayName ?? c.name}
                            </option>
                          ))}
                        </Select>
                        <Select label="Condition" value={row.op} onChange={(value) => update({ op: value })} className="widget-editor__field--narrow">
                          {OPERATORS.map((o) => (
                            <option key={o.value} value={o.value}>
                              {o.label}
                            </option>
                          ))}
                        </Select>
                        {operator?.needsValue !== false ? (
                          <label className="widget-editor__field widget-editor__field--narrow">
                            <span className="widget-editor__label">Value</span>
                            <input className="field__control field__control--compact" value={row.value} onChange={(event) => update({ value: event.target.value })} />
                          </label>
                        ) : null}
                        <Button
                          type="button"
                          variant="ghost"
                          size="sm"
                          aria-label="Remove condition"
                          onClick={() => setQuery({ ...query, filter: rowsToFilter(filterRows.filter((_, i) => i !== index)) })}
                        >
                          <Icon name="trash" size={13} />
                        </Button>
                      </div>
                    );
                  })}
                  <Button
                    type="button"
                    variant="ghost"
                    size="sm"
                    leadingIcon={<Icon name="plus" size={13} />}
                    disabled={columns.length === 0}
                    onClick={() => {
                      const number = columns.find(isNumberColumn) ?? columns[0];
                      // A new condition needs a value before it filters anything.
                      setQuery({ ...query, filter: { ...(query.filter ?? {}), [number.name]: { ...(((query.filter ?? {})[number.name] as object) ?? {}), lt: 0 } } });
                    }}
                  >
                    Add condition
                  </Button>
                </section>

                <section className="widget-editor__section">
                  <h3>Order</h3>
                  <div className="widget-editor__row">
                    <Select
                      label="Sort by"
                      value={query.sort?.[0]?.replace(/^-/, "") ?? ""}
                      onChange={(value) => setQuery({ ...query, sort: value ? [`${query.sort?.[0]?.startsWith("-") ? "-" : ""}${value}`] : undefined })}
                    >
                      <option value="">Default</option>
                      {outputs.map((name) => (
                        <option key={name} value={name}>
                          {name}
                        </option>
                      ))}
                    </Select>
                    <Select
                      label="Direction"
                      value={query.sort?.[0]?.startsWith("-") ? "desc" : "asc"}
                      onChange={(value) =>
                        query.sort?.[0] && setQuery({ ...query, sort: [`${value === "desc" ? "-" : ""}${query.sort[0].replace(/^-/, "")}`] })
                      }
                      className="widget-editor__field--narrow"
                    >
                      <option value="asc">Up</option>
                      <option value="desc">Down</option>
                    </Select>
                    <label className="widget-editor__field widget-editor__field--narrow">
                      <span className="widget-editor__label">Limit</span>
                      <input
                        className="field__control field__control--compact"
                        type="number"
                        min={1}
                        max={10000}
                        value={query.limit ?? ""}
                        placeholder="All"
                        onChange={(event) => setQuery({ ...query, limit: event.target.value ? Number(event.target.value) : undefined })}
                      />
                    </label>
                  </div>
                </section>
              </>
            ) : null}

            {table && !asOf ? (
              <Select
                label="Date range filters"
                value={widget.data?.dateColumn === null ? "__none" : (widget.data?.dateColumn ?? "")}
                onChange={(value) => set({ data: { ...widget.data, dateColumn: value === "" ? undefined : value === "__none" ? null : value } })}
              >
                <option value="">Automatic (the date it is grouped by)</option>
                {columns.filter(isDateColumn).map((c) => (
                  <option key={c.name} value={c.name}>
                    {c.displayName ?? c.name}
                  </option>
                ))}
                <option value="__none">Nothing — ignore the range</option>
              </Select>
            ) : null}

            <section className="widget-editor__section">
              <h3>Display</h3>
              <Select label="Numbers as" value={widget.display?.format ?? "auto"} onChange={(value) => setDisplay({ format: value as WidgetDisplay["format"] })}>
                {FORMATS.map((f) => (
                  <option key={f.value} value={f.value}>
                    {f.label}
                  </option>
                ))}
              </Select>
              <div className="widget-editor__checks">
                {widget.type === "bar" || widget.type === "area" ? (
                  <label>
                    <input type="checkbox" checked={widget.display?.stacked ?? false} onChange={(event) => setDisplay({ stacked: event.target.checked })} />
                    Stacked
                  </label>
                ) : null}
                {widget.type === "kpi" ? (
                  <label>
                    <input type="checkbox" checked={widget.display?.compare !== false} onChange={(event) => setDisplay({ compare: event.target.checked })} />
                    Compare with the period before
                  </label>
                ) : null}
              </div>
            </section>
          </>
        )}
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
