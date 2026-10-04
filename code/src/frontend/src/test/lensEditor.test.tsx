import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { DashboardWidget } from "../api/dashboards";
import type { QueryResult } from "../api/query";
import { addField, describeWidget, fieldsOf, normalize, setTableMode, suggestions, switchType } from "../features/dashboards/lens/model";
import { toChartModel } from "../features/dashboards/widgetData";
import type { ModelTable } from "../types/api";

const queryApi = vi.hoisted(() => ({ runQuery: vi.fn() }));
const dataApi = vi.hoisted(() => ({ fetchTableRows: vi.fn() }));
vi.mock("../api/query", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/query")>()), ...queryApi }));
vi.mock("../api/data", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/data")>()), ...dataApi }));

const table: ModelTable = {
  name: "girokonto",
  displayName: "Girokonto",
  columns: [
    { name: "id", type: "integer", primaryKey: true },
    { name: "buchungstag", displayName: "Buchungstag", type: "date" },
    { name: "betrag_eur", displayName: "Betrag (EUR)", type: "currency" },
    { name: "kategorie_id", displayName: "Kategorie", type: "reference", references: "kategorie" },
    { name: "auftraggeber", displayName: "Auftraggeber", type: "text" },
    { name: "meta", type: "json" },
  ],
};
const field = (name: string) => fieldsOf(table).find((f) => f.name === name)!;
const empty = (type: DashboardWidget["type"]): DashboardWidget => ({
  id: "w",
  type,
  title: "",
  position: { x: 0, y: 0, w: 6, h: 4 },
  data: { query: { source: "girokonto", measures: [] } },
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("Lens model", () => {
  it("offers every field but the key and JSON, by kind", () => {
    expect(fieldsOf(table).map((f) => `${f.name}:${f.kind}`)).toEqual(["buchungstag:date", "betrag_eur:number", "kategorie_id:category", "auftraggeber:text"]);
  });

  it("puts a dropped field where it fits", () => {
    let bar = addField(empty("bar"), field("buchungstag"), table);
    bar = addField(bar, field("betrag_eur"), table);
    bar = addField(bar, field("kategorie_id"), table);
    expect(bar.data!.query).toMatchObject({
      groupBy: [{ column: "buchungstag", bucket: "month" }, { column: "kategorie_id", top: 10, other: true }],
      measures: [{ fn: "sum", column: "betrag_eur" }],
    });
    expect(describeWidget(bar, table)).toBe("Sum of Betrag (EUR) by Buchungstag per month, split by Top 10 Kategorie + other");

    const donut = addField(addField(empty("donut"), field("kategorie_id"), table), field("betrag_eur"), table);
    expect(donut.data!.query).toMatchObject({ groupBy: [{ column: "kategorie_id", top: 6, other: true }], measures: [{ fn: "sum", column: "betrag_eur" }] });
    // A text field as a value counts its distinct values.
    expect(addField(empty("kpi"), field("auftraggeber"), table, "value").data!.query!.measures).toEqual([{ name: "unique_auftraggeber", fn: "count_distinct", column: "auftraggeber" }]);
  });

  it("keeps fields when the chart type changes and drops what no longer applies", () => {
    const line: DashboardWidget = {
      ...empty("line"),
      data: {
        query: {
          source: "girokonto",
          groupBy: [{ column: "buchungstag", bucket: "month", as: "month" }, { column: "kategorie_id" }],
          measures: [{ name: "balance", fn: "sum", column: "betrag_eur", running: true, carryIn: true }],
        },
      },
    };
    const donut = switchType(line, "donut", table);
    expect(donut.data!.query!.groupBy).toEqual([{ column: "kategorie_id", top: 6, other: true }]);
    // No date group left: no running total.
    expect(donut.data!.query!.measures).toEqual([{ name: "balance", fn: "sum", column: "betrag_eur" }]);

    const kpi = switchType(line, "kpi", table);
    expect(kpi.data!.query!.groupBy).toEqual([{ column: "buchungstag", bucket: "month", as: "month" }]);
    expect(kpi.data!.query!.measures![0].running).toBe(true);

    expect(suggestions(line, table).map((s) => s.type)).toEqual(["area", "bar", "kpi", "table"]);
    expect(normalize({ ...line, data: { ...line.data, asOf: true, query: { ...line.data!.query!, groupBy: [] } } }).data!.asOf).toBeUndefined();
  });

  it("switches a table between summarized and individual rows", () => {
    const summarized = addField(addField(empty("table"), field("auftraggeber"), table), field("betrag_eur"), table);
    const rows = setTableMode(summarized, true, table);
    expect(rows.data!.rows).toEqual({ columns: ["auftraggeber", "betrag_eur"], limit: 25 });
    expect(describeWidget(rows, table)).toBe("Rows of Girokonto: Auftraggeber, Betrag (EUR)");
    expect(setTableMode(rows, false, table).data!.rows).toBeUndefined();
  });
});

const topResult: QueryResult = {
  data: [
    { kategorie_id: 1, kategorie_id_label: "Miete", kategorie_id_other: false, spent: -1150 },
    { kategorie_id: null, kategorie_id_label: null, kategorie_id_other: false, spent: -10 },
    { kategorie_id: null, kategorie_id_label: null, kategorie_id_other: true, spent: -282.05 },
  ],
  meta: {
    truncated: false,
    columns: [
      { name: "kategorie_id_other", type: "boolean", role: "other", labelOf: "kategorie_id" },
      { name: "kategorie_id", type: "reference", role: "group" },
      { name: "kategorie_id_label", type: "text", role: "label", labelOf: "kategorie_id" },
      { name: "spent", type: "currency", role: "measure" },
    ],
  },
};

describe("Lens rendering", () => {
  it("names the remaining top values Other", () => {
    const model = toChartModel(topResult, { source: "girokonto", groupBy: [{ column: "kategorie_id", top: 1, other: true }] }, { from: null, to: null });
    expect(model.categories).toEqual(["Miete", "(none)", "Other"]);
  });

  it("shows tables with Other, a total row and individual rows", async () => {
    const { WidgetContent } = await import("../features/dashboards/widgets/WidgetContent");
    const tokens = { text: "", muted: "", grid: "", accent: "", palette: [], negative: "", surface: "" } as never;
    queryApi.runQuery.mockImplementation(async (query: { groupBy?: unknown[] }) =>
      (query.groupBy ?? []).length === 0
        ? { data: [{ spent: -1442.05 }], meta: { truncated: false, columns: [{ name: "spent", type: "currency", role: "measure" }] } }
        : topResult,
    );
    const summarized: DashboardWidget = {
      ...empty("table"),
      title: "Spending",
      data: { query: { source: "girokonto", groupBy: [{ column: "kategorie_id", top: 1, other: true }], measures: [{ name: "spent", fn: "sum", column: "betrag_eur" }] } },
      display: { summary: true },
    };
    render(<WidgetContent widget={summarized} range={{ from: null, to: null }} refreshToken={0} tokens={tokens} />);
    const grid = await screen.findByRole("table");
    await waitFor(() => expect(within(grid).getByText("Total")).toBeInTheDocument());
    expect(within(grid).getByText("Other")).toBeInTheDocument();
    expect(within(grid).getByText("Miete")).toBeInTheDocument();
    cleanup();

    dataApi.fetchTableRows.mockResolvedValue({ data: [{ id: 1, auftraggeber: "REWE", betrag_eur: -45.9 }], meta: { page: 1, per_page: 10, total: 9, total_pages: 1 } });
    const rows: DashboardWidget = { ...empty("table"), data: { query: { source: "girokonto", filter: { betrag_eur: { lt: 0 } } }, rows: { columns: ["auftraggeber", "betrag_eur"], sort: "-betrag_eur", limit: 10 }, dateColumn: "buchungstag" } };
    render(<WidgetContent widget={rows} range={{ from: "2026-09-01", to: "2026-10-01" }} refreshToken={0} tokens={tokens} />);
    expect(await screen.findByText("REWE")).toBeInTheDocument();
    expect(screen.getByText("1 of 9 rows")).toBeInTheDocument();
    expect(dataApi.fetchTableRows).toHaveBeenCalledWith("girokonto", {
      page: 1,
      perPage: 10,
      sort: [{ column: "betrag_eur", dir: "desc" }],
      search: "",
      filters: [
        { column: "betrag_eur", op: "lt", value: "0" },
        { column: "buchungstag", op: "gte", value: "2026-09-01" },
        { column: "buchungstag", op: "lt", value: "2026-10-01" },
      ],
    });
  });

  it("shows a key figure with a secondary value, a goal and a trend", async () => {
    const { WidgetContent } = await import("../features/dashboards/widgets/WidgetContent");
    queryApi.runQuery.mockResolvedValue({
      data: [
        { period: "2026-08-01", rate: 0.5, bookings: 8 },
        { period: "2026-09-01", rate: 0.556, bookings: 9 },
      ],
      meta: { truncated: false, columns: [{ name: "period", type: "date", role: "group" }, { name: "rate", type: "float", role: "measure" }, { name: "bookings", type: "integer", role: "measure" }] },
    } satisfies QueryResult);
    const kpi: DashboardWidget = {
      ...empty("kpi"),
      data: {
        query: {
          source: "girokonto",
          groupBy: [{ column: "buchungstag", bucket: "month", as: "period" }],
          measures: [{ name: "rate", formula: "SUM(betrag_eur) / SUM(betrag_eur WHERE betrag_eur > 0)" }, { name: "bookings", formula: "COUNT()" }],
        },
      },
      display: { format: "percent", goal: 0.7, trend: true, higherIsBetter: true },
    };
    render(<WidgetContent widget={kpi} range={{ from: null, to: null }} refreshToken={0} tokens={{} as never} />);
    expect(await screen.findByText(/bookings: 9/)).toBeInTheDocument();
    expect(screen.getByRole("img", { name: "Trend" })).toBeInTheDocument();
    expect(screen.getByText(/of 70/)).toBeInTheDocument();
    expect(document.querySelector(".widget-kpi__delta--up")).toBeTruthy();
  });
});
