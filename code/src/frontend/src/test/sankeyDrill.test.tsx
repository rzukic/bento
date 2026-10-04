import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { DashboardWidget } from "../api/dashboards";
import type { QueryResult } from "../api/query";
import type { ChartClick } from "../features/dashboards/charts/EChart";
import { buildSankeyOption, type ChartTokens } from "../features/dashboards/charts/chartOptions";
import { drillTarget, drillUrl, signFilter } from "../features/dashboards/drill";
import { addField, describeWidget, fieldsOf, normalize, slotsFor, suggestions, switchType } from "../features/dashboards/lens/model";
import { sankeyMode, toSankeyModel } from "../features/dashboards/sankey";
import { WidgetContent } from "../features/dashboards/widgets/WidgetContent";
import type { ModelTable } from "../types/api";

const queryApi = vi.hoisted(() => ({ runQuery: vi.fn() }));
vi.mock("../api/query", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/query")>()), ...queryApi }));

// The chart stand-in hands its click handler to the test.
const charts = vi.hoisted(() => ({ click: undefined as undefined | ((click: ChartClick) => void) }));
vi.mock("../features/dashboards/charts/EChart", () => ({
  EChart: ({ label, onItemClick }: { label: string; onItemClick?: (click: ChartClick) => void }) => {
    charts.click = onItemClick;
    return <div role="img" aria-label={label} />;
  },
}));

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
  charts.click = undefined;
});

const tokens: ChartTokens = {
  text: "#eee",
  muted: "#999",
  line: "#333",
  surface: "#111",
  popover: "#222",
  palette: ["#3b82f6", "#14b8a6", "#f59e0b"],
  positive: "#4ade80",
  negative: "#f87171",
};

const byCategory: QueryResult = {
  data: [
    { kategorie_id: 1, kategorie_id_label: "Gehalt", kategorie_id_other: false, amount: 3250 },
    { kategorie_id: 2, kategorie_id_label: "Miete", kategorie_id_other: false, amount: -1150 },
    { kategorie_id: 3, kategorie_id_label: "Lebensmittel", kategorie_id_other: false, amount: -171.85 },
    { kategorie_id: null, kategorie_id_label: null, kategorie_id_other: true, amount: -120.2 },
  ],
  meta: {
    truncated: false,
    columns: [
      { name: "kategorie_id", type: "reference", role: "group" },
      { name: "kategorie_id_label", type: "text", role: "label", labelOf: "kategorie_id" },
      { name: "kategorie_id_other", type: "boolean", role: "other", labelOf: "kategorie_id" },
      { name: "amount", type: "currency", role: "measure" },
    ],
  },
};
const cashQuery = { source: "girokonto", groupBy: [{ column: "kategorie_id", top: 3, other: true }], measures: [{ name: "amount", fn: "sum" as const, column: "betrag_eur" }] };

describe("Sankey model", () => {
  it("splits one category by sign: income → the centre → expenses, the rest saved", () => {
    const model = toSankeyModel(byCategory, cashQuery, "cashflow", "currency");
    expect(model.links.map((l) => [l.source, l.target, Math.round(l.value * 100) / 100])).toEqual([
      ["in:Gehalt", "center", 3250],
      ["center", "out:Miete", 1150],
      ["center", "out:Lebensmittel", 171.85],
      ["center", "out:Other", 120.2],
      ["center", "saved", 1807.95],
    ]);
    expect(model.nodes.find((n) => n.id === "center")?.label).toBe("Income");
    // "Other" and "Saved" have no rows to list.
    expect(model.nodes.find((n) => n.id === "out:Other")?.picks).toBeNull();
    expect(model.nodes.find((n) => n.id === "saved")?.picks).toBeNull();
    expect(model.nodes.find((n) => n.id === "out:Miete")).toMatchObject({ sign: "negative", picks: [{ column: "kategorie_id", value: 2, other: false }] });
  });

  it("draws from savings when expenses are larger, and details expenses by a second level", () => {
    const result: QueryResult = {
      data: [
        { cat: "Gehalt", sub: "Firma", amount: 1000 },
        { cat: "Wohnen", sub: "Miete", amount: -900 },
        { cat: "Wohnen", sub: "Strom", amount: -300 },
      ],
      meta: {
        truncated: false,
        columns: [
          { name: "cat", type: "text", role: "group" },
          { name: "sub", type: "text", role: "group" },
          { name: "amount", type: "currency", role: "measure" },
        ],
      },
    };
    const model = toSankeyModel(result, undefined, "cashflow", "currency");
    expect(model.links.map((l) => [l.source, l.target, l.value])).toEqual([
      ["in:Gehalt", "center", 1000],
      ["deficit", "center", 200],
      ["center", "out:Wohnen", 1200],
      ["out:Wohnen", "sub:Wohnen\u0000Miete", 900],
      ["out:Wohnen", "sub:Wohnen\u0000Strom", 300],
    ]);
    expect(model.links[3].picks).toEqual([
      { column: "cat", value: "Wohnen", other: false },
      { column: "sub", value: "Miete", other: false },
    ]);

    // Between levels: a flow per pair of consecutive levels, as amounts; a value on two levels stays two nodes.
    const levels = toSankeyModel(result, undefined, "levels", "currency");
    expect(levels.links.map((l) => [l.source, l.target, l.value])).toEqual([
      ["0:Gehalt", "1:Firma", 1000],
      ["0:Wohnen", "1:Miete", 900],
      ["0:Wohnen", "1:Strom", 300],
    ]);
    expect(sankeyMode(undefined, 2)).toBe("levels");
    expect(sankeyMode(undefined, 1)).toBe("cashflow");
  });

  it("puts the last column's labels inside and names flows in the tooltip", () => {
    const model = toSankeyModel(byCategory, cashQuery, "cashflow", "currency");
    const option = buildSankeyOption(model, tokens) as {
      series: { levels: { depth: number; label: { position: string } }[]; data: { name: string; itemStyle: { color: string } }[] }[];
      tooltip: { formatter: (p: unknown) => string };
    };
    expect(option.series[0].levels).toEqual([{ depth: 2, label: { position: "left" } }]);
    expect(option.series[0].data.find((d) => d.name === "saved")?.itemStyle.color).toBe(tokens.positive);
    expect(option.tooltip.formatter({ dataType: "edge", value: 1150, data: { source: "center", target: "out:Miete" } })).toContain("Income → Miete");
  });
});

describe("drill-down", () => {
  const range = { from: "2026-07-01", to: "2026-10-01" };

  it("lists the rows behind a period and a value: the widget's conditions, inside the range", () => {
    const query = {
      source: "girokonto",
      filter: { betrag_eur: { lt: 0 } },
      groupBy: [{ column: "buchungstag", bucket: "month" as const, as: "month" }, { column: "kategorie_id" }],
      measures: [{ name: "spent", fn: "sum" as const, column: "betrag_eur" }],
    };
    const target = drillTarget(query, { query }, range, [
      { column: "buchungstag", bucket: "month", value: "2026-09-01" },
      { column: "kategorie_id", value: 2 },
    ]);
    expect(target).toEqual({
      table: "girokonto",
      filters: [
        { column: "betrag_eur", op: "lt", value: "0" },
        { column: "buchungstag", op: "gte", value: "2026-09-01" },
        { column: "buchungstag", op: "lt", value: "2026-10-01" },
        { column: "kategorie_id", op: "eq", value: "2" },
      ],
    });
    expect(drillUrl(target!)).toContain("/data/girokonto?");
    expect(decodeURIComponent(drillUrl(target!))).toContain("filter[buchungstag][gte]=2026-09-01");

    // A range ending mid-month keeps the period inside it; empty values; "Other" cannot be listed.
    const mid = drillTarget(query, { query }, { from: "2026-07-01", to: "2026-09-15" }, [{ column: "buchungstag", bucket: "month", value: "2026-09-01" }]);
    expect(mid?.filters).toContainEqual({ column: "buchungstag", op: "lt", value: "2026-09-15" });
    expect(drillTarget(query, { query }, range, [{ column: "kategorie_id", value: null }])?.filters).toContainEqual({ column: "kategorie_id", op: "null" });
    expect(drillTarget(query, { query }, range, [{ column: "kategorie_id", value: null, other: true }])).toBeNull();
  });

  it("counts every row up to the period's end for values at the end of each period", () => {
    const query = { source: "konto", groupBy: [{ column: "tag", bucket: "month" as const }], measures: [{ name: "b", formula: "SUM(x WHERE tag <= BUCKET_END())" }] };
    expect(drillTarget(query, { query, asOf: true }, range, [{ column: "tag", bucket: "month", value: "2026-08-01" }])?.filters).toEqual([
      { column: "tag", op: "lt", value: "2026-09-01" },
    ]);
  });

  it("lists every row up to the period's end for running totals, from the start with carry-in", () => {
    const groupBy = [{ column: "tag", bucket: "month" as const }];
    const running = { source: "konto", groupBy, measures: [{ name: "b", fn: "sum" as const, column: "x", running: true }] };
    const pick = [{ column: "tag", bucket: "month" as const, value: "2026-08-01" }];
    expect(drillTarget(running, { query: running }, range, pick)?.filters).toEqual([
      { column: "tag", op: "gte", value: "2026-07-01" },
      { column: "tag", op: "lt", value: "2026-09-01" },
    ]);
    const carried = { ...running, measures: [{ ...running.measures[0], carryIn: true }] };
    expect(drillTarget(carried, { query: carried }, range, pick)?.filters).toEqual([{ column: "tag", op: "lt", value: "2026-09-01" }]);
  });

  it("keeps a cash flow side's sign when the value is a plain sum", () => {
    expect(signFilter(cashQuery, "negative")).toEqual([{ column: "betrag_eur", op: "lt", value: "0" }]);
    expect(signFilter({ ...cashQuery, measures: [{ name: "x", formula: "SUM(betrag_eur)" }] }, "negative")).toEqual([]);
  });

  const widget = (type: DashboardWidget["type"], query: NonNullable<DashboardWidget["data"]>["query"]): DashboardWidget => ({
    id: "w",
    type,
    title: "W",
    position: { x: 0, y: 0, w: 6, h: 4 },
    data: { query, dateColumn: "buchungstag" },
  });

  it("opens the rows of a clicked bar, slice or Sankey flow", async () => {
    const onDrill = vi.fn();
    const monthly: QueryResult = {
      data: [{ month: "2026-09-01", spent: -100 }],
      meta: { truncated: false, columns: [{ name: "month", type: "date", role: "group" }, { name: "spent", type: "currency", role: "measure" }] },
    };
    queryApi.runQuery.mockResolvedValue(monthly);
    const bar = widget("bar", { source: "girokonto", groupBy: [{ column: "buchungstag", bucket: "month", as: "month" }], measures: [{ name: "spent", fn: "sum", column: "betrag_eur" }] });
    render(<WidgetContent widget={bar} range={range} refreshToken={0} tokens={tokens} onDrill={onDrill} />);
    await waitFor(() => expect(charts.click).toBeDefined());
    // July, August, September: the third bar.
    charts.click!({ componentType: "series", seriesIndex: 0, dataIndex: 2 });
    expect(onDrill).toHaveBeenLastCalledWith({
      table: "girokonto",
      filters: [
        { column: "buchungstag", op: "gte", value: "2026-09-01" },
        { column: "buchungstag", op: "lt", value: "2026-10-01" },
      ],
    });
    cleanup();

    queryApi.runQuery.mockResolvedValue(byCategory);
    render(<WidgetContent widget={{ ...widget("sankey", cashQuery), display: { sankey: "cashflow" } }} range={range} refreshToken={0} tokens={tokens} onDrill={onDrill} />);
    await waitFor(() => expect(charts.click).toBeDefined());
    charts.click!({ componentType: "series", dataType: "edge", data: { source: "center", target: "out:Miete" } });
    expect(onDrill).toHaveBeenLastCalledWith({
      table: "girokonto",
      filters: [
        { column: "buchungstag", op: "gte", value: "2026-07-01" },
        { column: "buchungstag", op: "lt", value: "2026-10-01" },
        { column: "kategorie_id", op: "eq", value: "2" },
        { column: "betrag_eur", op: "lt", value: "0" },
      ],
    });
    const calls = onDrill.mock.calls.length;
    charts.click!({ componentType: "series", dataType: "node", name: "out:Other" });
    expect(onDrill).toHaveBeenCalledTimes(calls);
  });

  it("opens the rows behind a key figure, and nothing while editing", async () => {
    const onDrill = vi.fn();
    queryApi.runQuery.mockResolvedValue({ data: [{ spent: -500 }], meta: { truncated: false, columns: [{ name: "spent", type: "currency", role: "measure" }] } });
    const kpi = { ...widget("kpi", { source: "girokonto", filter: { betrag_eur: { lt: 0 } }, measures: [{ name: "spent", fn: "sum", column: "betrag_eur" }] }), display: { compare: false } };
    const { rerender } = render(<WidgetContent widget={kpi} range={range} refreshToken={0} tokens={tokens} onDrill={onDrill} />);
    fireEvent.click(await screen.findByTitle("Show the rows behind this number"));
    expect(onDrill).toHaveBeenCalledWith({
      table: "girokonto",
      filters: [
        { column: "betrag_eur", op: "lt", value: "0" },
        { column: "buchungstag", op: "gte", value: "2026-07-01" },
        { column: "buchungstag", op: "lt", value: "2026-10-01" },
      ],
    });
    rerender(<WidgetContent widget={kpi} range={range} refreshToken={0} tokens={tokens} />);
    expect(screen.queryByTitle("Show the rows behind this number")).not.toBeInTheDocument();
  });
});

describe("Sankey in the editor", () => {
  const table: ModelTable = {
    name: "girokonto",
    displayName: "Girokonto",
    columns: [
      { name: "id", type: "integer", primaryKey: true },
      { name: "buchungstag", displayName: "Buchungstag", type: "date" },
      { name: "betrag_eur", displayName: "Betrag (EUR)", type: "currency" },
      { name: "kategorie_id", displayName: "Kategorie", type: "reference", references: "kategorie" },
      { name: "auftraggeber", displayName: "Auftraggeber", type: "text" },
    ],
  };
  const field = (name: string) => fieldsOf(table).find((f) => f.name === name)!;
  const empty: DashboardWidget = { id: "w", type: "sankey", title: "", position: { x: 0, y: 0, w: 8, h: 5 }, data: { query: { source: "girokonto", measures: [] } } };

  it("takes categories as levels and a number as the size; dates do not flow", () => {
    expect(slotsFor(empty).map((s) => s.id)).toEqual(["levels", "size"]);
    let sankey = addField(empty, field("kategorie_id"), table);
    sankey = addField(sankey, field("auftraggeber"), table);
    sankey = addField(sankey, field("betrag_eur"), table);
    expect(sankey.data?.query).toMatchObject({
      groupBy: [
        { column: "kategorie_id", top: 8, other: true, rank: "abs" },
        { column: "auftraggeber", top: 8, other: true, rank: "abs" },
      ],
      measures: [{ fn: "sum", column: "betrag_eur" }],
    });
    expect(describeWidget(sankey, table)).toBe("Sum of Betrag (EUR) flowing Top 8 Kategorie + other → Top 8 Auftraggeber + other");
    expect(describeWidget({ ...sankey, display: { sankey: "cashflow" } }, table)).toBe("Sum of Betrag (EUR): income → expenses by Top 8 Kategorie + other → Top 8 Auftraggeber + other");
    expect(addField(empty, field("buchungstag"), table)).toEqual(empty);
    const dated = normalize({ ...sankey, data: { query: { ...sankey.data!.query!, groupBy: [{ column: "buchungstag", bucket: "month" }, ...sankey.data!.query!.groupBy!] } } });
    expect(dated.data?.query?.groupBy?.map((g) => g.column)).toEqual(["kategorie_id", "auftraggeber"]);
  });

  it("is suggested for categories and keeps the fields when switching", () => {
    const bar: DashboardWidget = {
      ...empty,
      type: "bar",
      data: { query: { source: "girokonto", groupBy: [{ column: "kategorie_id" }], measures: [{ name: "s", fn: "sum", column: "betrag_eur" }] } },
    };
    expect(suggestions(bar, table).map((w) => w.type)).toContain("sankey");
    expect(switchType(bar, "sankey", table).data?.query?.groupBy).toEqual([{ column: "kategorie_id", top: 8, other: true, rank: "abs" }]);
  });
});
