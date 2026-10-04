import type { ImportAnalysis, ImportAnalyzedColumn, ImportColumnMapping } from "../../../api/import";
import type { ModelTable, SchemaColumn, SchemaColumnType, SchemaModelDocument, SchemaRelation, SchemaTable } from "../../../types/api";
import { getDisplayColumnForTable } from "../components/editors/referenceMetadata";

/** Types a file column can become; links are chosen with `storeAs`. */
export const PLAIN_COLUMN_TYPES: SchemaColumnType[] = ["text", "integer", "float", "currency", "date", "datetime", "boolean"];

/** How a column is stored: its value, a new lookup table, or a link to an existing table. */
export type StoreAs = { kind: "value" } | { kind: "new-table"; table: string } | { kind: "existing-table"; table: string };

export type ColumnPlan = {
  index: number;
  header: string;
  include: boolean;
  name: string;
  type: SchemaColumnType;
  required: boolean;
  storeAs: StoreAs;
};

export type TablePlan = {
  name: string;
  displayName: string;
  columns: ColumnPlan[];
};

/** A lookup table to fill from one file column before the main import. */
export type LookupFill = { table: string; displayName: string; labelColumn: string; header: string };

export type BuiltTable = {
  model: SchemaModelDocument;
  mapping: ImportColumnMapping[];
  keyColumns: string[];
  lookups: LookupFill[];
};

const RESERVED = new Set([
  "id", "select", "from", "where", "group", "order", "by", "table", "column", "index", "user", "users", "join",
  "insert", "update", "delete", "create", "alter", "drop", "constraint", "primary", "key", "foreign", "references",
  "values", "limit", "offset",
]);

const MONEY_HEADER = /eur|€|usd|\$|chf|gbp|£|betrag|amount|preis|price|kosten|cost|saldo|balance|umsatz/i;
const CATEGORY_HEADER = /kategor|categor/i;

/** "Betrag (EUR)" → "betrag_eur", "Begünstigter" → "beguenstigter", "2. Name" → "c_2_name". */
export function toIdentifier(text: string): string {
  const base = text
    .replace(/ä/g, "ae").replace(/ö/g, "oe").replace(/ü/g, "ue")
    .replace(/Ä/g, "Ae").replace(/Ö/g, "Oe").replace(/Ü/g, "Ue")
    .replace(/ß/g, "ss")
    .normalize("NFD")
    .replace(/[̀-ͯ]/g, "")
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "_")
    .replace(/^_+|_+$/g, "")
    .slice(0, 60);
  if (!base) return "column";
  return /^[0-9]/.test(base) ? `c_${base}` : base;
}

/** A column name the schema accepts without quoting trouble ("id" is the table's own key). */
export function toColumnName(header: string): string {
  const name = toIdentifier(header);
  return RESERVED.has(name) ? `${name}_value` : name;
}

/** Appends _2, _3 … until the name is not taken (case-insensitive). */
export function uniqueName(name: string, taken: Iterable<string>): string {
  const used = new Set(Array.from(taken, (t) => t.toLowerCase()));
  if (!used.has(name.toLowerCase())) return name;
  let suffix = 2;
  while (used.has(`${name}_${suffix}`.toLowerCase())) suffix += 1;
  return `${name}_${suffix}`;
}

/** "girokonto-2026-09.csv" → "girokonto"; a free name if that one exists. */
export function suggestTableName(fileName: string, existing: string[]): string {
  const stem = fileName
    .replace(/\.[^.]+$/, "")
    .replace(/[-_ .]*\d{4}([-_ .]?\d{1,2}){0,2}$/, "")
    .replace(/[-_ .]*\d{1,2}[-_ .]\d{4}$/, "");
  const name = toIdentifier(stem || "imported");
  return uniqueName(name === "column" ? "imported" : name, existing);
}

/** "girokonto" → "Girokonto", "bank_umsaetze" → "Bank umsaetze". */
export function toDisplayName(name: string): string {
  const spaced = name.replace(/_/g, " ").trim();
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

function proposeType(column: ImportAnalyzedColumn): SchemaColumnType {
  const inferred = column.inferredType as SchemaColumnType;
  if (inferred === "float" && MONEY_HEADER.test(column.name)) return "currency";
  return PLAIN_COLUMN_TYPES.includes(inferred) ? inferred : "text";
}

/** Text columns with few distinct values can become their own table. */
export function canBeLookup(column: ImportAnalyzedColumn, rowCount: number): boolean {
  return column.inferredType === "text" && column.distinctCount >= 1 && column.distinctCount <= 100 && column.distinctCount < rowCount;
}

/**
 * A first proposal: every column with any value, cleaned-up names, the
 * detected types (amount-like headers as currency), required where no cell
 * is empty, and a lookup table for category columns.
 */
export function proposeTable(analysis: ImportAnalysis, fileName: string, existingTables: string[]): TablePlan {
  const name = suggestTableName(fileName, existingTables);
  const taken: string[] = ["id"];
  const reservedTables = [...existingTables, name];
  const columns = analysis.columns.map((column): ColumnPlan => {
    const isLookup = CATEGORY_HEADER.test(column.name) && canBeLookup(column, analysis.rowCount);
    const baseName = toColumnName(column.name);
    let storeAs: StoreAs = { kind: "value" };
    if (isLookup) {
      const lookupName = uniqueName(baseName, reservedTables);
      reservedTables.push(lookupName);
      storeAs = { kind: "new-table", table: lookupName };
    }
    const columnName = uniqueName(isLookup ? `${baseName}_id` : baseName, taken);
    taken.push(columnName);
    return {
      index: column.index,
      header: column.name,
      include: column.blank < analysis.rowCount,
      name: columnName,
      type: isLookup ? "reference" : proposeType(column),
      required: !isLookup && analysis.rowCount > 0 && column.blank === 0,
      storeAs,
    };
  });
  return { name, displayName: toDisplayName(name), columns };
}

/** Switching how a column is stored keeps its name in step ("kategorie" ↔ "kategorie_id"). */
export function withStoreAs(column: ColumnPlan, storeAs: StoreAs, fallbackType: SchemaColumnType): ColumnPlan {
  const isLink = storeAs.kind !== "value";
  const wasLink = column.storeAs.kind !== "value";
  let name = column.name;
  if (isLink && !wasLink && !name.endsWith("_id")) name = `${name}_id`;
  if (!isLink && wasLink && name.endsWith("_id")) name = name.slice(0, -3) || name;
  return {
    ...column,
    storeAs,
    name,
    type: isLink ? "reference" : column.type === "reference" ? fallbackType : column.type,
    required: isLink ? false : column.required,
  };
}

/** What stops the table from being created, in words for the page. */
export function validatePlan(plan: TablePlan, existingTables: string[]): string[] {
  const problems: string[] = [];
  const existing = new Set(existingTables.map((t) => t.toLowerCase()));
  const tableName = plan.name.trim();
  if (!tableName) problems.push("Give the table a name.");
  else if (existing.has(tableName.toLowerCase())) problems.push(`A table named "${tableName}" already exists.`);

  const included = plan.columns.filter((c) => c.include);
  if (included.length === 0) problems.push("Include at least one column.");

  const seen = new Set<string>(["id"]);
  for (const column of included) {
    const name = column.name.trim().toLowerCase();
    if (!name) problems.push(`The column from "${column.header}" needs a name.`);
    else if (seen.has(name)) problems.push(`The column name "${column.name}" is used twice${name === "id" ? " (id is the table's own key)" : ""}.`);
    seen.add(name);
  }

  const newTables = new Set<string>();
  for (const column of included) {
    if (column.storeAs.kind !== "new-table") continue;
    const lookup = column.storeAs.table.trim().toLowerCase();
    if (!lookup) problems.push(`The new table for "${column.header}" needs a name.`);
    else if (existing.has(lookup)) problems.push(`A table named "${column.storeAs.table}" already exists; link to it instead.`);
    else if (lookup === tableName.toLowerCase() || newTables.has(lookup)) problems.push(`The table name "${column.storeAs.table}" is used twice.`);
    newTables.add(lookup);
  }
  return problems;
}

/**
 * The current model plus the new table, its lookup tables and their
 * relations; the mapping and key columns for importing the file into it;
 * and the lookup tables to fill first.
 */
export function buildTable(current: SchemaModelDocument, plan: TablePlan, existingTables: ModelTable[]): BuiltTable {
  const tableName = plan.name.trim();
  const included = plan.columns.filter((c) => c.include);
  const newTables: SchemaTable[] = [];
  const relations: SchemaRelation[] = [];
  const lookups: LookupFill[] = [];
  const relationNames = new Set(current.relations.map((r) => r.name.toLowerCase()));

  const columns: SchemaColumn[] = [{ name: "id", type: "integer", primaryKey: true }];
  for (const column of included) {
    const name = column.name.trim();
    if (column.storeAs.kind === "value") {
      columns.push({ name, displayName: column.header, type: column.type, nullable: !column.required });
      continue;
    }

    let target = column.storeAs.table.trim();
    let labelColumn = "name";
    let displayName = column.header;
    if (column.storeAs.kind === "new-table") {
      newTables.push({
        name: target,
        displayName: column.header,
        columns: [
          { name: "id", type: "integer", primaryKey: true },
          { name: "name", displayName: "Name", type: "text", nullable: false, unique: true },
        ],
      });
    } else {
      const existing = existingTables.find((t) => t.name.toLowerCase() === target.toLowerCase());
      target = existing?.name ?? target;
      labelColumn = existing ? getDisplayColumnForTable(existing) : "name";
      displayName = existing?.displayName ?? target;
    }

    columns.push({ name, displayName: column.header, type: "reference", references: target, nullable: true });
    const relationName = uniqueName(`${target}_${tableName}`, relationNames);
    relationNames.add(relationName.toLowerCase());
    relations.push({ name: relationName, type: "1:N", from: target, to: tableName, column: name });
    // A table without a name column is linked by id; there is nothing to fill.
    if (labelColumn !== "id") lookups.push({ table: target, displayName, labelColumn, header: column.header });
  }

  const table: SchemaTable = { name: tableName, displayName: plan.displayName.trim() || toDisplayName(tableName), columns };
  return {
    model: {
      ...current,
      tables: [...current.tables, ...newTables, table],
      relations: [...current.relations, ...relations],
    },
    mapping: included.map((column) => ({ source: column.header, target: column.name.trim() })),
    keyColumns: included.filter((column) => column.storeAs.kind === "value").map((column) => column.name.trim()),
    lookups,
  };
}
