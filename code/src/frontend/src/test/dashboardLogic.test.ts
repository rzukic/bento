import { describe, expect, it } from "vitest";
import type { QueryResult } from "../api/query";
import { rangeFromSearch, rangeToSearch, resolveRange } from "../features/dashboards/dateRange";
import { addItem, collides, compact, moveItem, removeItem, resizeItem, type LayoutItem } from "../features/dashboards/layout";
import { cashFlowColumns, describeCashFlow } from "../features/dashboards/starter";
import type { ModelTable } from "../types/api";
import {
  bucketSequence,
  bucketStart,
  effectiveDateColumn,
  rangeFilter,
  toChartModel,
  toKpiModel,
  withFilter,
} from "../features/dashboards/widgetData";

const today = new Date(2026, 9, 3); // 3 October 2026

describe("date ranges", () => {
  it("resolves presets to whole months, end exclusive", () => {
    expect(resolveRange({ preset: "this-month" }, today)).toEqual({ from: "2026-10-01", to: "2026-11-01" });
    expect(resolveRange({ preset: "last-12-months" }, today)).toEqual({ from: "2025-11-01", to: "2026-11-01" });
    expect(resolveRange({ preset: "last-3-months" }, today)).toEqual({ from: "2026-08-01", to: "2026-11-01" });
    expect(resolveRange({ preset: "this-year" }, today)).toEqual({ from: "2026-01-01", to: "2027-01-01" });
    expect(resolveRange({ preset: "last-year" }, today)).toEqual({ from: "2025-01-01", to: "2026-01-01" });
    expect(resolveRange({ preset: "all" }, today)).toEqual({ from: null, to: null });
  });

  it("treats a custom end date as the last day shown", () => {
    expect(resolveRange({ from: "2026-01-01", to: "2026-06-30" }, today)).toEqual({ from: "2026-01-01", to: "2026-07-01" });
  });

  it("round-trips ranges through the URL", () => {
    const params = rangeToSearch({ preset: "last-6-months" }, new URLSearchParams("tab=x&from=1&to=2"));
    expect(params.toString()).toBe("tab=x&range=last-6-months");
    expect(rangeFromSearch(params)).toEqual({ preset: "last-6-months" });
    expect(rangeFromSearch(new URLSearchParams("from=2026-01-01&to=2026-03-31"))).toEqual({ from: "2026-01-01", to: "2026-03-31" });
    expect(rangeFromSearch(new URLSearchParams("range=forever"))).toBeNull();
  });
});

describe("grid layout", () => {
  const items: LayoutItem[] = [
    { id: "a", x: 0, y: 0, w: 6, h: 2 },
    { id: "b", x: 6, y: 0, w: 6, h: 2 },
    { id: "c", x: 0, y: 2, w: 12, h: 3 },
  ];

  it("pushes what an item lands on downwards and never overlaps", () => {
    const moved = moveItem(items, "c", 0, 0);
    expect(moved.find((i) => i.id === "c")).toMatchObject({ x: 0, y: 0 });
    expect(moved.find((i) => i.id === "a")).toMatchObject({ y: 3 });
    expect(moved.find((i) => i.id === "b")).toMatchObject({ y: 3 });
    for (const a of moved) for (const b of moved) expect(collides(a, b)).toBe(false);
  });

  it("floats items up into free space and keeps them inside 12 columns", () => {
    expect(compact([{ id: "a", x: 0, y: 5, w: 4, h: 2 }])).toEqual([{ id: "a", x: 0, y: 0, w: 4, h: 2 }]);
    const resized = resizeItem(items, "a", 20, 2);
    expect(resized.find((i) => i.id === "a")).toMatchObject({ x: 0, w: 12 });
    expect(resized.find((i) => i.id === "b")).toMatchObject({ y: 2 });
    expect(moveItem(items, "a", 9, 0).find((i) => i.id === "a")!.x).toBe(6);
  });

  it("adds into the first free spot and closes gaps on removal", () => {
    const added = addItem(items.filter((i) => i.id !== "b"), "d", 6, 2);
    expect(added.find((i) => i.id === "d")).toMatchObject({ x: 6, y: 0 });
    expect(removeItem(items, "a").find((i) => i.id === "c")).toMatchObject({ y: 2 });
  });
});

describe("widget data", () => {
  it("adds the date range to a widget's query", () => {
    const filter = rangeFilter("booked_on", { from: "2026-01-01", to: "2026-07-01" });
    expect(filter).toEqual({ booked_on: { gte: "2026-01-01", lt: "2026-07-01" } });
    expect(rangeFilter(null, { from: "2026-01-01", to: null })).toBeUndefined();
    expect(withFilter({ source: "t", filter: { booked_on: { lt: "2026-03-01" }, amount: { lt: 0 } } }, filter)).toEqual({
      source: "t",
      filter: { booked_on: { lt: "2026-07-01", gte: "2026-01-01" }, amount: { lt: 0 } },
    });
  });

  it("uses the widget's date group unless a date column is set", () => {
    const widget = {
      id: "w",
      type: "line" as const,
      title: "",
      position: { x: 0, y: 0, w: 4, h: 3 },
      data: { query: { source: "t", groupBy: [{ column: "booked_on", bucket: "month" as const }] } },
    };
    expect(effectiveDateColumn(widget)).toBe("booked_on");
    expect(effectiveDateColumn({ ...widget, data: { ...widget.data, dateColumn: null } })).toBeNull();
  });

  it("lists every bucket of a range", () => {
    expect(bucketStart("2026-09-10", "week")).toBe("2026-09-07");
    expect(bucketStart("2026-11-20", "quarter")).toBe("2026-10-01");
    expect(bucketSequence("2026-08-15", "2026-11-02", "month")).toEqual(["2026-08-01", "2026-09-01", "2026-10-01", "2026-11-01"]);
    expect(bucketSequence("2026-09-07", "2026-09-21", "week")).toEqual(["2026-09-07", "2026-09-14", "2026-09-21"]);
  });

  const monthly: QueryResult = {
    data: [
      { month: "2026-08-01", net: 1949.9, balance: 1949.9 },
      { month: "2026-10-01", net: 3069.95, balance: 5019.85 },
    ],
    meta: {
      truncated: false,
      columns: [
        { name: "month", type: "date", role: "group" },
        { name: "net", type: "currency", role: "measure" },
        { name: "balance", type: "currency", role: "measure" },
      ],
    },
  };
  const monthlyQuery = {
    source: "t",
    groupBy: [{ column: "booked_on", bucket: "month" as const, as: "month" }],
    measures: [
      { name: "net", fn: "sum" as const, column: "amount" },
      { name: "balance", fn: "sum" as const, column: "amount", running: true },
    ],
  };

  it("fills empty buckets: 0 for sums, the last value for running totals, nothing past today", () => {
    const model = toChartModel(monthly, monthlyQuery, { from: "2026-07-01", to: "2027-01-01" }, undefined, today);

    expect(model.categories).toEqual(["2026-07-01", "2026-08-01", "2026-09-01", "2026-10-01"]);
    expect(model.format).toBe("currency");
    expect(model.series).toEqual([
      { name: "net", values: [0, 1949.9, 0, 3069.95] },
      { name: "balance", values: [null, 1949.9, 1949.9, 5019.85] },
    ]);
  });

  it("splits a second group into series named by their labels", () => {
    const result: QueryResult = {
      data: [
        { month: "2026-09-01", category_id: 3, category_id_label: "Food", spent: -49.1 },
        { month: "2026-09-01", category_id: 2, category_id_label: "Rent", spent: -1000 },
        { month: "2026-10-01", category_id: 3, category_id_label: "Food", spent: -20.05 },
      ],
      meta: {
        truncated: false,
        columns: [
          { name: "month", type: "date", role: "group" },
          { name: "category_id", type: "reference", role: "group" },
          { name: "category_id_label", type: "text", role: "label", labelOf: "category_id" },
          { name: "spent", type: "currency", role: "measure" },
        ],
      },
    };
    const model = toChartModel(
      result,
      { source: "t", groupBy: [{ column: "booked_on", bucket: "month", as: "month" }, { column: "category_id" }], measures: [{ name: "spent", fn: "sum", column: "amount" }] },
      { from: null, to: null },
      undefined,
      today,
    );
    expect(model.categories).toEqual(["2026-09-01", "2026-10-01"]);
    expect(model.series).toEqual([
      { name: "Food", values: [-49.1, -20.05] },
      { name: "Rent", values: [-1000, 0] },
    ]);
  });

  it("shows the latest value of a KPI with the bucket before it", () => {
    expect(toKpiModel(monthly)).toEqual({ value: 3069.95, previous: 1949.9, format: "currency", label: "2026-10-01" });
    expect(toKpiModel({ data: [{ n: 4 }], meta: { truncated: false, columns: [{ name: "n", type: "integer", role: "measure" }] } })).toEqual({
      value: 4,
      previous: null,
      format: "integer",
      label: undefined,
    });
  });
});

describe("cash-flow starter", () => {
  const orders = {
    name: "orders",
    columns: [
      { name: "id", type: "integer", primaryKey: true },
      { name: "placed_at", type: "datetime" },
      { name: "quantity", type: "integer" },
      { name: "status", type: "enum", values: ["new"] },
    ],
  } as unknown as ModelTable;
  const bank = {
    name: "bank",
    columns: [
      { name: "id", type: "integer", primaryKey: true },
      { name: "booked", displayName: "Booked", type: "date" },
      { name: "amount", displayName: "Amount", type: "currency" },
      { name: "note", type: "text" },
    ],
  } as unknown as ModelTable;

  it("needs a currency column; plain numbers are not amounts", () => {
    expect(cashFlowColumns(orders)).toBeNull();
    expect(cashFlowColumns(bank)).toMatchObject({ date: { name: "booked" }, amount: { name: "amount" }, payee: { name: "note" } });
  });

  it("describes only what the table can show", () => {
    expect(describeCashFlow(bank)).toBe("Balance and net per month from Amount by Booked and top note.");
    expect(describeCashFlow({ ...bank, columns: bank.columns.filter((c) => c.name !== "note") } as ModelTable)).toBe(
      "Balance and net per month from Amount by Booked.",
    );
  });
});
