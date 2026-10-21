import { describe, expect, it } from "vitest";
import { buildChartOption, buildSankeyOption, type ChartTokens } from "../features/dashboards/charts/chartOptions";
import { withMeasureColors, withShifted } from "../features/dashboards/chartTransforms";
import { resolveRange } from "../features/dashboards/dateRange";
import { withoutChartOptions } from "../features/dashboards/formulaQueries";
import { toSankeyModel } from "../features/dashboards/sankey";
import type { ChartModel } from "../features/dashboards/widgetData";

// Gaps found comparing with the Finanzfluss Copilot dashboard: more ranges,
// a colour per value, shares in donuts and Sankeys, the total in a donut.

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

describe("date ranges", () => {
  const today = new Date(2026, 9, 4);

  it("adds the last 7 days and the last 3 and 5 years in whole months", () => {
    expect(resolveRange({ preset: "last-7-days" }, today)).toEqual({ from: "2026-09-28", to: "2026-10-05" });
    expect(resolveRange({ preset: "last-3-years" }, today)).toEqual({ from: "2023-11-01", to: "2026-11-01" });
    expect(resolveRange({ preset: "last-5-years" }, today)).toEqual({ from: "2021-11-01", to: "2026-11-01" });
  });
});

describe("a colour per value", () => {
  const model: ChartModel = {
    categories: ["2026-08-01", "2026-09-01"],
    bucket: "month",
    format: "currency",
    series: [
      { name: "income", values: [3000, 3250] },
      { name: "expenses", values: [-1200, -1442] },
    ],
  };
  const measures = [
    { name: "income", formula: "SUM(amount WHERE amount > 0)", color: "green" as const },
    { name: "expenses", formula: "SUM(amount WHERE amount < 0)", color: "red" as const },
  ];

  it("paints income green and expenses red, also their earlier period", () => {
    const coloured = withShifted(withMeasureColors(model, measures, false), model, "year");
    const series = (buildChartOption("bar", coloured, tokens) as { series: { name: string; color: string }[] }).series;
    expect(series.map((s) => s.color)).toEqual([tokens.positive, tokens.negative, tokens.positive, tokens.negative]);
    // A breakdown keeps the palette per value; colours never reach the API.
    expect(withMeasureColors(model, measures, true)).toBe(model);
    expect(withoutChartOptions({ source: "t", measures }).measures).toEqual(measures.map(({ color: _c, ...m }) => m));
  });
});

describe("shares", () => {
  it("shows a donut's total in the middle and each slice's share in the tooltip", () => {
    const donut: ChartModel = { categories: ["Miete", "Freizeit"], format: "currency", series: [{ name: "spent", values: [-1150, -62] }] };
    const option = buildChartOption("donut", donut, tokens) as {
      title: { text: string };
      tooltip: { formatter: (p: unknown) => string };
    };
    expect(option.title.text).toMatch(/1[.,]?2/);
    expect(option.tooltip.formatter({ name: "Miete", value: 1150, percent: 94.88 })).toMatch(/94[.,]9\s?%/);
  });

  it("labels a cash-flow Sankey with shares of the income when asked, and always names them in the tooltip", () => {
    const result = {
      data: [
        { cat: "Gehalt", amount: 4000 },
        { cat: "Miete", amount: -1000 },
      ],
      meta: {
        truncated: false,
        columns: [
          { name: "cat", type: "text", role: "group" as const },
          { name: "amount", type: "currency", role: "measure" as const },
        ],
      },
    };
    const model = toSankeyModel(result, undefined, "balance", "currency");
    const option = buildSankeyOption(model, tokens, { shares: true }) as {
      series: { label: { formatter: (p: unknown) => string } }[];
      tooltip: { formatter: (p: unknown) => string };
    };
    expect(option.series[0].label.formatter({ name: "out:Miete", value: 1000 })).toMatch(/^Miete\s+25\s?%$/);
    expect(option.tooltip.formatter({ dataType: "edge", value: 3000, data: { source: "center", target: "rest" } })).toMatch(/75\s?%/);
    const amounts = buildSankeyOption(model, tokens) as { series: { label: { formatter: (p: unknown) => string } }[] };
    expect(amounts.series[0].label.formatter({ name: "out:Miete", value: 1000 })).not.toContain("%");
  });
});
