import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { DashboardWidget } from "../api/dashboards";
import type { QueryResult } from "../api/query";
import { buildChartOption, type ChartTokens } from "../features/dashboards/charts/chartOptions";
import {
  annotationsAt,
  applyMeasureShows,
  placeAnnotations,
  changes,
  movingAverage,
  resolveReferenceLines,
  shiftedRange,
  withShifted,
  withTotal,
} from "../features/dashboards/chartTransforms";
import { runnableQuery, withoutChartOptions } from "../features/dashboards/formulaQueries";
import { measureAsFormula } from "../features/dashboards/lens/filters";
import { describeMeasure, normalize } from "../features/dashboards/lens/model";
import { newVariableSource } from "../features/dashboards/VariablesEditor";
import { AnnotationsEditor } from "../features/dashboards/AnnotationsEditor";
import { WidgetContent } from "../features/dashboards/widgets/WidgetContent";
import type { ChartModel } from "../features/dashboards/widgetData";
import type { ModelTable } from "../types/api";

const queryApi = vi.hoisted(() => ({ runQuery: vi.fn() }));
vi.mock("../api/query", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/query")>()), ...queryApi }));
// ECharts needs a real layout engine; the tests check the data it is given.
vi.mock("../features/dashboards/charts/EChart", () => ({
  EChart: ({ option, label }: { option: { series?: { name?: string; data?: unknown[] }[] }; label: string }) => (
    <div role="img" aria-label={label} data-series={JSON.stringify(option.series?.map((s) => ({ name: s.name, data: s.data })))} />
  ),
}));

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

const tokens: ChartTokens = {
  text: "#eee",
  muted: "#999",
  line: "#333",
  surface: "#111",
  popover: "#222",
  palette: ["#3b82f6", "#14b8a6"],
  positive: "#4ade80",
  negative: "#f87171",
};

const monthly = (values: (number | null)[], name = "spent"): ChartModel => ({
  categories: values.map((_, i) => `2026-${String(i + 1).padStart(2, "0")}-01`),
  bucket: "month",
  format: "currency",
  series: [{ name, values }],
});

type Series = {
  name: string;
  type: string;
  stack?: string;
  lineStyle?: { type?: string };
  markLine?: { data: { yAxis?: number; xAxis?: string; name: string; lineStyle?: { color: string } }[] };
  markArea?: { data: [{ xAxis: string; name: string }, { xAxis: string }][] };
  itemStyle?: { color?: unknown; opacity?: number };
};
const seriesOf = (option: unknown) => (option as { series: Series[] }).series;

describe("show as", () => {
  it("computes the change from the period before", () => {
    expect(changes([100, 120, null, 90, 60])).toEqual([null, 20, null, null, -30]);
  });

  it("averages the last periods, empty until there are enough", () => {
    expect(movingAverage([3, 6, 9, 12, null, 3], 3)).toEqual([null, null, 6, 9, null, null]);
    expect(movingAverage([1, 2], 1)).toEqual([1, 2]);
  });

  it("applies each measure's choice to its series, or the first measure's to every series of a breakdown", () => {
    const model = { ...monthly([10, 15, 5]), series: [{ name: "spent", values: [10, 15, 5] }, { name: "income", values: [1, 2, 3] }] };
    const shown = applyMeasureShows(model, [{ name: "spent", fn: "sum", column: "amount", show: "change" }, { name: "income", fn: "sum", column: "amount" }], false);
    expect(shown.series.map((s) => s.values)).toEqual([[null, 5, -10], [1, 2, 3]]);

    const split = applyMeasureShows(model, [{ name: "spent", fn: "sum", column: "amount", show: "moving_average", window: 2 }], true);
    expect(split.series.map((s) => s.values)).toEqual([[null, 12.5, 10], [null, 1.5, 2.5]]);
    // Without dates along the axis nothing changes.
    expect(applyMeasureShows({ ...model, bucket: undefined }, [{ name: "spent", show: "change" }], false)).toEqual({ ...model, bucket: undefined });
  });

  it("is never sent to the API", () => {
    const query = { source: "t", measures: [{ name: "spent", fn: "sum" as const, column: "amount", show: "moving_average" as const, window: 3 }] };
    expect(withoutChartOptions(query).measures).toEqual([{ name: "spent", fn: "sum", column: "amount" }]);
    expect(runnableQuery(query, { query }, { from: null, to: null }, {}).measures).toEqual([{ name: "spent", fn: "sum", column: "amount" }]);
  });
});

describe("compare with an earlier period", () => {
  it("shifts whole months by the range's length or by a year", () => {
    const last12 = { from: "2025-11-01", to: "2026-11-01" };
    expect(shiftedRange(last12, "previous")).toEqual({ from: "2024-11-01", to: "2025-11-01" });
    expect(shiftedRange(last12, "year")).toEqual({ from: "2024-11-01", to: "2025-11-01" });
    expect(shiftedRange({ from: "2026-07-01", to: "2026-10-01" }, "previous")).toEqual({ from: "2026-04-01", to: "2026-07-01" });
    // Other ranges by days; month ends stay in their month; open ranges have nothing before them.
    expect(shiftedRange({ from: "2026-09-10", to: "2026-09-20" }, "previous")).toEqual({ from: "2026-08-31", to: "2026-09-10" });
    expect(shiftedRange({ from: "2024-02-29", to: "2024-03-10" }, "year")).toEqual({ from: "2023-02-28", to: "2023-03-10" });
    expect(shiftedRange({ from: null, to: null }, "year")).toBeNull();
  });

  it("adds the earlier values as dashed series, period by period, never stacked", () => {
    const merged = withShifted(monthly([10, 20, 30]), monthly([1, 2, 3, 4]), "year");
    expect(merged.series[1]).toEqual({ name: "spent (a year earlier)", values: [1, 2, 3], kind: "shifted" });

    const option = buildChartOption("area", merged, tokens, { stacked: true });
    const [current, earlier] = seriesOf(option);
    expect(current).toMatchObject({ stack: "total" });
    expect(earlier.stack).toBeUndefined();
    expect(earlier.lineStyle?.type).toBe("dashed");
  });

  it("runs the widget's query again over the earlier range", async () => {
    const result: QueryResult = {
      data: [{ month: "2026-09-01", spent: -100 }],
      meta: { truncated: false, columns: [{ name: "month", type: "date", role: "group" }, { name: "spent", type: "currency", role: "measure" }] },
    };
    queryApi.runQuery.mockResolvedValue(result);
    const widget: DashboardWidget = {
      id: "w",
      type: "line",
      title: "Spending",
      position: { x: 0, y: 0, w: 6, h: 4 },
      data: {
        timeShift: "year",
        query: { source: "girokonto", groupBy: [{ column: "buchungstag", bucket: "month", as: "month" }], measures: [{ name: "spent", fn: "sum", column: "betrag_eur" }] },
      },
    };
    render(<WidgetContent widget={widget} range={{ from: "2026-07-01", to: "2026-10-01" }} refreshToken={0} tokens={tokens} />);
    await waitFor(() => expect(queryApi.runQuery).toHaveBeenCalledTimes(2));
    const filters = queryApi.runQuery.mock.calls.map((call) => call[0].filter);
    expect(filters).toContainEqual({ buchungstag: { gte: "2026-07-01", lt: "2026-10-01" } });
    expect(filters).toContainEqual({ buchungstag: { gte: "2025-07-01", lt: "2025-10-01" } });
    await waitFor(() => expect(JSON.parse(screen.getByRole("img", { name: "Spending" }).dataset.series!)).toHaveLength(2));
    expect(JSON.parse(screen.getByRole("img", { name: "Spending" }).dataset.series!)[1].name).toBe("spent (a year earlier)");
  });
});

describe("stacks, reference lines and colour by sign", () => {
  it("adds the total of a stack as a line on top, left out of the stack", () => {
    const model = { ...monthly([100, -40]), series: [{ name: "Gehalt", values: [100, null] }, { name: "Miete", values: [-60, -40] }] };
    const withLine = withTotal(model);
    expect(withLine.series[2]).toEqual({ name: "Total", values: [40, -40], kind: "total" });
    const total = seriesOf(buildChartOption("bar", withLine, tokens, { stacked: true }))[2];
    expect(total).toMatchObject({ type: "line", name: "Total" });
    expect(total.stack).toBeUndefined();
  });

  it("lists the stack in the tooltip, then the earlier period, and totals the stack only", () => {
    const model = withShifted(withTotal({ ...monthly([1]), series: [{ name: "Gehalt", values: [100] }, { name: "Miete", values: [-60] }] }), { ...monthly([1]), series: [{ name: "Gehalt", values: [90] }] }, "year");
    const option = buildChartOption("bar", model, tokens, { stacked: true }) as { tooltip: { formatter: (p: unknown) => string } };
    const html = option.tooltip.formatter(
      model.series.map((series, seriesIndex) => ({ seriesIndex, seriesName: series.name, value: series.values[0], color: "#000", axisValue: "2026-01-01" })),
    );
    const names = [...html.matchAll(/<span>([^<]*)<\/span>/g)].map((m) => m[1]);
    expect(names).toEqual(["Gehalt", "Miete", "Gehalt (a year earlier)", "Total"]);
    expect(html).toMatch(/€40[.,]00/);
  });

  it("draws reference lines at numbers or variables and keeps them in view", () => {
    const lines = resolveReferenceLines([{ value: 500, label: "Budget" }, { value: "burn_rate" }, { value: "missing" }], { burn_rate: 1200 });
    expect(lines).toEqual([{ value: 500, label: "Budget" }, { value: 1200, label: "burn_rate" }]);

    const option = buildChartOption("line", monthly([100, 200]), tokens, { referenceLines: lines }) as {
      yAxis: { max: (e: { max: number }) => number; min: (e: { min: number }) => number };
    };
    expect(seriesOf(option)[0].markLine?.data).toEqual([{ yAxis: 500, name: "Budget" }, { yAxis: 1200, name: "burn_rate" }]);
    expect(option.yAxis.max({ max: 200 })).toBe(1200);
    expect(option.yAxis.min({ min: 100 })).toBe(100);
  });

  it("colours one series by sign: bars per value, lines where they cross 0", () => {
    const bars = seriesOf(buildChartOption("bar", monthly([50, -20]), tokens, { colorBySign: true }))[0];
    const color = bars.itemStyle?.color as (p: { value: number }) => string;
    expect([color({ value: 50 }), color({ value: -20 })]).toEqual([tokens.positive, tokens.negative]);

    const line = buildChartOption("line", monthly([50, -20]), tokens, { colorBySign: true }) as { visualMap?: { pieces: unknown[] } };
    expect(line.visualMap?.pieces).toEqual([
      { gte: -1e15, lt: 0, color: tokens.negative },
      { gte: 0, lte: 1e15, color: tokens.positive },
    ]);
    // Not with several series.
    const two = { ...monthly([1]), series: [{ name: "a", values: [1] }, { name: "b", values: [-1] }] };
    expect((buildChartOption("line", two, tokens, { colorBySign: true }) as { visualMap?: unknown }).visualMap).toBeUndefined();
  });
});

describe("median, percentiles and the editor's model", () => {
  const table = { name: "girokonto", columns: [{ name: "betrag_eur", displayName: "Betrag (EUR)", type: "currency" }] } as ModelTable;

  it("describes the new functions and how a value is shown", () => {
    expect(describeMeasure({ name: "m", fn: "median", column: "betrag_eur" }, table)).toBe("Median of Betrag (EUR)");
    expect(describeMeasure({ name: "p", fn: "percentile", column: "betrag_eur", percentile: 90 }, table)).toBe("90th percentile of Betrag (EUR)");
    expect(describeMeasure({ name: "p", fn: "percentile", column: "betrag_eur", percentile: 22 }, table)).toBe("22nd percentile of Betrag (EUR)");
    expect(describeMeasure({ name: "s", fn: "sum", column: "betrag_eur", show: "change" }, table)).toBe("Change in Sum of Betrag (EUR)");
    expect(describeMeasure({ name: "spent", formula: "SUM(x)", show: "moving_average", window: 6 }, table)).toBe("spent, 6-period average");
  });

  it("writes them as formulas for values at the end of each period", () => {
    expect(measureAsFormula({ name: "p", fn: "percentile", column: "amount", percentile: 75 }, "booked_on")).toBe("PERCENTILE(amount, 75 WHERE booked_on <= BUCKET_END())");
    expect(measureAsFormula({ name: "m", fn: "median", column: "amount" }, "booked_on")).toBe("MEDIAN(amount WHERE booked_on <= BUCKET_END())");
  });

  it("drops what no longer applies: show as without a date axis, comparisons with a breakdown", () => {
    const base: DashboardWidget = {
      id: "w",
      type: "line",
      title: "",
      position: { x: 0, y: 0, w: 6, h: 4 },
      data: {
        timeShift: "year",
        query: { source: "t", groupBy: [{ column: "booked_on", bucket: "month" }], measures: [{ name: "spent", fn: "sum", column: "amount", show: "change" }] },
      },
    };
    expect(normalize(base).data).toMatchObject({ timeShift: "year", query: { measures: [{ show: "change" }] } });
    const split = normalize({ ...base, data: { ...base.data, query: { ...base.data!.query!, groupBy: [{ column: "booked_on", bucket: "month" }, { column: "category" }] } } });
    expect(split.data?.timeShift).toBeUndefined();
    const byCategory = normalize({ ...base, data: { ...base.data, query: { ...base.data!.query!, groupBy: [{ column: "category" }] } } });
    expect(byCategory.data?.query?.measures?.[0].show).toBeUndefined();
    expect(normalize({ ...base, type: "table" }).data?.query?.measures?.[0].show).toBeUndefined();
  });
});

describe("new dashboard variables", () => {
  const tables = [{ name: "customers", columns: [] }, { name: "girokonto", columns: [] }] as unknown as ModelTable[];
  const widget = (source: string, dateColumn?: string): DashboardWidget => ({
    id: source,
    type: "bar",
    title: "",
    position: { x: 0, y: 0, w: 6, h: 4 },
    data: { query: { source, groupBy: dateColumn ? [{ column: dateColumn, bucket: "month" }] : [], measures: [] } },
  });

  it("start on the table most widgets use, with their date column", () => {
    expect(newVariableSource([], [widget("girokonto", "buchungstag"), widget("girokonto"), widget("customers")], tables)).toEqual({ source: "girokonto", dateColumn: "buchungstag" });
  });

  it("follow the last variable, and fall back to the first table", () => {
    expect(newVariableSource([{ name: "a", source: "customers", formula: "COUNT()", dateColumn: "created" }], [widget("girokonto")], tables)).toEqual({ source: "customers", dateColumn: "created" });
    expect(newVariableSource([], [], tables)).toEqual({ source: "customers" });
  });
});

describe("annotations", () => {
  const months = ["2026-07-01", "2026-08-01", "2026-09-01", "2026-10-01"];

  it("puts each day in its period, joins events of one period and cuts spans to the axis", () => {
    const placed = placeAnnotations(
      [
        { date: "2026-08-15", label: "Salary raise", color: "teal" },
        { date: "2026-08-20", label: "Moved" },
        { date: "2026-06-30", label: "Before the range" },
        { date: "2026-09-10", to: "2026-12-24", label: "Parental leave", color: "violet" },
        { date: "2026-09-01", label: "  " },
        { date: "not a date", label: "Broken" },
      ],
      months,
      "month",
    );
    expect(placed.points).toEqual([{ key: "2026-08-01", label: "Salary raise · Moved", color: "teal" }]);
    expect(placed.ranges).toEqual([{ from: "2026-09-01", to: "2026-10-01", label: "Parental leave", color: "violet" }]);
    expect(annotationsAt(placed, "2026-08-01")).toEqual(["Salary raise · Moved"]);
    expect(annotationsAt(placed, "2026-10-01")).toEqual(["Parental leave"]);
    // Nothing without dates along the axis.
    expect(placeAnnotations([{ date: "2026-08-15", label: "x" }], ["Miete"], undefined)).toEqual({ points: [], ranges: [] });
  });

  it("draws lines and spans on the chart, beside the reference lines, and names them in the tooltip", () => {
    const placed = placeAnnotations([{ date: "2026-08-15", label: "Salary raise", color: "teal" }, { date: "2026-09-01", to: "2026-10-31", label: "Vacation" }], months, "month");
    const model = { ...monthly([1, 2, 3, 4]), categories: months };
    const option = buildChartOption("line", model, tokens, { annotations: placed, referenceLines: [{ value: 3, label: "Budget" }] }) as {
      grid: { top: number };
      tooltip: { formatter: (p: unknown) => string };
    };
    const first = seriesOf(option)[0];
    expect(first.markLine?.data.map((d) => d.xAxis ?? d.yAxis)).toEqual([3, "2026-08-01"]);
    expect(first.markLine?.data[1].lineStyle?.color).toBe("#14b8a6");
    expect(first.markArea?.data[0].map((d) => d.xAxis)).toEqual(["2026-09-01", "2026-10-01"]);
    expect(option.grid.top).toBeGreaterThan(12);
    const html = option.tooltip.formatter([{ seriesIndex: 0, seriesName: "spent", value: 2, color: "#000", axisValue: "2026-08-01" }]);
    expect(html).toContain("◆ Salary raise");
  });

  it("are added and edited in their panel", () => {
    const onChange = vi.fn();
    const { rerender } = render(<AnnotationsEditor annotations={[]} onChange={onChange} onClose={() => {}} />);
    fireEvent.click(screen.getByRole("button", { name: "Add annotation" }));
    const added = onChange.mock.calls[0][0];
    expect(added).toHaveLength(1);
    expect(added[0].date).toMatch(/^\d{4}-\d{2}-\d{2}$/);

    rerender(<AnnotationsEditor annotations={[{ date: "2026-08-15", label: "" }]} onChange={onChange} onClose={() => {}} />);
    fireEvent.change(screen.getByLabelText("Label of annotation 1"), { target: { value: "Salary raise" } });
    expect(onChange).toHaveBeenLastCalledWith([{ date: "2026-08-15", label: "Salary raise" }]);
    fireEvent.change(screen.getByLabelText("End of annotation 1"), { target: { value: "2026-08-31" } });
    expect(onChange).toHaveBeenLastCalledWith([{ date: "2026-08-15", label: "", to: "2026-08-31" }]);
    fireEvent.change(screen.getByLabelText("Colour of annotation 1"), { target: { value: "rose" } });
    expect(onChange).toHaveBeenLastCalledWith([{ date: "2026-08-15", label: "", color: "rose" }]);
    fireEvent.click(screen.getByRole("button", { name: "Remove annotation 1" }));
    expect(onChange).toHaveBeenLastCalledWith([]);
  });
});
