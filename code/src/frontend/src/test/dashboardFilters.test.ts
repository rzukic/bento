import { describe, expect, it } from "vitest";
import type { ModelTable } from "../types/api";
import {
  appliedFilters,
  clearSelection,
  filterableColumns,
  globalFilterClauses,
  globalFilterFor,
  mergeFilters,
  selectionFromSearch,
  selectionToSearch,
} from "../features/dashboards/dashboardFilters";
import { drillTarget } from "../features/dashboards/drill";
import { runnableQuery, variableRequests } from "../features/dashboards/formulaQueries";
import { shiftedRange } from "../features/dashboards/chartTransforms";
import { previousRange } from "../features/dashboards/useExtraQueries";

const tables = [
  {
    name: "girokonto",
    columns: [
      { name: "id", type: "integer", primaryKey: true },
      { name: "buchungstag", type: "date" },
      { name: "betrag", type: "currency" },
      { name: "konto_id", type: "reference", references: "konten", displayName: "Konto" },
      { name: "status", type: "enum" },
    ],
  },
  {
    name: "trades",
    columns: [
      { name: "id", type: "integer", primaryKey: true },
      { name: "konto_id", type: "reference", references: "konten" },
      { name: "kurs", type: "currency" },
    ],
  },
  { name: "konten", columns: [{ name: "id", type: "integer", primaryKey: true }, { name: "name", type: "text" }] },
] as unknown as ModelTable[];

describe("dashboard filters", () => {
  it("offers list-like columns once per name, with the tables that have them", () => {
    expect(filterableColumns(tables)).toEqual([
      { column: "konto_id", label: "Konto", tables: ["girokonto", "trades"] },
      { column: "name", label: "name", tables: ["konten"] },
      { column: "status", label: "status", tables: ["girokonto"] },
    ]);
  });

  it("keeps the chosen values in the address", () => {
    let params = new URLSearchParams("range=all");
    params = selectionToSearch("konto_id", ["1", "3"], params);
    expect(params.toString()).toBe("range=all&filter.konto_id=1&filter.konto_id=3");
    expect(selectionFromSearch(params)).toEqual({ konto_id: ["1", "3"] });
    expect(clearSelection(params).toString()).toBe("range=all");
  });

  it("narrows only the tables that have the column, ids as numbers", () => {
    const applied = appliedFilters([{ column: "konto_id" }, { column: "status" }], { konto_id: ["1"], status: ["paid", "open"], other: ["x"] }, tables);
    expect(applied).toEqual([
      { column: "konto_id", values: [1], tables: ["girokonto", "trades"] },
      { column: "status", values: ["paid", "open"], tables: ["girokonto"] },
    ]);
    const range = { from: "2026-01-01", to: "2027-01-01", filters: applied };
    expect(globalFilterFor("girokonto", range)).toEqual({ konto_id: { eq: 1 }, status: { in: ["paid", "open"] } });
    expect(globalFilterFor("trades", range)).toEqual({ konto_id: { eq: 1 } });
    expect(globalFilterFor("konten", range)).toBeUndefined();
    expect(globalFilterClauses("girokonto", range)).toEqual([
      { column: "konto_id", op: "eq", value: "1" },
      { column: "status", op: "in", value: "paid,open" },
    ]);
  });

  const range = { from: "2026-01-01", to: "2027-01-01", filters: [{ column: "konto_id", values: [1], tables: ["girokonto"] }] };

  it("reaches widget queries, variables, drill-downs and earlier ranges", () => {
    const query = { source: "girokonto", filter: { betrag: { lt: 0 } }, measures: [{ name: "s", fn: "sum" as const, column: "betrag" }] };
    expect(runnableQuery(query, { query, dateColumn: "buchungstag" }, range, {}).filter).toEqual({
      betrag: { lt: 0 },
      buchungstag: { gte: "2026-01-01", lt: "2027-01-01" },
      konto_id: { eq: 1 },
    });
    expect(variableRequests([{ name: "spent", source: "girokonto", formula: "SUM(betrag)" }], range)[0]).toMatchObject({ filter: { konto_id: { eq: 1 } } });
    expect(variableRequests([{ name: "x", source: "konten", formula: "COUNT()" }], range)[0]).not.toHaveProperty("filter");
    expect(drillTarget(query, { query, dateColumn: "buchungstag" }, range, [])?.filters).toContainEqual({ column: "konto_id", op: "eq", value: "1" });
    expect(previousRange(range)?.filters).toEqual(range.filters);
    expect(shiftedRange(range, "year")?.filters).toEqual(range.filters);
  });

  it("combines conditions on the same column; a plain value means equals", () => {
    expect(mergeFilters({ status: "paid", betrag: { lt: 0 } }, { status: { in: ["paid", "open"] }, betrag: { gte: -100 } })).toEqual({
      status: { eq: "paid", in: ["paid", "open"] },
      betrag: { lt: 0, gte: -100 },
    });
  });
});
