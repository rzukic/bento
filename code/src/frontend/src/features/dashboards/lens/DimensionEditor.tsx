import type { DashboardWidget } from "../../../api/dashboards";
import type { MeasureShow, QueryBucket, QueryFunction, QueryGroupBy, QueryMeasure, SeriesColor, VariableValue } from "../../../api/query";
import { Icon } from "../../../components/common/Icon";
import { Button } from "../../../components/ui/Button";
import type { ModelSnapshot, ModelTable } from "../../../types/api";
import { MeasureFormulaField } from "../../formulas/MeasureFormulaField";
import { DEFAULT_WINDOW } from "../chartTransforms";
import { describeGroup, describeMeasure, fieldsOf, type SlotDef } from "./model";

const QUICK_FUNCTIONS: { value: QueryFunction; label: string }[] = [
  { value: "count", label: "Count" },
  { value: "sum", label: "Sum" },
  { value: "avg", label: "Average" },
  { value: "min", label: "Minimum" },
  { value: "max", label: "Maximum" },
  { value: "count_distinct", label: "Unique count" },
  { value: "median", label: "Median" },
  { value: "percentile", label: "Percentile" },
];

const SERIES_COLORS: [SeriesColor, string][] = [
  ["green", "Green (income)"],
  ["red", "Red (expenses)"],
  ["blue", "Blue"],
  ["teal", "Teal"],
  ["amber", "Amber"],
  ["violet", "Violet"],
  ["rose", "Rose"],
  ["gray", "Ink"],
];

/** Functions that need a number column. */
const NUMERIC: QueryFunction[] = ["sum", "avg", "median", "percentile"];

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
    NUMERIC.includes(fn)
      ? fields.filter((f) => f.kind === "number")
      : fn === "min" || fn === "max"
        ? fields.filter((f) => f.kind === "number" || f.kind === "date")
        : fields;

  const pick = (next: QueryFunction) => {
    const keeps = next === "count" || next === "count_distinct" || candidates.some((f) => f.name === measure.column);
    const nextCandidates = NUMERIC.includes(next) ? fields.filter((f) => f.kind === "number") : fields;
    const keepsColumn = keeps && (next === "count" || next === "count_distinct" || nextCandidates.some((f) => f.name === measure.column));
    const fallback = next === "count" ? undefined : nextCandidates[0]?.name;
    const { formula: _formula, percentile: _percentile, ...plain } = measure;
    onChange({
      ...plain,
      fn: next,
      column: next === "count" && !measure.column ? undefined : keepsColumn ? measure.column : fallback,
      ...(next === "percentile" ? { percentile: measure.percentile ?? 90 } : {}),
      // Running totals add up sums and counts only.
      ...(next === "sum" || next === "count" ? {} : { running: undefined, carryIn: undefined }),
    });
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
            onChange({
              name: measure.name,
              show: measure.show,
              window: measure.window,
              formula: !measure.column
                ? "COUNT()"
                : fn === "percentile"
                  ? `PERCENTILE(${measure.column}, ${measure.percentile ?? 90})`
                  : `${fn === "count_distinct" ? "COUNT" : fn.toUpperCase()}(${measure.column})`,
            })
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
          <span className="lens-field__hint">Over the rows of each group, e.g. SUM(hours WHERE status = "billable") / COUNT().</span>
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
          {fn === "percentile" ? (
            <label className="lens-field">
              <span className="lens-field__label">Percentile</span>
              <input
                className="field__control field__control--compact"
                type="number"
                min={0}
                max={100}
                aria-label="Percentile"
                value={measure.percentile ?? 90}
                onChange={(event) => onChange({ ...measure, percentile: Math.max(0, Math.min(100, Number(event.target.value) || 0)) })}
              />
              <span className="lens-field__hint">90: the value 90 % of the rows stay below. 50 is the median.</span>
            </label>
          ) : null}
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

      {byDate && isDateChart(widget) ? (
        <div className="lens-field">
          <label className="lens-field">
            <span className="lens-field__label">Show as</span>
            <select
              className="field__control field__control--compact"
              aria-label="Show as"
              value={measure.show ?? ""}
              onChange={(event) => {
                const show = (event.target.value || undefined) as MeasureShow | undefined;
                onChange({ ...measure, show, window: show === "moving_average" ? (measure.window ?? DEFAULT_WINDOW) : undefined });
              }}
            >
              <option value="">The value</option>
              <option value="change">Change from the period before</option>
              <option value="moving_average">Moving average</option>
            </select>
          </label>
          {measure.show === "moving_average" ? (
            <label className="lens-field">
              <span className="lens-field__label">Periods</span>
              <input
                className="field__control field__control--compact"
                type="number"
                min={2}
                max={24}
                aria-label="Periods"
                value={measure.window ?? DEFAULT_WINDOW}
                onChange={(event) => onChange({ ...measure, window: Math.max(2, Math.min(24, Math.round(Number(event.target.value)) || DEFAULT_WINDOW)) })}
              />
              <span className="lens-field__hint">Each point averages this period and the ones before; the first points stay empty.</span>
            </label>
          ) : null}
        </div>
      ) : null}

      {isDateChart(widget) && (widget.data?.query?.groupBy ?? []).length < 2 ? (
        <label className="lens-field">
          <span className="lens-field__label">Colour</span>
          <select
            className="field__control field__control--compact"
            aria-label="Colour"
            value={measure.color ?? ""}
            onChange={(event) => onChange({ ...measure, color: (event.target.value || undefined) as SeriesColor | undefined })}
          >
            <option value="">Automatic</option>
            {SERIES_COLORS.map(([value, label]) => (
              <option key={value} value={value}>
                {label}
              </option>
            ))}
          </select>
        </label>
      ) : null}

      {isDateChart(widget) && (widget.data?.query?.groupBy ?? []).length < 2 && (widget.data?.query?.measures ?? []).length > 1 ? (
        <div className="lens-field lens-field--row">
          <label className="lens-field">
            <span className="lens-field__label">Value axis</span>
            <select
              className="field__control field__control--compact"
              aria-label="Value axis"
              value={measure.axis ?? ""}
              onChange={(event) =>
                onChange({ ...measure, axis: event.target.value === "right" ? "right" : undefined, ...(event.target.value === "right" ? {} : { format: undefined }) })
              }
            >
              <option value="">Left</option>
              <option value="right">Right (its own scale)</option>
            </select>
          </label>
          {measure.axis === "right" ? (
            <label className="lens-field">
              <span className="lens-field__label">Written as</span>
              <select
                className="field__control field__control--compact"
                aria-label="Written as"
                value={measure.format ?? ""}
                onChange={(event) => onChange({ ...measure, format: (event.target.value || undefined) as typeof measure.format })}
              >
                <option value="">Like the chart</option>
                <option value="percent">Percent</option>
                <option value="number">Number</option>
                <option value="currency">Amount</option>
              </select>
            </label>
          ) : null}
        </div>
      ) : null}

      <label className="lens-field">
        <span className="lens-field__label">Name</span>
        <input className="field__control field__control--compact" value={measure.name} onChange={(event) => onChange({ ...measure, name: event.target.value })} />
      </label>
    </>
  );
}

function isDateChart(widget: DashboardWidget): boolean {
  return widget.type === "line" || widget.type === "area" || widget.type === "bar";
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
                  onChange={(event) => onChange({ ...group, rank: event.target.value as "asc" | "desc" | "abs" })}
                >
                  <option value="desc">Largest first</option>
                  <option value="asc">Smallest first (e.g. negative values)</option>
                  <option value="abs">Largest amount first (income and expenses)</option>
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
