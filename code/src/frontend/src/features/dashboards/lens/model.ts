import type { DashboardWidget, WidgetType } from "../../../api/dashboards";
import type { AggregationQuery, QueryBucket, QueryFunction, QueryGroupBy, QueryMeasure } from "../../../api/query";
import { valueTypeOf } from "../../../lib/columns";
import type { ModelColumn, ModelTable } from "../../../types/api";

/**
 * The Lens-style editor's model (Step 3.6b): a widget is edited through
 * chart-specific slots (horizontal axis, vertical axis, breakdown, …) that
 * map onto the same widget JSON the dashboards already store. Everything
 * here is pure so the editor, drag and drop and suggestions share it.
 */

export type FieldKind = "date" | "number" | "category" | "text";

export type LensField = {
  name: string;
  label: string;
  /** The value type: computed columns by their result. */
  type: string;
  kind: FieldKind;
  column: ModelColumn;
};

export function fieldKind(type: string): FieldKind {
  if (type === "date" || type === "datetime") return "date";
  if (type === "integer" || type === "float" || type === "currency") return "number";
  if (type === "reference" || type === "enum" || type === "boolean") return "category";
  return "text";
}

/** The fields a widget can use: everything but the key and JSON. */
export function fieldsOf(table: ModelTable | undefined): LensField[] {
  return (table?.columns ?? [])
    .filter((c) => !c.primaryKey && c.type !== "json")
    .map((column) => {
      const type = valueTypeOf(column);
      return { name: column.name, label: column.displayName || column.name, type, kind: fieldKind(type), column };
    });
}

// ── Slots ─────────────────────────────────────────────────────────────

export type SlotId = "x" | "y" | "breakdown" | "slice" | "size" | "value" | "secondary" | "rows" | "metrics" | "columns";

export type SlotDef = {
  id: SlotId;
  label: string;
  accepts: "group" | "measure" | "column";
  /** How many items fit; undefined = several. */
  max?: number;
  empty: string;
};

export function isRowsTable(widget: DashboardWidget): boolean {
  return widget.type === "table" && Boolean(widget.data?.rows);
}

export function slotsFor(widget: DashboardWidget): SlotDef[] {
  switch (widget.type) {
    case "line":
    case "area":
    case "bar":
      return [
        { id: "x", label: "Horizontal axis", accepts: "group", max: 1, empty: "Drop a date or category" },
        { id: "y", label: "Vertical axis", accepts: "measure", empty: "Drop a number" },
        { id: "breakdown", label: "Breakdown", accepts: "group", max: 1, empty: "Drop a field to split the series" },
      ];
    case "donut":
      return [
        { id: "slice", label: "Slice by", accepts: "group", max: 1, empty: "Drop a category" },
        { id: "size", label: "Size by", accepts: "measure", max: 1, empty: "Drop a number" },
      ];
    case "kpi":
      return [
        { id: "value", label: "Value", accepts: "measure", max: 1, empty: "Drop a number" },
        { id: "secondary", label: "Secondary value", accepts: "measure", max: 1, empty: "Optional, e.g. a count" },
      ];
    case "table":
      return isRowsTable(widget)
        ? [{ id: "columns", label: "Columns", accepts: "column", empty: "Drop the fields to list" }]
        : [
            { id: "rows", label: "Rows", accepts: "group", max: 2, empty: "Drop a field to group the rows" },
            { id: "metrics", label: "Metrics", accepts: "measure", empty: "Drop a number" },
          ];
    default:
      return [];
  }
}

/** Where a slot's items live in the query: group or measure indexes. */
export function slotIndexes(widget: DashboardWidget, slot: SlotId): number[] {
  const groups = widget.data?.query?.groupBy ?? [];
  const measures = widget.data?.query?.measures ?? [];
  switch (slot) {
    case "x":
    case "slice":
      return groups.length > 0 ? [0] : [];
    case "breakdown":
      return groups.length > 1 ? [1] : [];
    case "rows":
      return groups.map((_, i) => i);
    case "y":
    case "metrics":
      return measures.map((_, i) => i);
    case "size":
    case "value":
      return measures.length > 0 ? [0] : [];
    case "secondary":
      return measures.length > 1 ? [1] : [];
    case "columns":
      return (widget.data?.rows?.columns ?? []).map((_, i) => i);
  }
}

// ── Labels ────────────────────────────────────────────────────────────

const FUNCTION_LABELS: Record<QueryFunction, string> = {
  count: "Count",
  sum: "Sum",
  avg: "Average",
  min: "Minimum",
  max: "Maximum",
  count_distinct: "Unique count",
};

export function fieldLabel(table: ModelTable | undefined, name: string | undefined): string {
  if (!name) return "";
  const column = table?.columns.find((c) => c.name === name);
  return column?.displayName || name;
}

export function describeMeasure(measure: QueryMeasure, table: ModelTable | undefined): string {
  if (measure.formula !== undefined) return measure.name || "Formula";
  const fn = FUNCTION_LABELS[measure.fn ?? "count"];
  const running = measure.running ? " (running)" : "";
  if (!measure.column) return `Count of rows${running}`;
  return `${fn} of ${fieldLabel(table, measure.column)}${running}`;
}

const BUCKET_LABELS: Record<QueryBucket, string> = { day: "day", week: "week", month: "month", quarter: "quarter", year: "year" };

export function describeGroup(group: QueryGroupBy, table: ModelTable | undefined): string {
  const label = fieldLabel(table, group.column);
  if (group.bucket) return `${label} per ${BUCKET_LABELS[group.bucket]}`;
  if (group.top) return `Top ${group.top} ${label}${group.other ? " + other" : ""}`;
  return label;
}

/** "Sum of Betrag (EUR) by Buchungstag per month, split by Kategorie" — the preview's caption. */
export function describeWidget(widget: DashboardWidget, table: ModelTable | undefined): string {
  if (isRowsTable(widget)) {
    const columns = widget.data?.rows?.columns ?? [];
    return columns.length > 0 ? `Rows of ${table?.displayName ?? table?.name ?? "the table"}: ${columns.map((c) => fieldLabel(table, c)).join(", ")}` : "";
  }
  const query = widget.data?.query;
  if (!query) return "";
  const measures = (query.measures ?? []).map((m) => describeMeasure(m, table));
  const groups = query.groupBy ?? [];
  let text = measures.join(", ") || "Count of rows";
  if (widget.type === "kpi") {
    text += groups[0]?.bucket ? `, latest ${groups[0].bucket}` : ", whole range";
    return text;
  }
  if (groups[0]) text += ` by ${describeGroup(groups[0], table)}`;
  if (groups[1]) text += `, split by ${describeGroup(groups[1], table)}`;
  return text;
}

// ── Editing ───────────────────────────────────────────────────────────

function uniqueName(base: string, taken: string[]): string {
  const clean = base.replace(/[^A-Za-z0-9_]/g, "_").toLowerCase() || "value";
  if (!taken.includes(clean)) return clean;
  let n = 2;
  while (taken.includes(`${clean}_${n}`)) n++;
  return `${clean}_${n}`;
}

function outputsOf(query: AggregationQuery): string[] {
  return [...(query.groupBy ?? []).map((g) => g.as ?? g.column), ...(query.measures ?? []).map((m) => m.name)];
}

/** A measure for a field: numbers are summed, other fields counted by distinct value. */
export function measureFor(field: LensField | null, query: AggregationQuery): QueryMeasure {
  const taken = outputsOf(query);
  if (!field) return { name: uniqueName("count", taken), fn: "count" };
  if (field.kind === "number") return { name: uniqueName(`sum_${field.name}`, taken), fn: "sum", column: field.name };
  return { name: uniqueName(`unique_${field.name}`, taken), fn: "count_distinct", column: field.name };
}

/** A group for a field: dates per month, categories with their top values on charts (a breakdown keeps five). */
export function groupFor(field: LensField, widgetType: WidgetType, query: AggregationQuery): QueryGroupBy {
  const taken = outputsOf(query);
  if (field.kind === "date") return { column: field.name, bucket: "month", as: uniqueName("month", taken) };
  const group: QueryGroupBy = { column: field.name };
  if (widgetType === "donut" || widgetType === "line" || widgetType === "area" || widgetType === "bar")
    return { ...group, top: widgetType === "donut" ? 6 : 10, other: true };
  return group;
}

/** A measure without its running total: the flag dropped, RUNNING_SUM(…) unwrapped. */
export function withoutTimeSeries(measure: QueryMeasure): QueryMeasure {
  const { running: _running, carryIn: _carryIn, ...rest } = measure;
  const wrapped = rest.formula?.match(/^\s*=?\s*RUNNING_SUM\s*\(([\s\S]*)\)\s*$/i);
  return wrapped ? { ...rest, formula: wrapped[1].trim() } : rest;
}

/**
 * Keeps a widget valid after every edit (options that no longer apply are
 * dropped, not kept hidden): running totals and values by period end need a
 * date group, top values never apply to dates, key figures group by a date
 * only, donuts by one field.
 */
export function normalize(widget: DashboardWidget): DashboardWidget {
  const data = widget.data;
  const query = data?.query;
  if (!data || !query) return widget;
  let groups = [...(query.groupBy ?? [])].map((g) => (g.bucket ? { column: g.column, bucket: g.bucket, as: g.as } : g));
  if (widget.type === "kpi") groups = groups.filter((g) => g.bucket).slice(0, 1);
  if (widget.type === "donut") groups = groups.slice(0, 1);
  if (widget.type !== "table") groups = groups.slice(0, 2);
  const byDate = groups.some((g) => g.bucket);
  let measures = query.measures ?? [];
  if (!byDate) measures = measures.map(withoutTimeSeries);
  if (widget.type === "kpi" || widget.type === "donut") measures = measures.slice(0, widget.type === "kpi" ? 2 : 1);
  // Top values rank by the first measure; they cannot combine with carry-in.
  if (groups.some((g) => g.top)) measures = measures.map(({ carryIn: _c, ...m }) => m);
  const outputs = new Set([...groups.map((g) => g.as ?? g.column), ...measures.map((m) => m.name)]);
  const sort = query.sort?.filter((s) => outputs.has(s.replace(/^-/, "")));
  return {
    ...widget,
    data: {
      ...data,
      asOf: byDate ? data.asOf : undefined,
      query: { ...query, groupBy: groups, measures, sort: sort && sort.length > 0 ? sort : undefined },
    },
  };
}

function withQuery(widget: DashboardWidget, query: AggregationQuery): DashboardWidget {
  return normalize({ ...widget, data: { ...widget.data, query, savedQueryId: undefined } });
}

/**
 * Puts a field into a slot — or, dropped on the chart, where it fits best:
 * a date on the horizontal axis, a number as a value, a category as the
 * axis or the breakdown.
 */
export function addField(widget: DashboardWidget, field: LensField, table: ModelTable, slot?: SlotId): DashboardWidget {
  if (isRowsTable(widget)) {
    const columns = widget.data?.rows?.columns ?? [];
    if (columns.includes(field.name)) return widget;
    return { ...widget, data: { ...widget.data, rows: { ...widget.data!.rows!, columns: [...columns, field.name] } } };
  }

  const query: AggregationQuery = widget.data?.query ?? { source: table.name, measures: [] };
  const groups = [...(query.groupBy ?? [])];
  const measures = [...(query.measures ?? [])];
  const target = slot ?? autoSlot(widget, field, groups);
  if (!target) return widget;

  const def = slotsFor(widget).find((s) => s.id === target);
  if (def?.accepts === "measure") {
    const measure = measureFor(field, query);
    // The automatic "count of rows" is a placeholder: the first real value replaces it.
    const placeholder = measures.length === 1 && measures[0].fn === "count" && !measures[0].column && measures[0].formula === undefined;
    if ((target === "y" || target === "metrics") && placeholder) measures[0] = measure;
    else if (target === "y" || target === "metrics") measures.push(measure);
    else if (target === "secondary") measures[1] = measure;
    else measures[0] = measure;
    if (measures.length === 0) measures.push(measureFor(null, query));
  } else if (def?.accepts === "group") {
    const group = groupFor(field, widget.type, query);
    if (target === "x" || target === "slice") groups[0] = group;
    else if (target === "breakdown") {
      if (groups.length === 0) groups.push({ column: field.name, ...(field.kind === "date" ? { bucket: "month" as QueryBucket, as: "month" } : {}) });
      // Each value is a line or a colour: five plus "Other" stay readable.
      groups[1] = group.top ? { ...group, top: 5 } : group;
    } else if (target === "rows") groups.push(group);
  }
  if (measures.length === 0) measures.push(measureFor(null, query));
  return withQuery(widget, { ...query, groupBy: groups, measures });
}

function autoSlot(widget: DashboardWidget, field: LensField, groups: QueryGroupBy[]): SlotId | null {
  switch (widget.type) {
    case "line":
    case "area":
    case "bar":
      if (field.kind === "number") return "y";
      if (field.kind === "date") return "x";
      return groups.length === 0 ? "x" : "breakdown";
    case "donut":
      return field.kind === "number" ? "size" : field.kind === "date" ? null : "slice";
    case "kpi":
      return field.kind === "number" ? "value" : null;
    case "table":
      return field.kind === "number" ? "metrics" : "rows";
    default:
      return null;
  }
}

/** Removes a slot item; an empty value list falls back to counting rows. */
export function removeItem(widget: DashboardWidget, slot: SlotId, index: number, table: ModelTable): DashboardWidget {
  if (slot === "columns") {
    const columns = (widget.data?.rows?.columns ?? []).filter((_, i) => i !== index);
    return { ...widget, data: { ...widget.data, rows: { ...widget.data!.rows!, columns } } };
  }
  const query: AggregationQuery = widget.data?.query ?? { source: table.name };
  const def = slotsFor(widget).find((s) => s.id === slot);
  if (def?.accepts === "measure") {
    const measures = (query.measures ?? []).filter((_, i) => i !== index);
    return withQuery(widget, { ...query, measures: measures.length > 0 ? measures : [measureFor(null, query)] });
  }
  // Removing the axis promotes the breakdown.
  const groups = (query.groupBy ?? []).filter((_, i) => i !== index);
  return withQuery(widget, { ...query, groupBy: groups });
}

export function updateMeasure(widget: DashboardWidget, index: number, measure: QueryMeasure): DashboardWidget {
  const query = widget.data!.query!;
  return withQuery(widget, { ...query, measures: (query.measures ?? []).map((m, i) => (i === index ? measure : m)) });
}

export function updateGroup(widget: DashboardWidget, index: number, group: QueryGroupBy): DashboardWidget {
  const query = widget.data!.query!;
  return withQuery(widget, { ...query, groupBy: (query.groupBy ?? []).map((g, i) => (i === index ? group : g)) });
}

/** A new chart type that keeps the fields: axis ↔ slices ↔ rows, values ↔ size ↔ metrics. */
export function switchType(widget: DashboardWidget, type: WidgetType, table: ModelTable | undefined): DashboardWidget {
  if (type === widget.type) return widget;
  if (type === "text") return { ...widget, type, data: undefined, display: { text: widget.display?.text ?? "" } };
  const query: AggregationQuery = widget.data?.query ?? { source: table?.name ?? "", measures: [{ name: "count", fn: "count" }] };
  let groups = [...(query.groupBy ?? [])];
  if (type === "donut") {
    const category = groups.find((g) => !g.bucket) ?? groups[0];
    groups = category ? [category.bucket ? category : { ...category, top: category.top ?? 6, other: category.other ?? true }] : [];
  }
  const next: DashboardWidget = {
    ...widget,
    type,
    data: { ...widget.data, rows: type === "table" ? widget.data?.rows : undefined, query: { ...query, groupBy: groups }, variable: undefined },
  };
  return normalize(next);
}

/** Other ways to show the same fields, best first (Lens' suggestions). */
export function suggestions(widget: DashboardWidget, table: ModelTable | undefined): DashboardWidget[] {
  const query = widget.data?.query;
  if (!query || widget.type === "text" || isRowsTable(widget)) return [];
  const groups = query.groupBy ?? [];
  const byDate = groups[0]?.bucket !== undefined;
  const types: WidgetType[] =
    groups.length === 0 ? ["kpi", "bar", "table"] : byDate ? ["line", "area", "bar", "kpi", "table"] : ["bar", "donut", "table", "kpi"];
  return types.filter((t) => t !== widget.type).slice(0, 4).map((t) => switchType(widget, t, table));
}

/** Table mode: summarized (groups and metrics) or the rows as they are. */
export function setTableMode(widget: DashboardWidget, rows: boolean, table: ModelTable | undefined): DashboardWidget {
  if (rows === isRowsTable(widget)) return widget;
  if (!rows) return { ...widget, data: { ...widget.data, rows: undefined } };
  const fields = fieldsOf(table);
  const fromQuery = [...(widget.data?.query?.groupBy ?? []).map((g) => g.column), ...(widget.data?.query?.measures ?? []).map((m) => m.column ?? "")];
  const columns = [...new Set(fromQuery.filter((c) => fields.some((f) => f.name === c)))];
  return {
    ...widget,
    data: {
      ...widget.data,
      rows: { columns: columns.length > 0 ? columns : fields.slice(0, 4).map((f) => f.name), limit: 25 },
    },
  };
}
