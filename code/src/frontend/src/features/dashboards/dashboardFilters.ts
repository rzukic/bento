import type { DashboardFilter } from "../../api/dashboards";
import type { QueryFilter } from "../../api/query";
import type { FilterClause } from "../../types/explorer";
import type { ModelTable } from "../../types/api";
import { valueTypeOf } from "../../lib/columns";
import type { AppliedFilter, ResolvedRange } from "./dateRange";

// Step 3.8 (Part 9 R9.6): a filter bar above the widgets. A filter names a
// column; it narrows every widget, variable and drill-down whose table has
// that column. The chosen values live in the address (?filter.konto_id=1),
// so a bookmark keeps them.

const PARAM = "filter.";

/** Column types a filter offers values for (one of a list, not a range). */
const FILTERABLE = new Set(["reference", "enum", "text", "boolean", "integer"]);

export function filterableColumns(tables: ModelTable[]): { column: string; label: string; tables: string[] }[] {
  const byName = new Map<string, { column: string; label: string; tables: string[] }>();
  for (const table of tables) {
    for (const column of table.columns) {
      if (column.primaryKey || !FILTERABLE.has(valueTypeOf(column))) continue;
      const entry = byName.get(column.name) ?? { column: column.name, label: column.displayName || column.name, tables: [] };
      entry.tables.push(table.name);
      byName.set(column.name, entry);
    }
  }
  return [...byName.values()].sort((a, b) => a.label.localeCompare(b.label));
}

/** The tables that have a column of this name. */
export function tablesWithColumn(tables: ModelTable[], column: string): string[] {
  return tables.filter((t) => t.columns.some((c) => c.name === column)).map((t) => t.name);
}

/** The chosen values per filter column, from the address. */
export function selectionFromSearch(params: URLSearchParams): Record<string, string[]> {
  const selection: Record<string, string[]> = {};
  for (const [key, value] of params.entries()) {
    if (!key.startsWith(PARAM) || value === "") continue;
    const column = key.slice(PARAM.length);
    (selection[column] ??= []).push(value);
  }
  return selection;
}

export function selectionToSearch(column: string, values: string[], params: URLSearchParams): URLSearchParams {
  const next = new URLSearchParams(params);
  next.delete(`${PARAM}${column}`);
  for (const value of values) next.append(`${PARAM}${column}`, value);
  return next;
}

export function clearSelection(params: URLSearchParams): URLSearchParams {
  const next = new URLSearchParams(params);
  for (const key of [...next.keys()]) if (key.startsWith(PARAM)) next.delete(key);
  return next;
}

/** The dashboard's filters with values chosen, typed like their columns (ids stay numbers). */
export function appliedFilters(filters: DashboardFilter[] | undefined, selection: Record<string, string[]>, tables: ModelTable[]): AppliedFilter[] {
  return (filters ?? []).flatMap((filter) => {
    const chosen = selection[filter.column] ?? [];
    if (chosen.length === 0) return [];
    const on = tablesWithColumn(tables, filter.column);
    const column = tables.flatMap((t) => t.columns).find((c) => c.name === filter.column);
    const kind = column ? valueTypeOf(column) : "text";
    const values = chosen.map((v) =>
      kind === "boolean" ? v === "true" : (kind === "reference" || kind === "integer") && /^-?\d+$/.test(v) ? Number(v) : v,
    );
    return [{ column: filter.column, values, tables: on }];
  });
}

/** The query filter a range's chosen filters add on this table. */
export function globalFilterFor(source: string | undefined, range: ResolvedRange): QueryFilter | undefined {
  const parts = (range.filters ?? []).filter((f) => source && f.tables.includes(source) && f.values.length > 0);
  if (parts.length === 0) return undefined;
  return Object.fromEntries(parts.map((f) => [f.column, f.values.length === 1 ? { eq: f.values[0] } : { in: f.values }]));
}

/** The same as explorer filters (list endpoint, drill-down). */
export function globalFilterClauses(source: string | undefined, range: ResolvedRange): FilterClause[] {
  return (range.filters ?? [])
    .filter((f) => source && f.tables.includes(source) && f.values.length > 0)
    .map((f) => (f.values.length === 1 ? { column: f.column, op: "eq" as const, value: String(f.values[0]) } : { column: f.column, op: "in" as const, value: f.values.join(",") }));
}

/** Two query filters as one: conditions on the same column all apply. */
export function mergeFilters(a: QueryFilter | undefined, b: QueryFilter | undefined): QueryFilter | undefined {
  if (!a) return b;
  if (!b) return a;
  const merged: QueryFilter = { ...a };
  for (const [column, condition] of Object.entries(b)) merged[column] = mergeCondition(merged[column], condition);
  return merged;
}

/** A plain value means equals; two object conditions combine. */
export function mergeCondition(existing: unknown, condition: unknown): unknown {
  if (existing === undefined) return condition;
  const asObject = (value: unknown) =>
    value !== null && typeof value === "object" && !Array.isArray(value) ? (value as Record<string, unknown>) : value === null ? { null: true } : { eq: value };
  return { ...asObject(existing), ...asObject(condition) };
}
