import { useEffect, useMemo, useState, type DragEvent, type ReactNode } from "react";
import { createPortal } from "react-dom";
import type { DashboardAnnotation, DashboardVariable, DashboardWidget, ReferenceLine, TimeShift, WidgetDisplay, WidgetType } from "../../../api/dashboards";
import { listSavedQueries, type QueryResult, type SavedQuery } from "../../../api/query";
import { Icon } from "../../../components/common/Icon";
import { Button } from "../../../components/ui/Button";
import { formatKindForType, formatNumber, toNumber } from "../../../lib/format";
import type { ModelSnapshot, ModelTable } from "../../../types/api";
import type { ChartTokens } from "../charts/chartOptions";
import type { ResolvedRange } from "../dateRange";
import { variableValues, type VariableResults } from "../formulaQueries";
import { WIDGET_LABELS, dataTables, defaultQuery } from "../starter";
import { useWidgetResult } from "../useWidgetResult";
import { WidgetContent } from "../widgets/WidgetContent";
import { DimensionEditor } from "./DimensionEditor";
import { OPERATORS, filterToRows, measureAsFormula, rowsToFilter, type FilterRow } from "./filters";
import {
  addField,
  describeGroup,
  describeMeasure,
  describeWidget,
  fieldLabel,
  fieldsOf,
  isRowsTable,
  normalize,
  removeItem,
  setTableMode,
  slotIndexes,
  slotsFor,
  suggestions,
  switchType,
  updateGroup,
  updateMeasure,
  type FieldKind,
  type LensField,
  type SlotDef,
  type SlotId,
} from "./model";
import "./Lens.css";

const FIELD_DRAG = "application/x-bento-field";

const KIND_ICON: Record<FieldKind, "calendar" | "number" | "list" | "type"> = { date: "calendar", number: "number", category: "list", text: "type" };
const KIND_LABEL: Record<FieldKind, string> = { date: "Dates", number: "Numbers", category: "Categories", text: "Text" };

const FORMATS: { value: NonNullable<WidgetDisplay["format"]>; label: string }[] = [
  { value: "auto", label: "Automatic" },
  { value: "currency", label: "Amount" },
  { value: "number", label: "Number" },
  { value: "integer", label: "Whole number" },
  { value: "percent", label: "Percent" },
];

const TYPES: WidgetType[] = ["bar", "line", "area", "donut", "kpi", "table", "text"];

type Editing = { slot: SlotId; index: number } | null;

/**
 * The widget editor (Step 3.6b), modelled on Kibana Lens: the table's
 * fields on the left (drag them onto the chart or a slot), a large live
 * preview in the middle with suggestions, and on the right the chart's
 * slots — horizontal axis, vertical axis, breakdown; slices; value; rows —
 * with a settings panel per item, plus a Style tab.
 */
export function LensEditor({
  widget,
  tables,
  snapshot,
  variables = [],
  variableResults,
  annotations,
  range,
  refreshToken,
  tokens,
  onChange,
  onClose,
}: {
  widget: DashboardWidget;
  tables: ModelTable[];
  snapshot: ModelSnapshot | null;
  variables?: DashboardVariable[];
  variableResults?: VariableResults;
  /** The dashboard's events, shown in the preview and switchable per chart. */
  annotations?: DashboardAnnotation[];
  range: ResolvedRange;
  refreshToken: number;
  tokens: ChartTokens;
  onChange: (widget: DashboardWidget) => void;
  onClose: () => void;
}) {
  const [saved, setSaved] = useState<SavedQuery[]>([]);
  const [tab, setTab] = useState<"data" | "style">("data");
  const [editing, setEditing] = useState<Editing>(null);
  const [search, setSearch] = useState("");
  const [showData, setShowData] = useState(false);
  const values = useMemo(() => variableValues(variableResults), [variableResults]);

  useEffect(() => {
    listSavedQueries().then(setSaved).catch(() => setSaved([]));
  }, []);
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key !== "Escape") return;
      if (editing) setEditing(null);
      else onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [editing, onClose]);

  const sources = dataTables(tables);
  const query = widget.data?.query;
  const savedId = widget.data?.savedQueryId;
  const table = sources.find((t) => t.name === (query?.source ?? saved.find((s) => s.id === savedId)?.source));
  const fields = fieldsOf(table);
  const shown = fields.filter((f) => !search || f.label.toLowerCase().includes(search.toLowerCase()) || f.name.includes(search.toLowerCase()));
  const set = (next: DashboardWidget) => onChange(next);
  const setDisplay = (patch: Partial<WidgetDisplay>) => set({ ...widget, display: { ...widget.display, ...patch } });
  const usesQuery = widget.type !== "text" && !widget.data?.variable && savedId === undefined;

  const drop = (slot?: SlotId) => (event: DragEvent) => {
    event.preventDefault();
    event.stopPropagation();
    const field = fields.find((f) => f.name === event.dataTransfer.getData(FIELD_DRAG));
    if (field && table) set(addField(widget, field, table, slot));
  };
  const allowDrop = (event: DragEvent) => {
    if (event.dataTransfer.types.includes(FIELD_DRAG)) event.preventDefault();
  };

  const panel = (
    <div className="lens" role="dialog" aria-label="Edit widget">
      <div className="lens__sky" aria-hidden="true">
        <div className="lens__pool" />
        <svg className="lens__grain" xmlns="http://www.w3.org/2000/svg">
          <filter id="lens-grain">
            <feTurbulence type="fractalNoise" baseFrequency="0.9" numOctaves="2" stitchTiles="stitch" />
            <feColorMatrix type="saturate" values="0" />
          </filter>
          <rect width="100%" height="100%" filter="url(#lens-grain)" />
        </svg>
      </div>
      <header className="lens__header">
        <div className="lens__heading">
          <span className="lens__kicker">Widget</span>
          <input className="lens__title" aria-label="Title" value={widget.title} onChange={(event) => set({ ...widget, title: event.target.value })} />
        </div>
        <div className="lens__types">
          <div className="lens__types-track" role="radiogroup" aria-label="Shows as">
            {TYPES.map((type) => (
              <button
                key={type}
                type="button"
                role="radio"
                aria-checked={widget.type === type}
                className={widget.type === type ? "lens__type lens__type--active" : "lens__type"}
                onClick={() => {
                  setEditing(null);
                  if (type !== "text" && !widget.data && sources[0]) set({ ...widget, type, data: { query: defaultQuery(type, sources[0]) } });
                  else set(switchType(widget, type, table));
                }}
              >
                {WIDGET_LABELS[type]}
              </button>
            ))}
          </div>
        </div>
        <Button type="button" variant="accent" onClick={onClose}>
          Done
        </Button>
      </header>

      <div className="lens__body">
        <aside className="lens__fields" aria-label="Fields">
          <label className="lens-field">
            <span className="lens-field__label">Data from</span>
            <select
              className="field__control field__control--compact"
              aria-label="Data from"
              value={widget.data?.variable ? `var:${widget.data.variable}` : savedId !== undefined ? `saved:${savedId}` : query ? `table:${query.source}` : ""}
              onChange={(event) => {
                const value = event.target.value;
                setEditing(null);
                if (value.startsWith("var:")) set({ ...widget, type: "kpi", data: { variable: value.slice(4) } });
                else if (value.startsWith("saved:")) set({ ...widget, data: { savedQueryId: Number(value.slice(6)), dateColumn: widget.data?.dateColumn } });
                else {
                  const next = sources.find((t) => t.name === value.slice(6));
                  if (next) set({ ...widget, type: widget.type === "text" ? "bar" : widget.type, data: { query: defaultQuery(widget.type === "text" ? "bar" : widget.type, next) } });
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
              {variables.length > 0 ? (
                <optgroup label="Dashboard variable">
                  {variables.map((v) => (
                    <option key={v.name} value={`var:${v.name}`}>
                      {v.label || v.name}
                    </option>
                  ))}
                </optgroup>
              ) : null}
            </select>
          </label>
          {usesQuery && table ? (
            <>
              <input
                className="field__control field__control--compact"
                type="search"
                placeholder="Search fields"
                aria-label="Search fields"
                value={search}
                onChange={(event) => setSearch(event.target.value)}
              />
              <div className="lens__field-groups">
                {(["date", "number", "category", "text"] as FieldKind[]).map((kind) => {
                  const ofKind = shown.filter((f) => f.kind === kind);
                  if (ofKind.length === 0) return null;
                  return (
                    <section key={kind} className="lens__field-group">
                      <h3>{KIND_LABEL[kind]}</h3>
                      <ul>
                        {ofKind.map((field) => (
                          <FieldItem key={field.name} field={field} onAdd={() => set(addField(widget, field, table))} />
                        ))}
                      </ul>
                    </section>
                  );
                })}
              </div>
              <p className="lens__hint">Drag a field onto the chart or a slot, or use +.</p>
            </>
          ) : null}
        </aside>

        <main className="lens__workspace" onDragOver={allowDrop} onDrop={drop()}>
          <div className={`lens__preview lens__preview--${widget.type}`} aria-label="Preview">
            <WidgetContent widget={widget} range={range} refreshToken={refreshToken} tokens={tokens} variables={variableResults} annotations={annotations} />
          </div>
          {usesQuery ? <p className="lens__caption">{describeWidget(widget, table)}</p> : null}
          {usesQuery ? (
            <div className="lens__suggestions" aria-label="Suggestions">
              {suggestions(widget, table).map((suggestion) => (
                <button key={suggestion.type} type="button" className="lens__suggestion" onClick={() => set(suggestion)}>
                  <Icon name={suggestion.type === "kpi" ? "activity" : suggestion.type === "table" ? "table" : "dashboard"} size={14} />
                  {WIDGET_LABELS[suggestion.type]}
                </button>
              ))}
              {query && !isRowsTable(widget) ? (
                <button type="button" className="lens__suggestion lens__suggestion--data" aria-expanded={showData} onClick={() => setShowData((v) => !v)}>
                  <Icon name="table" size={14} /> {showData ? "Hide data" : "View data"}
                </button>
              ) : null}
            </div>
          ) : null}
          {showData && query ? <ResultData widget={widget} range={range} refreshToken={refreshToken} variables={values} /> : null}
        </main>

        <aside className="lens__config" aria-label="Widget settings">
          {editing && table ? (
            <EditingPanel
              editing={editing}
              widget={widget}
              table={table}
              snapshot={snapshot}
              variables={values}
              onChange={set}
              onClose={() => setEditing(null)}
            />
          ) : (
            <>
              <div className="lens-tabs" role="tablist" aria-label="Settings">
                <button type="button" role="tab" aria-selected={tab === "data"} className="lens-tabs__tab" onClick={() => setTab("data")}>
                  Data
                </button>
                <button type="button" role="tab" aria-selected={tab === "style"} className="lens-tabs__tab" onClick={() => setTab("style")}>
                  Style
                </button>
              </div>
              {tab === "data" ? (
                <DataSettings
                  widget={widget}
                  table={table}
                  usesQuery={usesQuery}
                  onChange={set}
                  onEdit={setEditing}
                  onDrop={drop}
                  allowDrop={allowDrop}
                />
              ) : (
                <StyleSettings widget={widget} table={table} variables={variables} annotationCount={annotations?.length ?? 0} setDisplay={setDisplay} onChange={set} />
              )}
            </>
          )}
        </aside>
      </div>
    </div>
  );

  return createPortal(panel, document.body);
}

function FieldItem({ field, onAdd }: { field: LensField; onAdd: () => void }) {
  return (
    <li
      className="lens__field"
      draggable
      onDragStart={(event) => {
        event.dataTransfer.setData(FIELD_DRAG, field.name);
        event.dataTransfer.effectAllowed = "copy";
      }}
      title={`${field.label} (${field.type}) — drag onto the chart or a slot`}
    >
      <Icon name={KIND_ICON[field.kind]} size={13} />
      <span className="lens__field-name">{field.label}</span>
      <button type="button" className="lens__field-add" aria-label={`Add ${field.label}`} onClick={onAdd}>
        <Icon name="plus" size={12} />
      </button>
    </li>
  );
}

function DataSettings({
  widget,
  table,
  usesQuery,
  onChange,
  onEdit,
  onDrop,
  allowDrop,
}: {
  widget: DashboardWidget;
  table: ModelTable | undefined;
  usesQuery: boolean;
  onChange: (widget: DashboardWidget) => void;
  onEdit: (editing: Editing) => void;
  onDrop: (slot?: SlotId) => (event: DragEvent) => void;
  allowDrop: (event: DragEvent) => void;
}) {
  if (widget.type === "text") {
    return (
      <label className="lens-field">
        <span className="lens-field__label">Text</span>
        <textarea
          className="field__control lens__textarea"
          value={widget.display?.text ?? ""}
          rows={10}
          onChange={(event) => onChange({ ...widget, display: { ...widget.display, text: event.target.value } })}
        />
        <span className="lens-field__hint"># heading, - list item</span>
      </label>
    );
  }
  if (widget.data?.variable) return <p className="lens__hint">Shows the dashboard variable "{widget.data.variable}". Variables are edited under Variables.</p>;
  if (!usesQuery || !table) return <p className="lens__hint">A saved query is used as it is. Choose a table to build the widget here.</p>;

  const query = widget.data!.query!;
  const groups = query.groupBy ?? [];
  const dateGroup = groups.find((g) => g.bucket);
  const filterRows = filterToRows(query.filter);
  const setFilter = (rows: FilterRow[]) => onChange({ ...widget, data: { ...widget.data, query: { ...query, filter: rowsToFilter(rows) } } });
  const fields = fieldsOf(table);
  const dateFields = fields.filter((f) => f.kind === "date");

  return (
    <div className="lens-settings">
      {widget.type === "table" ? (
        <div className="lens-segmented" role="radiogroup" aria-label="Table shows">
          <button type="button" role="radio" aria-checked={!isRowsTable(widget)} onClick={() => onChange(setTableMode(widget, false, table))}>
            Summarized
          </button>
          <button type="button" role="radio" aria-checked={isRowsTable(widget)} onClick={() => onChange(setTableMode(widget, true, table))}>
            Individual rows
          </button>
        </div>
      ) : null}

      {widget.type === "kpi" ? (
        <label className="lens-field">
          <span className="lens-field__label">For</span>
          <select
            className="field__control field__control--compact"
            aria-label="For"
            value={dateGroup ? `latest:${dateGroup.column}` : `range:${widget.data?.dateColumn ?? ""}`}
            onChange={(event) => {
              const [mode, column] = event.target.value.split(":");
              const next =
                mode === "latest"
                  ? { ...query, groupBy: [{ column, bucket: dateGroup?.bucket ?? ("month" as const), as: "period" }] }
                  : { ...query, groupBy: [] };
              onChange(normalize({ ...widget, data: { ...widget.data, query: next, dateColumn: mode === "range" ? column || null : undefined } }));
            }}
          >
            {dateFields.map((f) => (
              <option key={`r${f.name}`} value={`range:${f.name}`}>
                The whole range (by {f.label})
              </option>
            ))}
            {dateFields.map((f) => (
              <option key={`l${f.name}`} value={`latest:${f.name}`}>
                The latest period (by {f.label})
              </option>
            ))}
            <option value="range:">All rows, ignoring the range</option>
          </select>
        </label>
      ) : null}

      {slotsFor(widget).map((slot) => (
        <Slot key={slot.id} slot={slot} widget={widget} table={table} onChange={onChange} onEdit={onEdit} onDrop={onDrop(slot.id)} allowDrop={allowDrop} />
      ))}

      {dateGroup && widget.type !== "kpi" && widget.type !== "table" ? (
        <label className="lens-check" title="Each period shows the value at its last day, e.g. a balance or net worth">
          <input
            type="checkbox"
            checked={Boolean(widget.data?.asOf)}
            onChange={(event) => {
              const on = event.target.checked;
              const measures = on
                ? (query.measures ?? []).map((m) => ({ name: m.name, formula: measureAsFormula(m, dateGroup.column), show: m.show, window: m.window }))
                : query.measures;
              onChange(normalize({ ...widget, data: { ...widget.data, asOf: on || undefined, query: { ...query, measures } } }));
            }}
          />
          Value at the end of each period
        </label>
      ) : null}

      {dateGroup && (widget.type === "line" || widget.type === "area" || widget.type === "bar") && (query.groupBy ?? []).length === 1 ? (
        <label className="lens-field">
          <span className="lens-field__label">Compare with</span>
          <select
            className="field__control field__control--compact"
            aria-label="Compare with"
            value={widget.data?.timeShift ?? ""}
            onChange={(event) => onChange({ ...widget, data: { ...widget.data, timeShift: (event.target.value || undefined) as TimeShift | undefined } })}
          >
            <option value="">Nothing</option>
            <option value="previous">The period before</option>
            <option value="year">The same period a year earlier</option>
          </select>
          <span className="lens-field__hint">Each value again for the earlier range, dashed (or as lighter bars). Needs a dashboard range with a start and an end.</span>
        </label>
      ) : null}

      <section className="lens-section">
        <h3>Only rows where</h3>
        {filterRows.map((row, index) => {
          const operator = OPERATORS.find((o) => o.value === row.op);
          const update = (patch: Partial<FilterRow>) => setFilter(filterRows.map((r, i) => (i === index ? { ...r, ...patch } : r)));
          return (
            <div key={index} className="lens-condition">
              <select className="field__control field__control--compact" aria-label="Condition field" value={row.column} onChange={(event) => update({ column: event.target.value })}>
                {fields.map((f) => (
                  <option key={f.name} value={f.name}>
                    {f.label}
                  </option>
                ))}
              </select>
              <select className="field__control field__control--compact" aria-label="Condition" value={row.op} onChange={(event) => update({ op: event.target.value })}>
                {OPERATORS.map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </select>
              {operator?.needsValue !== false ? (
                <input className="field__control field__control--compact" aria-label="Value" value={row.value} onChange={(event) => update({ value: event.target.value })} />
              ) : null}
              <Button type="button" variant="ghost" size="sm" aria-label="Remove condition" onClick={() => setFilter(filterRows.filter((_, i) => i !== index))}>
                <Icon name="x" size={12} />
              </Button>
            </div>
          );
        })}
        <Button
          type="button"
          variant="ghost"
          size="sm"
          leadingIcon={<Icon name="plus" size={13} />}
          onClick={() => {
            const field = fields.find((f) => f.kind === "number") ?? fields[0];
            if (field) onChange({ ...widget, data: { ...widget.data, query: { ...query, filter: { ...(query.filter ?? {}), [field.name]: { ...(((query.filter ?? {})[field.name] as object) ?? {}), lt: 0 } } } } });
          }}
        >
          Add condition
        </Button>
      </section>

      {widget.type !== "kpi" && !widget.data?.asOf ? (
        <label className="lens-field">
          <span className="lens-field__label">Dashboard range applies to</span>
          <select
            className="field__control field__control--compact"
            aria-label="Dashboard range applies to"
            value={widget.data?.dateColumn === null ? "__none" : (widget.data?.dateColumn ?? "")}
            onChange={(event) => {
              const value = event.target.value;
              onChange({ ...widget, data: { ...widget.data, dateColumn: value === "" ? undefined : value === "__none" ? null : value } });
            }}
          >
            <option value="">{dateGroup ? `${fieldLabel(table, dateGroup.column)} (the axis)` : "Automatic"}</option>
            {dateFields.map((f) => (
              <option key={f.name} value={f.name}>
                {f.label}
              </option>
            ))}
            <option value="__none">Nothing — all rows</option>
          </select>
        </label>
      ) : null}
    </div>
  );
}

function Slot({
  slot,
  widget,
  table,
  onChange,
  onEdit,
  onDrop,
  allowDrop,
}: {
  slot: SlotDef;
  widget: DashboardWidget;
  table: ModelTable;
  onChange: (widget: DashboardWidget) => void;
  onEdit: (editing: Editing) => void;
  onDrop: (event: DragEvent) => void;
  allowDrop: (event: DragEvent) => void;
}) {
  const [over, setOver] = useState(false);
  const indexes = slotIndexes(widget, slot.id);
  const query = widget.data?.query;
  const label = (index: number): ReactNode => {
    if (slot.accepts === "column") return fieldLabel(table, widget.data?.rows?.columns[index]);
    if (slot.accepts === "measure") return describeMeasure(query!.measures![index], table);
    return describeGroup(query!.groupBy![index], table);
  };
  const full = slot.max !== undefined && indexes.length >= slot.max;

  const add = () => {
    const fields = fieldsOf(table);
    if (slot.accepts === "measure") {
      const field = fields.find((f) => f.kind === "number") ?? fields[0];
      if (!field) return;
      const next = addField(widget, field, table, slot.id);
      onChange(next);
      const count = (next.data?.query?.measures ?? []).length;
      onEdit({ slot: slot.id, index: slot.id === "secondary" ? 1 : slot.id === "size" || slot.id === "value" ? 0 : Math.max(0, count - 1) });
      return;
    }
    const preferred =
      slot.accepts === "column"
        ? fields.find((f) => !(widget.data?.rows?.columns ?? []).includes(f.name))
        : fields.find((f) => (slot.id === "x" ? f.kind === "date" : f.kind === "category")) ?? fields.find((f) => f.kind !== "number");
    if (!preferred) return;
    const next = addField(widget, preferred, table, slot.id);
    onChange(next);
    const after = slotIndexes(next, slot.id);
    onEdit({ slot: slot.id, index: after[after.length - 1] ?? 0 });
  };

  return (
    <section
      className={over ? "lens-slot lens-slot--over" : "lens-slot"}
      onDragOver={(event) => {
        allowDrop(event);
        setOver(true);
      }}
      onDragLeave={() => setOver(false)}
      onDrop={(event) => {
        setOver(false);
        onDrop(event);
      }}
      aria-label={slot.label}
    >
      <h3>{slot.label}</h3>
      {indexes.map((index) => (
        <div key={index} className="lens-slot__item">
          <button type="button" className="lens-slot__label" onClick={() => onEdit({ slot: slot.id, index })} aria-label={`Edit ${slot.label}: ${String(label(index))}`}>
            {label(index)}
          </button>
          <button type="button" className="lens-slot__remove" aria-label={`Remove ${String(label(index))}`} onClick={() => onChange(removeItem(widget, slot.id, index, table))}>
            <Icon name="x" size={12} />
          </button>
        </div>
      ))}
      {!full ? (
        <button type="button" className="lens-slot__add" onClick={add}>
          <Icon name="plus" size={12} /> {indexes.length === 0 ? slot.empty : "Add"}
        </button>
      ) : null}
    </section>
  );
}

function EditingPanel({
  editing,
  widget,
  table,
  snapshot,
  variables,
  onChange,
  onClose,
}: {
  editing: NonNullable<Editing>;
  widget: DashboardWidget;
  table: ModelTable;
  snapshot: ModelSnapshot | null;
  variables: Record<string, import("../../../api/query").VariableValue>;
  onChange: (widget: DashboardWidget) => void;
  onClose: () => void;
}) {
  const slot = slotsFor(widget).find((s) => s.id === editing.slot);
  const query = widget.data?.query;
  if (!slot) return null;
  const measure = slot.accepts === "measure" ? query?.measures?.[editing.index] : undefined;
  const group = slot.accepts === "group" ? query?.groupBy?.[editing.index] : undefined;
  const column = slot.accepts === "column" ? widget.data?.rows?.columns[editing.index] : undefined;
  if (!measure && !group && column === undefined) return null;
  return (
    <DimensionEditor
      slot={slot}
      widget={widget}
      table={table}
      snapshot={snapshot}
      variables={variables}
      measure={measure}
      group={group}
      column={column}
      onMeasure={(next) => onChange(updateMeasure(widget, editing.index, next))}
      onGroup={(next) => onChange(updateGroup(widget, editing.index, next))}
      onColumn={(next) =>
        onChange({
          ...widget,
          data: { ...widget.data, rows: { ...widget.data!.rows!, columns: widget.data!.rows!.columns.map((c, i) => (i === editing.index ? next : c)) } },
        })
      }
      onRemove={() => {
        onChange(removeItem(widget, editing.slot, editing.index, table));
        onClose();
      }}
      onClose={onClose}
    />
  );
}

function StyleSettings({
  widget,
  table,
  variables,
  annotationCount,
  setDisplay,
  onChange,
}: {
  widget: DashboardWidget;
  table: ModelTable | undefined;
  variables: DashboardVariable[];
  annotationCount: number;
  setDisplay: (patch: Partial<WidgetDisplay>) => void;
  onChange: (widget: DashboardWidget) => void;
}) {
  if (widget.type === "text") return <p className="lens__hint">Text widgets have no style settings.</p>;
  const display = widget.display ?? {};
  const hasBreakdown = (widget.data?.query?.groupBy ?? []).length > 1;
  const rows = widget.data?.rows;
  const goal = display.goal;
  const isChart = widget.type === "line" || widget.type === "area" || widget.type === "bar";
  return (
    <div className="lens-settings">
      {!rows ? (
        <label className="lens-field">
          <span className="lens-field__label">Numbers as</span>
          <select className="field__control field__control--compact" aria-label="Numbers as" value={display.format ?? "auto"} onChange={(event) => setDisplay({ format: event.target.value as WidgetDisplay["format"] })}>
            {FORMATS.map((f) => (
              <option key={f.value} value={f.value}>
                {f.label}
              </option>
            ))}
          </select>
        </label>
      ) : null}
      {(widget.type === "bar" || widget.type === "area") && hasBreakdown ? (
        <label className="lens-check">
          <input type="checkbox" checked={display.stacked ?? false} onChange={(event) => setDisplay({ stacked: event.target.checked })} />
          Stacked
        </label>
      ) : null}
      {(widget.type === "bar" || widget.type === "area") && hasBreakdown && display.stacked ? (
        <label className="lens-check" title="A line with the sum of the stack — the net when values are positive and negative">
          <input type="checkbox" checked={display.totalLine ?? false} onChange={(event) => setDisplay({ totalLine: event.target.checked || undefined })} />
          Total line
        </label>
      ) : null}
      {isChart && !hasBreakdown && (widget.data?.query?.measures ?? []).length === 1 ? (
        <label className="lens-check">
          <input type="checkbox" checked={display.colorBySign ?? false} onChange={(event) => setDisplay({ colorBySign: event.target.checked || undefined })} />
          Colour by sign (green above 0, red below)
        </label>
      ) : null}
      {isChart && (widget.data?.query?.groupBy ?? []).some((g) => g.bucket) ? (
        <label className="lens-check" title="The dashboard's annotations — set them under Annotations in the dashboard's toolbar">
          <input type="checkbox" checked={display.annotations !== false} onChange={(event) => setDisplay({ annotations: event.target.checked ? undefined : false })} />
          Show annotations{annotationCount > 0 ? ` (${annotationCount})` : ""}
        </label>
      ) : null}
      {isChart ? <ReferenceLines lines={display.referenceLines ?? []} variables={variables} onChange={(referenceLines) => setDisplay({ referenceLines: referenceLines.length > 0 ? referenceLines : undefined })} /> : null}
      {widget.type === "kpi" ? (
        <>
          <label className="lens-check">
            <input type="checkbox" checked={display.compare !== false} onChange={(event) => setDisplay({ compare: event.target.checked })} />
            Compare with the period before
          </label>
          {display.compare !== false ? (
            <label className="lens-check">
              <input type="checkbox" checked={display.higherIsBetter !== false} onChange={(event) => setDisplay({ higherIsBetter: event.target.checked })} />
              Higher is better (green when it goes up)
            </label>
          ) : null}
          {!widget.data?.variable ? (
            <label className="lens-check">
              <input type="checkbox" checked={display.trend ?? false} onChange={(event) => setDisplay({ trend: event.target.checked || undefined })} />
              Trend line
            </label>
          ) : null}
          <label className="lens-field">
            <span className="lens-field__label">Goal</span>
            <div className="lens-goal">
              <select
                className="field__control field__control--compact"
                aria-label="Goal kind"
                value={goal === undefined ? "" : typeof goal === "number" ? "number" : `var:${goal}`}
                onChange={(event) => {
                  const value = event.target.value;
                  setDisplay({ goal: value === "" ? undefined : value === "number" ? (typeof goal === "number" ? goal : 0) : value.slice(4) });
                }}
              >
                <option value="">None</option>
                <option value="number">A number</option>
                {variables.map((v) => (
                  <option key={v.name} value={`var:${v.name}`}>
                    Variable {v.name}
                  </option>
                ))}
              </select>
              {typeof goal === "number" ? (
                <input
                  className="field__control field__control--compact"
                  type="number"
                  aria-label="Goal value"
                  value={goal}
                  onChange={(event) => setDisplay({ goal: Number(event.target.value) })}
                />
              ) : null}
            </div>
          </label>
        </>
      ) : null}
      {widget.type === "table" && !rows ? (
        <label className="lens-check">
          <input type="checkbox" checked={display.summary ?? false} onChange={(event) => setDisplay({ summary: event.target.checked || undefined })} />
          Total row
        </label>
      ) : null}
      {rows ? (
        <>
          <label className="lens-field">
            <span className="lens-field__label">Sort by</span>
            <select
              className="field__control field__control--compact"
              aria-label="Sort rows by"
              value={rows.sort?.replace(/^-/, "") ?? ""}
              onChange={(event) =>
                onChange({ ...widget, data: { ...widget.data, rows: { ...rows, sort: event.target.value ? `${rows.sort?.startsWith("-") ? "-" : ""}${event.target.value}` : undefined } } })
              }
            >
              <option value="">As stored</option>
              {fieldsOf(table).map((f) => (
                <option key={f.name} value={f.name}>
                  {f.label}
                </option>
              ))}
            </select>
          </label>
          {rows.sort ? (
            <label className="lens-check">
              <input
                type="checkbox"
                checked={rows.sort.startsWith("-")}
                onChange={(event) => onChange({ ...widget, data: { ...widget.data, rows: { ...rows, sort: `${event.target.checked ? "-" : ""}${rows.sort!.replace(/^-/, "")}` } } })}
              />
              Newest / largest first
            </label>
          ) : null}
          <label className="lens-field">
            <span className="lens-field__label">Rows shown</span>
            <input
              className="field__control field__control--compact"
              type="number"
              min={1}
              max={1000}
              aria-label="Rows shown"
              value={rows.limit ?? 25}
              onChange={(event) => onChange({ ...widget, data: { ...widget.data, rows: { ...rows, limit: Math.max(1, Math.min(1000, Number(event.target.value) || 25)) } } })}
            />
          </label>
        </>
      ) : null}
    </div>
  );
}

/** Horizontal lines at a number or a dashboard variable, each with an optional label. */
function ReferenceLines({ lines, variables, onChange }: { lines: ReferenceLine[]; variables: DashboardVariable[]; onChange: (lines: ReferenceLine[]) => void }) {
  const update = (index: number, patch: Partial<ReferenceLine>) => onChange(lines.map((line, i) => (i === index ? { ...line, ...patch } : line)));
  return (
    <section className="lens-section" aria-label="Reference lines">
      <h3>Reference lines</h3>
      {lines.map((line, index) => (
        <div key={index} className="lens-reference">
          <select
            className="field__control field__control--compact"
            aria-label="Line at"
            value={typeof line.value === "number" ? "number" : `var:${line.value}`}
            onChange={(event) => update(index, { value: event.target.value === "number" ? 0 : event.target.value.slice(4) })}
          >
            <option value="number">A number</option>
            {variables.map((v) => (
              <option key={v.name} value={`var:${v.name}`}>
                Variable {v.name}
              </option>
            ))}
          </select>
          {typeof line.value === "number" ? (
            <input
              className="field__control field__control--compact"
              type="number"
              aria-label="Line value"
              value={line.value}
              onChange={(event) => update(index, { value: Number(event.target.value) })}
            />
          ) : (
            <span />
          )}
          <input
            className="field__control field__control--compact"
            aria-label="Line label"
            placeholder="Label, e.g. Budget"
            value={line.label ?? ""}
            onChange={(event) => update(index, { label: event.target.value || undefined })}
          />
          <Button type="button" variant="ghost" size="sm" aria-label="Remove reference line" onClick={() => onChange(lines.filter((_, i) => i !== index))}>
            <Icon name="x" size={12} />
          </Button>
        </div>
      ))}
      <Button type="button" variant="ghost" size="sm" leadingIcon={<Icon name="plus" size={13} />} onClick={() => onChange([...lines, { value: 0 }])}>
        Add reference line
      </Button>
    </section>
  );
}

/** The numbers behind the preview, as a plain table. */
function ResultData({
  widget,
  range,
  refreshToken,
  variables,
}: {
  widget: DashboardWidget;
  range: ResolvedRange;
  refreshToken: number;
  variables: Record<string, import("../../../api/query").VariableValue>;
}) {
  const state = useWidgetResult(widget, range, refreshToken, variables);
  const result: QueryResult | null = state.result;
  if (!result) return <p className="lens__hint">{state.error ?? "Loading…"}</p>;
  const columns = result.meta.columns;
  return (
    <div className="lens__data">
      <table>
        <thead>
          <tr>
            {columns.map((c) => (
              <th key={c.name}>{c.name}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {result.data.slice(0, 100).map((row, index) => (
            <tr key={index}>
              {columns.map((c) => {
                const number = c.role === "measure" ? toNumber(row[c.name]) : null;
                return <td key={c.name}>{number !== null ? formatNumber(number, formatKindForType(c.type)) : row[c.name] === null ? "—" : String(row[c.name])}</td>;
              })}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
