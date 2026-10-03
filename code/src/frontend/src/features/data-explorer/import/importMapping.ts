import type { ImportColumnMapping, ImportProfileConfig } from "../../../api/import";
import type { ModelColumn, ModelTable } from "../../../types/api";

/** Column name → file header; "" means the column is not imported. */
export type MappingState = Record<string, string>;

/** Columns a file can fill: everything except generated keys and computed columns. */
export function getImportableColumns(table: ModelTable): ModelColumn[] {
  return table.columns.filter(
    (column) => column.type !== "computed" && (!column.primaryKey || column.type === "reference"),
  );
}

/** A value is needed: not nullable, no default, not a generated key. */
export function isRequiredColumn(column: ModelColumn): boolean {
  return column.nullable === false && (column.default === undefined || column.default === null) && !column.primaryKey;
}

export function normalizeName(name: string): string {
  return name
    .normalize("NFD")
    .replace(/[̀-ͯ]/g, "")
    .toLowerCase()
    .replace(/[^a-z0-9]/g, "");
}

type Concept = {
  /** Normalized header fragments, mostly from German bank and broker exports. */
  headers: string[];
  matches: (column: ModelColumn) => boolean;
};

const nameHas = (column: ModelColumn, ...fragments: string[]) => {
  const name = normalizeName(`${column.name} ${column.displayName ?? ""}`);
  return fragments.some((fragment) => name.includes(fragment));
};

const referencesTo = (column: ModelColumn) =>
  typeof column.references === "string" ? normalizeName(column.references) : "";

const CONCEPTS: Concept[] = [
  {
    headers: ["buchungstag", "buchungsdatum", "datum", "date", "bookingdate", "transactiondate"],
    matches: (column) => column.type === "date" || column.type === "datetime",
  },
  {
    headers: ["betrag", "umsatz", "amount", "value"],
    matches: (column) => column.type === "currency" || column.type === "float",
  },
  {
    headers: ["verwendungszweck", "buchungstext", "beschreibung", "memo", "description", "purpose", "reference"],
    matches: (column) => column.type === "text" && nameHas(column, "purpose", "memo", "description", "note", "zweck", "text"),
  },
  {
    headers: ["auftraggeber", "begunstigter", "empfanger", "zahlungsempfanger", "zahlungspflichtiger", "payee", "counterparty", "name"],
    matches: (column) => column.type === "text" && nameHas(column, "counterparty", "payee", "recipient", "name", "party"),
  },
  {
    headers: ["kategorie", "category"],
    matches: (column) => column.type === "reference" ? referencesTo(column).includes("categor") : nameHas(column, "categor"),
  },
];

/**
 * Proposes a file header for each column: the same name first, then what a
 * header means (e.g. "Buchungstag" → the date column, "Betrag (EUR)" → the
 * amount). Each header is used once.
 */
export function suggestMapping(headers: string[], columns: ModelColumn[]): MappingState {
  const mapping: MappingState = Object.fromEntries(columns.map((column) => [column.name, ""]));
  const usedHeaders = new Set<string>();
  const assign = (column: ModelColumn, header: string) => {
    mapping[column.name] = header;
    usedHeaders.add(header);
  };

  for (const column of columns) {
    const names = [column.name, column.displayName ?? ""].map(normalizeName).filter(Boolean);
    const header = headers.find((h) => !usedHeaders.has(h) && names.includes(normalizeName(h)));
    if (header) assign(column, header);
  }

  for (const concept of CONCEPTS) {
    // The concept's own order decides ("Verwendungszweck" before
    // "Buchungstext"), not the order of the file's columns.
    let header: string | undefined;
    for (const fragment of concept.headers) {
      header = headers.find((h) => !usedHeaders.has(h) && normalizeName(h).startsWith(fragment));
      if (header) break;
    }
    if (!header) continue;
    const column = columns.find((c) => mapping[c.name] === "" && concept.matches(c));
    if (column) assign(column, header);
  }

  return mapping;
}

/** Mapped columns that identify a row: all mapped columns except references. */
export function suggestKeyColumns(mapping: MappingState, columns: ModelColumn[]): string[] {
  return columns
    .filter((column) => mapping[column.name] && column.type !== "reference")
    .map((column) => column.name);
}

/** A saved profile applied to this file: entries whose header or column no longer exists are dropped. */
export function applyProfile(
  config: ImportProfileConfig,
  headers: string[],
  columns: ModelColumn[],
): { mapping: MappingState; keyColumns: string[] } {
  const mapping: MappingState = Object.fromEntries(columns.map((column) => [column.name, ""]));
  for (const entry of config.mapping ?? []) {
    const header = headers.find((h) => h.toLowerCase() === entry.source.toLowerCase());
    const column = columns.find((c) => c.name.toLowerCase() === entry.target.toLowerCase());
    if (header && column) mapping[column.name] = header;
  }
  const keyColumns = (config.keyColumns ?? []).filter((name) => mapping[name]);
  return { mapping, keyColumns };
}

/** Keeps mapped headers that still exist after the file was read differently. */
export function retainMapping(mapping: MappingState, headers: string[]): MappingState {
  return Object.fromEntries(
    Object.entries(mapping).map(([column, header]) => [column, headers.includes(header) ? header : ""]),
  );
}

export function toMappingPayload(mapping: MappingState): ImportColumnMapping[] {
  return Object.entries(mapping)
    .filter(([, header]) => header)
    .map(([target, source]) => ({ source, target }));
}
