import type { DashboardWidget } from "../../../api/dashboards";
import type { QueryBucket, QueryFunction, QueryGroupBy, QueryMeasure, VariableValue } from "../../../api/query";
import { Icon } from "../../../components/common/Icon";
import { Button } from "../../../components/ui/Button";
import type { ModelSnapshot, ModelTable } from "../../../types/api";
import { MeasureFormulaField } from "../../formulas/MeasureFormulaField";
import { describeGroup, describeMeasure, fieldsOf, type SlotDef } from "./model";

const QUICK_FUNCTIONS: { value: QueryFunction; label: string }[] = [
  { value: "count", label: "Count" },
  { value: "sum", label: "Sum" },
  { value: "avg", label: "Average" },
  { value: "min", label: "Minimum" },
  { value: "max", label: "Maximum" },
  { value: "count_distinct", label: "Unique count" },
];

const INTERVALS: { value: QueryBucket; label: string }[] = [
  { value: "day", label: "Day" },
  { value: "week", label: "Week" },
  { value: "month", label: "Month" },
  { value: "quarter", label: "Quarter" },
  { value: "year", label: "Year" },
];

type Props = {
  slot: SlotDef;
  widget: DashboardWidget;
  table: ModelTable | undefined;
  snapshot: ModelSnapshot | null;
  variables: Record<string, VariableValue>;
  measure?: QueryMeasure;
  group?: QueryGroupBy;
  column?: string;
  onMeasure: (measure: QueryMeasure) => void;
  onGroup: (group: QueryGroupBy) => void;
  onColumn: (column: string) => void;
  onRemove: () => void;
  onClose: () => void;
};

/**
 * The settings of one slot item (Lens' dimension flyout): for a value its
 * quick function and field — or a formula —; for a group its field and
 * interval or top values; for a listed column the field.
 */
export function DimensionEditor({ slot, widget, table, snapshot, variables, measure, group, column, onMeasure, onGroup, onColumn, onRemove, onClose }: Props) {
  const fields = fieldsOf(table);
  const byDate = (widget.data?.query?.groupBy ?? []).some((g) => g.bucket);
  const title = measure ? describeMeasure(measure, table) : group ? describeGroup(group, table) : column ? fields.find((f) => f.name === column)?.label : slot.label;

  return (
    <div className="lens-dimension">
      <header className="lens-dimension__header">
        <Button type="button" variant="ghost" size="sm" leadingIcon={<Icon name="chevron-left" size={13} />} onClick={onClose}>
          {slot.label}
        </Button>
        <span className="lens-dimension__title">{title}</span>
      </header>

      {measure ? (
        <MeasureSettings measure={measure} widget={widget} table={table} snapshot={snapshot} variables={variables} byDate={byDate} onChange={onMeasure} />
      ) : null}

      {group ? <GroupSettings group={group} table={table} onChange={onGroup} /> : null}

      {column !== undefined ? (
        <label className="lens-field">
          <span className="lens-field__label">Field</span>
          <select className="field__control field__control--compact" value={column} onChange={(event) => onColumn(event.target.value)}>
            {fields.map((f) => (
              <option key={f.name} value={f.name}>
                {f.label}
              </option>
            ))}
          </select>
        </label>
      ) : null}

      <footer className="lens-dimension__footer">
        <Button type="button" variant="ghost" size="sm" leadingIcon={<Icon name="trash" size={13} />} onClick={onRemove}>
          Remove
        </Button>
        <Button type="button" variant="primary" size="sm" onClick={onClose}>
          Done
        </Button>
      </footer>
    </div>
  );
}

function MeasureSettings({
  measure,
  widget,
  table,
  snapshot,
  variables,
  byDate,
  onChange,
}: {
  measure: QueryMeasure;
  widget: DashboardWidget;
  table: ModelTable | undefined;
  snapshot: ModelSnapshot | null;
  variables: Record<string, VariableValue>;
  byDate: boolean;
  onChange: (measure: QueryMeasure) => void;
}) {
  const fields = fieldsOf(table);
  const isFormula = measure.formula !== undefined;
  const fn = measure.fn ?? "count";
  const candidates =
    fn === "sum" || fn === "avg"
      ? fields.filter((f) => f.kind === "number")
      : fn === "min" || fn === "max"
        ? fields.filter((f) => f.kind === "number" || f.kind === "date")
        : fields;

  const pick = (next: QueryFunction) => {
    const keeps = next === "count" || next === "count_distinct" || candidates.some((f) => f.name === measure.column);
    const fallback = next === "count" ? undefined : (next === "sum" || next === "avg" ? fields.find((f) => f.kind === "number") : fields[0])?.name;
    const { formula: _formula, ...plain } = measure;
    onChange({ ...plain, fn: next, column: next === "count" && !measure.column ? undefined : keeps ? measure.column : fallback });
  };

  return (
    <>
      <div className="lens-tabs" role="tablist" aria-label="Value kind">
        <button type="button" role="tab" aria-selected={!isFormula} className="lens-tabs__tab" onClick={() => isFormula && pick("sum")}>
          Quick function
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={isFormula}
          className="lens-tabs__tab"
          onClick={() =>
            !isFormula &&
            onChange({ name: measure.name, formula: measure.column ? `${fn === "count_distinct" ? "COUNT" : fn.toUpperCase()}(${measure.column})` : "COUNT()" })
          }
        >
          Formula
        </button>
      </div>

      {isFormula ? (
        <div className="lens-field">
          <span className="lens-field__label">Formula</span>
          <MeasureFormulaField
            table={widget.data?.query?.source}
            snapshot={snapshot}
            value={measure.formula ?? ""}
            onChange={(formula) => onChange({ ...measure, formula })}
            asOf={Boolean(widget.data?.asOf)}
            variables={variables}
            ariaLabel={`Formula of ${measure.name}`}
          />
          <span className="lens-field__hint">Over the rows of each group, e.g. SUM(amount WHERE amount &lt; 0) / COUNT().</span>
        </div>
      ) : (
        <>
          <div className="lens-functions" role="group" aria-label="Function">
            {QUICK_FUNCTIONS.map((option) => (
              <button
                key={option.value}
                type="button"
                className={fn === option.value ? "lens-functions__item lens-functions__item--active" : "lens-functions__item"}
                aria-pressed={fn === option.value}
                onClick={() => pick(option.value)}
              >
                {option.label}
              </button>
            ))}
          </div>
          <label className="lens-field">
            <span className="lens-field__label">Field</span>
            <select
              className="field__control field__control--compact"
              aria-label="Field"
              value={measure.column ?? ""}
              onChange={(event) => onChange({ ...measure, column: event.target.value || undefined })}
            >
              {fn === "count" ? <option value="">Rows</option> : null}
              {candidates.map((f) => (
                <option key={f.name} value={f.name}>
                  {f.label}
                </option>
              ))}
            </select>
          </label>
          {byDate && (fn === "sum" || fn === "count") ? (
            <label className="lens-check">
              <input type="checkbox" checked={measure.running ?? false} onChange={(event) => onChange({ ...measure, running: event.target.checked || undefined, carryIn: undefined })} />
              Running total
            </label>
          ) : null}
          {measure.running ? (
            <label className="lens-check" title="Start from everything before the date range — for balances">
              <input type="checkbox" checked={measure.carryIn ?? false} onChange={(event) => onChange({ ...measure, carryIn: event.target.checked || undefined })} />
              Include earlier rows
            </label>
          ) : null}
        </>
      )}

      <label className="lens-field">
        <span className="lens-field__label">Name</span>
        <input className="field__control field__control--compact" value={measure.name} onChange={(event) => onChange({ ...measure, name: event.target.value })} />
      </label>
    </>
  );
}

function GroupSettings({ group, table, onChange }: { group: QueryGroupBy; table: ModelTable | undefined; onChange: (group: QueryGroupBy) => void }) {
  const fields = fieldsOf(table);
  const field = fields.find((f) => f.name === group.column);
  const isDate = field?.kind === "date";
  return (
    <>
      <label className="lens-field">
        <span className="lens-field__label">Field</span>
        <select
          className="field__control field__control--compact"
          aria-label="Field"
          value={group.column}
          onChange={(event) => {
            const next = fields.find((f) => f.name === event.target.value);
            if (!next) return;
            onChange(next.kind === "date" ? { column: next.name, bucket: group.bucket ?? "month", as: group.as ?? "month" } : { column: next.name, top: group.top, other: group.other, rank: group.rank });
          }}
        >
          {fields.map((f) => (
            <option key={f.name} value={f.name}>
              {f.label}
            </option>
          ))}
        </select>
      </label>
      {isDate ? (
        <label className="lens-field">
          <span className="lens-field__label">Interval</span>
          <select
            className="field__control field__control--compact"
            aria-label="Interval"
            value={group.bucket ?? "month"}
            onChange={(event) => onChange({ ...group, bucket: event.target.value as QueryBucket })}
          >
            {INTERVALS.map((i) => (
              <option key={i.value} value={i.value}>
                {i.label}
              </option>
            ))}
          </select>
        </label>
      ) : (
        <>
          <label className="lens-field">
            <span className="lens-field__label">Values</span>
            <select
              className="field__control field__control--compact"
              aria-label="Values"
              value={group.top ? String(group.top) : ""}
              onChange={(event) => {
                const top = event.target.value ? Number(event.target.value) : undefined;
                onChange(top ? { ...group, top, other: group.other ?? true } : { column: group.column, as: group.as });
              }}
            >
              <option value="">All values</option>
              {[3, 5, 8, 10, 15, 20, 50].map((n) => (
                <option key={n} value={n}>
                  Top {n}
                </option>
              ))}
            </select>
          </label>
          {group.top ? (
            <>
              <label className="lens-field">
                <span className="lens-field__label">Ranked by the first value</span>
                <select
                  className="field__control field__control--compact"
                  aria-label="Rank"
                  value={group.rank ?? "desc"}
                  onChange={(event) => onChange({ ...group, rank: event.target.value as "asc" | "desc" })}
                >
                  <option value="desc">Largest first</option>
                  <option value="asc">Smallest first (e.g. spending)</option>
                </select>
              </label>
              <label className="lens-check">
                <input type="checkbox" checked={group.other ?? false} onChange={(event) => onChange({ ...group, other: event.target.checked || undefined })} />
                Group the rest as "Other"
              </label>
            </>
          ) : null}
        </>
      )}
    </>
  );
}
