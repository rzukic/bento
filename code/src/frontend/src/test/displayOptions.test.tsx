import { cleanup, render, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { DashboardWidget } from "../api/dashboards";
import type { QueryResult } from "../api/query";
import { buildChartOption, type ChartTokens } from "../features/dashboards/charts/chartOptions";
import { resolveRange } from "../features/dashboards/dateRange";
import { asOfPeriod } from "../features/dashboards/formulaQueries";
import { toChartModel } from "../features/dashboards/widgetData";
import { WidgetContent } from "../features/dashboards/widgets/WidgetContent";
import { formatNumber } from "../lib/format";

// Beta feedback, package 3: forecasts, empty periods, the value axis, per-column formats in tables.

const queryApi = vi.hoisted(() => ({ runQuery: vi.fn() }));
vi.mock("../api/query", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/query")>()), ...queryApi }));

const tokens: ChartTokens = { text: "#eee", muted: "#999", line: "#333", surface: "#111", popover: "#222", palette: ["#3b82f6", "#14b8a6"], positive: "#4ade80", negative: "#f87171" };
const today = new Date(2026, 9, 10);

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("forecast ranges", () => {
  it("next … presets and a chosen end in the future reach ahead; other ranges stop at today", () => {
    expect(resolveRange({ preset: "next-20-years" }, today)).toEqual({ from: "2026-10-01", to: "2046-10-01", future: true });
    expect(resolveRange({ preset: "next-12-months" }, today)).toEqual({ from: "2026-10-01", to: "2027-10-01", future: true });
    expect(resolveRange({ from: "2024-01-01", to: "2045-12-31" }, today)).toEqual({ from: "2024-01-01", to: "2046-01-01", future: true });
    expect(resolveRange({ preset: "this-year" }, today).future).toBeUndefined();

    // Values at period ends: up to the end of a forecast (BUCKET_END() ahead), else today.
    expect(asOfPeriod(resolveRange({ preset: "next-5-years" }, today), today)).toEqual({ from: "2026-10-01", to: "2031-09-30" });
    expect(asOfPeriod(resolveRange({ preset: "this-year" }, today), today)).toEqual({ from: "2026-01-01", to: "2026-10-10" });
  });

  it("draws a forecast's future periods on the axis", () => {
    const result: QueryResult = {
      data: [{ year: "2026-01-01", value: 100 }],
      meta: { truncated: false, columns: [{ name: "year", type: "date", role: "group" }, { name: "value", type: "number", role: "measure" }] },
    };
    const query = { source: "t", groupBy: [{ column: "d", bucket: "year" as const, as: "year" }], measures: [{ name: "value", fn: "sum" as const, column: "v" }] };
    const forecast = toChartModel(result, query, { from: "2026-01-01", to: "2030-01-01", future: true }, undefined, today);
    expect(forecast.categories).toEqual(["2026-01-01", "2027-01-01", "2028-01-01", "2029-01-01"]);
    const thisYearOnly = toChartModel(result, query, { from: "2026-01-01", to: "2030-01-01" }, undefined, today);
    expect(thisYearOnly.categories).toEqual(["2026-01-01"]);
  });
});

describe("periods without a value", () => {
  // A depot over the last 6 months, bought in August: 0 before, a gap in October.
  const result: QueryResult = {
    data: [
      { month: "2026-05-01", depot: 0 },
      { month: "2026-06-01", depot: 0 },
      { month: "2026-08-01", depot: 1200 },
      { month: "2026-09-01", depot: 1300 },
    ],
    meta: { truncated: false, columns: [{ name: "month", type: "date", role: "group" }, { name: "depot", type: "currency", role: "measure" }] },
  };
  const query = { source: "t", groupBy: [{ column: "d", bucket: "month" as const, as: "month" }], measures: [{ name: "depot", fn: "sum" as const, column: "v" }] };
  const range = { from: "2026-05-01", to: "2026-11-01" };

  it("count as 0 by default", () => {
    expect(toChartModel(result, query, range, undefined, today).series[0].values).toEqual([0, 0, 0, 1200, 1300, 0]);
  });

  it("leave a gap, or are connected, and the periods before the first value are left out", () => {
    const gap = toChartModel(result, query, range, undefined, today, { emptyPeriods: "gap" });
    expect(gap.categories).toEqual(["2026-08-01", "2026-09-01", "2026-10-01"]);
    expect(gap.series[0].values).toEqual([1200, 1300, null]);
    expect(toChartModel(result, query, range, undefined, today, { emptyPeriods: "connect" }).series[0].values).toEqual([1200, 1300, null]);
  });

  it("stay empty, not 0, while a wider range is loading", () => {
    const stale = toChartModel(result, query, { from: "2026-01-01", to: "2026-11-01" }, undefined, today, { stale: true });
    expect(stale.categories[0]).toBe("2026-01-01");
    expect(stale.series[0].values.slice(0, 4)).toEqual([null, null, null, null]);
  });
});

describe("value axis and lines", () => {
  const model = { categories: ["a", "b"], series: [{ name: "Price", values: [98, null] }], format: "number" as const };
  type Axis = { scale?: boolean; min?: unknown };
  const axis = (option: unknown) => ((option as { yAxis: Axis[] | Axis }).yAxis instanceof Array ? (option as { yAxis: Axis[] }).yAxis[0] : (option as { yAxis: Axis }).yAxis);

  it("starts near the data, at 0 or at a number", () => {
    expect(axis(buildChartOption("line", model, tokens)).scale).toBeUndefined();
    expect(axis(buildChartOption("line", model, tokens, { yMin: "auto" })).scale).toBe(true);
    expect(axis(buildChartOption("line", model, tokens, { yMin: 90 })).min).toBe(90);
    // "zero" keeps 0 even with a reference line that would pin the range near the data.
    const zero = axis(buildChartOption("line", model, tokens, { yMin: "zero", referenceLines: [{ value: 100, label: "start" }] })).min as (e: { min: number }) => number;
    expect(zero({ min: 98 })).toBe(0);
  });

  it("connects lines over empty periods when asked", () => {
    const series = (option: unknown) => (option as { series: { connectNulls: boolean }[] }).series[0].connectNulls;
    expect(series(buildChartOption("line", model, tokens))).toBe(false);
    expect(series(buildChartOption("line", model, tokens, { connectNulls: true }))).toBe(true);
  });
});

describe("table columns", () => {
  it("write a signed number", () => {
    // Intl puts a no-break space before % and €.
    const signed = (value: number, kind: "percent" | "currency" | "number") => formatNumber(value, kind, "de-DE", true).replace(/[\u00a0\u202f]/g, " ");
    expect(signed(0.2217, "percent")).toBe("+22,2 %");
    expect(signed(-0.05, "percent")).toBe("-5 %");
    expect(signed(1250, "currency")).toBe("+1.250,00 €");
    expect(signed(0, "number")).toBe("0");
  });

  it("each in its own format, with a sign and green or red", async () => {
    queryApi.runQuery.mockResolvedValue({
      data: [
        { isin: "IE00B4L5Y983", value: 12500, xirr: 0.2217 },
        { isin: "IE00BK5BQT80", value: 800, xirr: -0.05 },
      ],
      meta: {
        truncated: false,
        columns: [
          { name: "isin", type: "text", role: "group" },
          { name: "value", type: "currency", role: "measure" },
          { name: "xirr", type: "number", role: "measure" },
        ],
      },
    } satisfies QueryResult);
    const widget: DashboardWidget = {
      id: "positions",
      type: "table",
      title: "Positions",
      position: { x: 0, y: 0, w: 6, h: 4 },
      data: { query: { source: "securities", groupBy: [{ column: "isin" }], measures: [{ name: "value", fn: "sum", column: "value" }, { name: "xirr", formula: "XIRR(amount, date)" }] }, dateColumn: null },
      display: { format: "currency", columns: { xirr: { format: "percent", signed: true, colorBySign: true } } },
    };
    render(<WidgetContent widget={widget} range={{ from: null, to: null }} refreshToken={0} tokens={tokens} />);

    const gain = await screen.findByText(/22[.,]2/);
    expect(gain.textContent).toMatch(/^\+/);
    expect(gain.className).toContain("widget-table__number--positive");
    const loss = screen.getByText(/^-5/);
    expect(loss.className).toContain("widget-table__number--negative");
    // The other measure keeps the table's format.
    const rows = screen.getAllByRole("row");
    expect(within(rows[1]).getByText(/12[.,]500/).className).toBe("widget-table__number");
  });
});
