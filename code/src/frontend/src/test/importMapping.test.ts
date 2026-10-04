import { describe, expect, it } from "vitest";
import {
  applyProfile,
  getImportableColumns,
  isRequiredColumn,
  retainMapping,
  suggestKeyColumns,
  suggestMapping,
  toMappingPayload,
} from "../features/data-explorer/import/importMapping";
import type { ModelTable } from "../types/api";

const transactions: ModelTable = {
  name: "transactions",
  columns: [
    { name: "id", type: "integer", primaryKey: true },
    { name: "booked_on", displayName: "Booked on", type: "date", nullable: false },
    { name: "counterparty", type: "text" },
    { name: "purpose", type: "text" },
    { name: "amount", type: "currency", nullable: false },
    { name: "category_id", type: "reference", references: "categories" },
    { name: "balance", type: "computed", formula: "= 1" },
  ],
};

const germanHeaders = [
  "Buchungstag",
  "Wertstellung",
  "Buchungstext",
  "Auftraggeber / Begünstigter",
  "Verwendungszweck",
  "Betrag (EUR)",
  "Kategorie",
  "Column 8",
];

describe("import mapping", () => {
  it("leaves out generated keys and computed columns", () => {
    expect(getImportableColumns(transactions).map((c) => c.name)).toEqual([
      "booked_on",
      "counterparty",
      "purpose",
      "amount",
      "category_id",
    ]);
  });

  it("marks columns without a value source as required", () => {
    const [bookedOn, counterparty] = getImportableColumns(transactions);
    expect(isRequiredColumn(bookedOn)).toBe(true);
    expect(isRequiredColumn(counterparty)).toBe(false);
  });

  it("maps a German bank export by meaning, preferring Verwendungszweck over Buchungstext", () => {
    const mapping = suggestMapping(germanHeaders, getImportableColumns(transactions));

    expect(mapping).toEqual({
      booked_on: "Buchungstag",
      counterparty: "Auftraggeber / Begünstigter",
      purpose: "Verwendungszweck",
      amount: "Betrag (EUR)",
      category_id: "Kategorie",
    });
  });

  it("prefers headers with the column's own name or display name", () => {
    const mapping = suggestMapping(["Booked on", "Datum", "Amount"], getImportableColumns(transactions));

    expect(mapping.booked_on).toBe("Booked on");
    expect(mapping.amount).toBe("Amount");
  });

  it("suggests every mapped column except references as the key", () => {
    const columns = getImportableColumns(transactions);
    const mapping = suggestMapping(germanHeaders, columns);

    expect(suggestKeyColumns(mapping, columns)).toEqual(["booked_on", "counterparty", "purpose", "amount"]);
  });

  it("applies a profile and drops entries whose header or column is gone", () => {
    const applied = applyProfile(
      {
        mapping: [
          { source: "buchungstag", target: "booked_on" },
          { source: "Betrag (EUR)", target: "amount" },
          { source: "Old header", target: "purpose" },
          { source: "Kategorie", target: "removed_column" },
        ],
        keyColumns: ["booked_on", "purpose"],
      },
      germanHeaders,
      getImportableColumns(transactions),
    );

    expect(applied.mapping.booked_on).toBe("Buchungstag");
    expect(applied.mapping.amount).toBe("Betrag (EUR)");
    expect(applied.mapping.purpose).toBe("");
    expect(applied.keyColumns).toEqual(["booked_on"]);
  });

  it("keeps choices whose header still exists after re-reading the file", () => {
    expect(retainMapping({ booked_on: "Buchungstag", amount: "Betrag" }, ["Buchungstag"])).toEqual({
      booked_on: "Buchungstag",
      amount: "",
    });
  });

  it("sends only mapped columns", () => {
    expect(toMappingPayload({ booked_on: "Buchungstag", purpose: "" })).toEqual([
      { source: "Buchungstag", target: "booked_on" },
    ]);
  });

  it("keeps a fixed value for every row through payloads, profiles and re-reads", async () => {
    const { applyProfile, asFixed, retainMapping, toMappingPayload } = await import("../features/data-explorer/import/importMapping");
    const mapping = { booked_on: "Buchungstag", account_id: asFixed("Checking"), purpose: "" };
    expect(toMappingPayload(mapping)).toEqual([
      { source: "Buchungstag", target: "booked_on" },
      { target: "account_id", value: "Checking" },
    ]);
    expect(retainMapping(mapping, ["Datum"])).toEqual({ booked_on: "", account_id: asFixed("Checking"), purpose: "" });
    const columns = [
      { name: "booked_on", type: "date" },
      { name: "account_id", type: "reference", references: "accounts" },
    ] as never;
    expect(applyProfile({ mapping: [{ source: "Buchungstag", target: "booked_on" }, { target: "account_id", value: "Checking" }], keyColumns: [] }, ["Buchungstag"], columns).mapping).toEqual({
      booked_on: "Buchungstag",
      account_id: asFixed("Checking"),
    });
  });
});
