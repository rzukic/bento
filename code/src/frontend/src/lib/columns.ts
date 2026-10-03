import type { ModelColumn } from "../types/api";

/**
 * The type a column's values behave like: a computed column's result type
 * (filled in by the server when the schema is applied), otherwise its type.
 * Used to format values and to offer filters.
 */
export function valueTypeOf(column: Pick<ModelColumn, "type" | "resultType">): string {
  if (column.type === "computed") {
    return typeof column.resultType === "string" && column.resultType ? column.resultType : "text";
  }
  return column.type;
}
