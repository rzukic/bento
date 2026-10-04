import type { AggregationQuery, QueryMeasure } from "../../../api/query";

export type FilterRow = { column: string; op: string; value: string };

export const OPERATORS: { value: string; label: string; needsValue: boolean }[] = [
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

/** A plain measure as the formula it stands for (formulas are needed by period end). */
export function measureAsFormula(measure: QueryMeasure, dateColumn: string | null): string {
  if (measure.formula) return measure.formula;
  const until = dateColumn ? ` WHERE ${dateColumn} <= BUCKET_END()` : "";
  const fn = measure.fn === "count_distinct" ? "COUNT" : (measure.fn ?? "sum").toUpperCase();
  return measure.column && fn !== "COUNT" ? `${fn}(${measure.column}${until})` : `COUNT(${until.trim()})`;
}
