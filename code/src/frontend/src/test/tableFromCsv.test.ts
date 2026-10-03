import { describe, expect, it } from "vitest";
import type { ImportAnalysis, ImportAnalyzedColumn } from "../api/import";
import {
  buildTable,
  proposeTable,
  suggestTableName,
  toColumnName,
  toIdentifier,
  uniqueName,
  validatePlan,
  withStoreAs,
} from "../features/data-explorer/import/tableFromCsv";
import type { ModelTable, SchemaModelDocument } from "../types/api";

function column(index: number, name: string, inferredType: string, extra: Partial<ImportAnalyzedColumn> = {}): ImportAnalyzedColumn {
  return { index, name, samples: [], inferredType, blank: 0, distinctCount: 10, distinct: [], ...extra };
}

const analysis: ImportAnalysis = {
  settings: { encoding: "windows-1252", delimiter: ";", headerLine: 5, decimalSeparator: ",", dateFormat: "dd.MM.yyyy" },
  columns: [
    column(0, "Buchungstag", "date"),
    column(1, "Auftraggeber / Begünstigter", "text"),
    column(2, "Verwendungszweck", "text", { blank: 2 }),
    column(3, "Betrag (EUR)", "float"),
    column(4, "Kategorie", "text", { distinctCount: 5 }),
    column(5, "Column 6", "text", { blank: 10, distinctCount: 0 }),
  ],
  preamble: [],
  previewRows: [],
  rowCount: 10,
  options: { encodings: [], delimiters: [], decimalSeparators: [], dateFormats: [] },
};

const current: SchemaModelDocument = {
  version: 4,
  tables: [{ name: "customers", columns: [{ name: "id", type: "integer", primaryKey: true }] }],
  relations: [],
};

describe("table from CSV", () => {
  it("turns headers into clean identifiers", () => {
    expect(toIdentifier("Betrag (EUR)")).toBe("betrag_eur");
    expect(toIdentifier("Auftraggeber / Begünstigter")).toBe("auftraggeber_beguenstigter");
    expect(toIdentifier("Straße")).toBe("strasse");
    expect(toIdentifier("2. Name")).toBe("c_2_name");
    expect(toIdentifier("  ")).toBe("column");
    expect(toColumnName("ID")).toBe("id_value");
    expect(toColumnName("Order")).toBe("order_value");
  });

  it("names the table after the file without its date", () => {
    expect(suggestTableName("girokonto-2026-09.csv", [])).toBe("girokonto");
    expect(suggestTableName("Umsätze_092026.csv", [])).toBe("umsaetze");
    expect(suggestTableName("girokonto.csv", ["girokonto"])).toBe("girokonto_2");
    expect(uniqueName("name", ["Name", "name_2"])).toBe("name_3");
  });

  it("proposes types, required flags, a lookup table for categories and skips empty columns", () => {
    const plan = proposeTable(analysis, "girokonto-2026-09.csv", ["customers"]);

    expect(plan.name).toBe("girokonto");
    expect(plan.displayName).toBe("Girokonto");
    expect(plan.columns.map((c) => [c.name, c.type, c.required, c.include])).toEqual([
      ["buchungstag", "date", true, true],
      ["auftraggeber_beguenstigter", "text", true, true],
      ["verwendungszweck", "text", false, true],
      ["betrag_eur", "currency", true, true],
      ["kategorie_id", "reference", false, true],
      ["column_6", "text", false, false],
    ]);
    expect(plan.columns[4].storeAs).toEqual({ kind: "new-table", table: "kategorie" });
  });

  it("keeps the column name in step when switching between value and link", () => {
    const plan = proposeTable(analysis, "girokonto.csv", []);
    const category = plan.columns[4];

    const asText = withStoreAs(category, { kind: "value" }, "text");
    expect(asText).toMatchObject({ name: "kategorie", type: "text" });
    const linked = withStoreAs(asText, { kind: "existing-table", table: "categories" }, "text");
    expect(linked).toMatchObject({ name: "kategorie_id", type: "reference", required: false });
  });

  it("reports names that clash", () => {
    const plan = proposeTable(analysis, "girokonto.csv", []);
    const clashing = {
      ...plan,
      name: "customers",
      columns: plan.columns.map((c, i) => (i === 1 ? { ...c, name: "buchungstag" } : c)),
    };

    expect(validatePlan(clashing, ["customers"])).toEqual([
      'A table named "customers" already exists.',
      'The column name "buchungstag" is used twice.',
    ]);
    expect(validatePlan(plan, ["customers", "kategorie"])).toEqual([
      'A table named "kategorie" already exists; link to it instead.',
    ]);
  });

  it("builds the model, the mapping into it and the lookup tables to fill", () => {
    const plan = proposeTable(analysis, "girokonto.csv", ["customers"]);
    const built = buildTable(current, plan, []);

    const names = built.model.tables.map((t) => t.name);
    expect(names).toEqual(["customers", "kategorie", "girokonto"]);
    const girokonto = built.model.tables[2];
    expect(girokonto.columns[0]).toEqual({ name: "id", type: "integer", primaryKey: true });
    expect(girokonto.columns.find((c) => c.name === "betrag_eur")).toEqual({
      name: "betrag_eur",
      displayName: "Betrag (EUR)",
      type: "currency",
      nullable: false,
    });
    expect(girokonto.columns.find((c) => c.name === "kategorie_id")).toMatchObject({ type: "reference", references: "kategorie" });
    expect(built.model.tables[1].columns[1]).toMatchObject({ name: "name", unique: true, nullable: false });
    expect(built.model.relations).toEqual([
      { name: "kategorie_girokonto", type: "1:N", from: "kategorie", to: "girokonto", column: "kategorie_id" },
    ]);
    expect(built.mapping).toContainEqual({ source: "Kategorie", target: "kategorie_id" });
    expect(built.mapping).not.toContainEqual(expect.objectContaining({ source: "Column 6" }));
    expect(built.keyColumns).toEqual(["buchungstag", "auftraggeber_beguenstigter", "verwendungszweck", "betrag_eur"]);
    expect(built.lookups).toEqual([{ table: "kategorie", displayName: "Kategorie", labelColumn: "name", header: "Kategorie" }]);
  });

  it("links to an existing table through its record label column", () => {
    const categories: ModelTable = {
      name: "categories",
      displayName: "Categories",
      columns: [
        { name: "id", type: "integer", primaryKey: true },
        { name: "title", type: "text" },
      ],
    };
    const plan = proposeTable(analysis, "girokonto.csv", ["categories"]);
    plan.columns[4] = withStoreAs(plan.columns[4], { kind: "existing-table", table: "categories" }, "text");

    const built = buildTable(current, plan, [categories]);

    expect(built.model.tables.map((t) => t.name)).toEqual(["customers", "girokonto"]);
    expect(built.lookups).toEqual([{ table: "categories", displayName: "Categories", labelColumn: "title", header: "Kategorie" }]);
  });
});
