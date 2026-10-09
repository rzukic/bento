import { isSection, type FormDefinition, type FormField, type FormItem } from "../../api/forms";
import type { ModelColumn, ModelTable } from "../../types/api";
import { parseLenientNumber } from "../data-explorer/components/editors/numericParsing";
import { getReferenceTableName } from "../data-explorer/components/editors/referenceMetadata";

// Step 4.4: a form is a table's columns in an order, with labels, sections,
// hidden fields and defaults (Form JSON, stored by the API). This module turns
// it into inputs and back into a row for the data API.

export type FieldKind = "reference" | "enum" | "boolean" | "date" | "datetime" | "integer" | "number" | "json" | "text";

const INTEGER_TYPES = new Set(["integer", "int64", "number"]);
const NUMBER_TYPES = new Set(["float", "double", "decimal", "currency"]);

export function fieldKind(column: ModelColumn): FieldKind {
  if (column.references || column.type === "relation") return "reference";
  if (column.type === "enum") return "enum";
  if (column.type === "boolean") return "boolean";
  if (column.type === "date") return "date";
  if (column.type === "datetime") return "datetime";
  if (INTEGER_TYPES.has(column.type)) return "integer";
  if (NUMBER_TYPES.has(column.type)) return "number";
  if (column.type === "json") return "json";
  return "text";
}

export function isNumeric(column: ModelColumn): boolean {
  const kind = fieldKind(column);
  return kind === "integer" || kind === "number";
}

/** Columns a form can fill: not computed, and not a key the database assigns (as the API checks). */
export function isFormWritable(column: ModelColumn): boolean {
  if (column.type === "computed") return false;
  if (column.primaryKey && (INTEGER_TYPES.has(column.type) || column.type === "uuid")) return false;
  return true;
}

/** Columns a new row must get: not nullable and without a default. */
export function isRequired(column: ModelColumn): boolean {
  return isFormWritable(column) && column.nullable === false && (column.default === undefined || column.default === null);
}

export function columnLabel(column: ModelColumn, field?: FormField): string {
  return field?.label?.trim() || column.displayName || column.name;
}

/**
 * The form a table gets before anyone changes it: every column it can fill,
 * in the table's order. Dates start today and links at the value used last time.
 */
export function defaultForm(table: ModelTable): FormDefinition {
  const fields: FormItem[] = table.columns.filter(isFormWritable).map((column) => {
    const kind = fieldKind(column);
    if (kind === "date") return { column: column.name, default: { kind: "today" } };
    if (kind === "datetime") return { column: column.name, default: { kind: "now" } };
    if (kind === "reference") return { column: column.name, default: { kind: "last" } };
    return { column: column.name };
  });
  return { table: table.name, fields };
}

/** The form's fields with their columns; fields whose column is gone are left out. */
export function formFields(form: FormDefinition, table: ModelTable): { field: FormField; column: ModelColumn }[] {
  return form.fields.flatMap((item) => {
    if (isSection(item)) return [];
    const column = table.columns.find((c) => c.name === item.column);
    return column && isFormWritable(column) ? [{ field: item, column }] : [];
  });
}

/** What the inputs hold while the form is filled: text for typed values, the value for pickers. */
export type FormValues = Record<string, unknown>;
export type FormSigns = Record<string, "negative" | "positive">;

const pad = (n: number) => String(n).padStart(2, "0");

export function todayIso(now = new Date()): string {
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

export function shiftDays(iso: string, days: number): string {
  const [y, m, d] = iso.split("-").map(Number);
  return todayIso(new Date(y!, m! - 1, d! + days));
}

export function nowLocal(now = new Date()): string {
  return `${todayIso(now)}T${pad(now.getHours())}:${pad(now.getMinutes())}`;
}

// ── Values used last time, per form and column, in this browser ──

const LAST_KEY = (formKey: string) => `bento.form.last.${formKey}`;

export function readLastValues(formKey: string): Record<string, unknown> {
  try {
    const raw = window.localStorage.getItem(LAST_KEY(formKey));
    const parsed: unknown = raw ? JSON.parse(raw) : {};
    return parsed && typeof parsed === "object" ? (parsed as Record<string, unknown>) : {};
  } catch {
    return {};
  }
}

export function writeLastValues(formKey: string, row: Record<string, unknown>): void {
  try {
    window.localStorage.setItem(LAST_KEY(formKey), JSON.stringify(row));
  } catch {
    // Private mode or storage full: the form still works, it just forgets.
  }
}

/** A value as the input shows it: numbers as text in the user's notation, the magnitude when a sign switch shows the sign. */
function inputValue(column: ModelColumn, field: FormField, value: unknown): unknown {
  if (value === null || value === undefined) return "";
  if (isNumeric(column) && typeof value === "number") {
    const shown = field.sign ? Math.abs(value) : value;
    return shown.toLocaleString(undefined, { maximumFractionDigits: 10, useGrouping: false });
  }
  if (fieldKind(column) === "json" && typeof value !== "string") return JSON.stringify(value);
  return value;
}

/** Each field's starting value and sign, from its default. */
export function initialState(form: FormDefinition, table: ModelTable, last: Record<string, unknown>, now = new Date()): { values: FormValues; signs: FormSigns } {
  const values: FormValues = {};
  const signs: FormSigns = {};
  for (const { field, column } of formFields(form, table)) {
    let value: unknown;
    switch (field.default?.kind) {
      case "today":
        value = fieldKind(column) === "datetime" ? `${todayIso(now)}T00:00` : todayIso(now);
        break;
      case "now":
        value = fieldKind(column) === "date" ? todayIso(now) : nowLocal(now);
        break;
      case "last":
        value = last[column.name] ?? null;
        break;
      case "value":
        value = field.default.value;
        break;
      default:
        value = null;
    }
    if (field.sign) {
      const lastSign = field.default?.kind === "last" && typeof value === "number" ? (value < 0 ? "negative" : "positive") : null;
      signs[column.name] = lastSign ?? field.sign;
    }
    values[column.name] = inputValue(column, field, value);
  }
  return { values, signs };
}

export type BuiltRow = { row: Record<string, unknown>; errors: Record<string, string> };

/**
 * The row to create: typed values from the inputs, signs applied. Empty
 * values are left out (the database fills its default or leaves them empty);
 * a required column without a value is an error before anything is sent.
 */
export function buildRow(form: FormDefinition, table: ModelTable, values: FormValues, signs: FormSigns): BuiltRow {
  const row: Record<string, unknown> = {};
  const errors: Record<string, string> = {};
  for (const { field, column } of formFields(form, table)) {
    const raw = values[column.name];
    const empty = raw === null || raw === undefined || (typeof raw === "string" && raw.trim() === "");
    if (empty) {
      if (isRequired(column)) errors[column.name] = `${columnLabel(column, field)} is required.`;
      continue;
    }
    const kind = fieldKind(column);
    if (kind === "integer" || kind === "number") {
      const parsed = typeof raw === "number" ? raw : parseLenientNumber(String(raw), { integer: kind === "integer" });
      if (parsed === null) {
        errors[column.name] = kind === "integer" ? "Enter a whole number." : "Enter a number, e.g. 12,50.";
        continue;
      }
      const sign = signs[column.name];
      row[column.name] = sign ? (sign === "negative" ? -Math.abs(parsed) : Math.abs(parsed)) : parsed;
    } else if (kind === "json") {
      try {
        row[column.name] = typeof raw === "string" ? JSON.parse(raw) : raw;
      } catch {
        errors[column.name] = "Enter valid JSON.";
      }
    } else if (kind === "text" && typeof raw === "string") {
      row[column.name] = raw.trim();
    } else {
      row[column.name] = raw;
    }
  }
  return { row, errors };
}

/** Required columns of the table the form does not ask for or fill: the form cannot add rows. */
export function missingRequired(form: FormDefinition, table: ModelTable): ModelColumn[] {
  const filled = new Set(formFields(form, table).map(({ column }) => column.name));
  return table.columns.filter((c) => isRequired(c) && !filled.has(c.name));
}

/** The table a reference field links to. */
export function referenceTable(column: ModelColumn): string | null {
  return getReferenceTableName(column) ?? (typeof column.references === "string" ? column.references : null);
}
