import { describe, expect, it } from "vitest";
import { buildChartOption, axisTooltipHtml } from "../features/dashboards/charts/chartOptions";
import { withMeasureColors } from "../features/dashboards/chartTransforms";
import { withoutChartOptions } from "../features/dashboards/formulaQueries";
import type { ChartModel } from "../features/dashboards/widgetData";

const tokens = {
  palette: ["#111", "#222", "#333"],
  text: "#000",
  muted: "#666",
  line: "#ddd",
  surface: "#fff",
  popover: "#fff",
  positive: "#0a0",
  negative: "#a00",
} as never;

describe("Second value axis (Step 4.3)", () => {
  const model: ChartModel = {
    categories: ["2026-01-01", "2026-02-01"],
    bucket: "month",
    format: "currency",
    series: [
      { name: "value", values: [1000, 1100] },
      { name: "return", values: [0.01, 0.1] },
    ],
  };

  it("puts a measure on the right axis in its own format", () => {
    const placed = withMeasureColors(model, [{ name: "value" }, { name: "return", axis: "right", format: "percent" }], false);
    expect(placed.series[1]).toMatchObject({ axis: "right", format: "percent" });
    const option = buildChartOption("line", placed, tokens, { stacked: true }) as {
      yAxis: { position?: string; axisLabel: { formatter: (v: number) => string } }[];
      series: { yAxisIndex: number; stack?: string }[];
    };
    expect(option.yAxis).toHaveLength(2);
    expect(option.yAxis[1].position).toBe("right");
    expect(option.yAxis[1].axisLabel.formatter(0.1)).toMatch(/10\s?%/);
    expect(option.series.map((s) => s.yAxisIndex)).toEqual([0, 1]);
    // A series on its own axis is not stacked with the others.
    expect(option.series[1].stack).toBeUndefined();
  });

  it("writes each series in its format in the tooltip, and leaves it out of the total", () => {
    const html = axisTooltipHtml(
      [
        { seriesIndex: 0, seriesName: "value", value: 1100, color: "#111", axisValue: "2026-02-01" },
        { seriesIndex: 1, seriesName: "return", value: 0.1, color: "#222", axisValue: "2026-02-01" },
      ] as never,
      {
        stacked: true,
        hovered: null,
        format: (v) => `${v} EUR`,
        formatOf: (i) => (i === 1 ? (v) => `${Number(v) * 100} pct` : undefined),
      },
    );
    expect(html).toContain("1100 EUR");
    expect(html).toContain("10 pct");
    expect(html).not.toContain("Total");
  });

  it("never sends axis or format to the API", () => {
    expect(withoutChartOptions({ source: "t", measures: [{ name: "r", formula: "TWR(1, 0)", axis: "right", format: "percent" }] }).measures).toEqual([
      { name: "r", formula: "TWR(1, 0)" },
    ]);
  });

  it("keeps one axis object when no measure is on the right", () => {
    const option = buildChartOption("line", model, tokens) as { yAxis: unknown };
    expect(Array.isArray(option.yAxis)).toBe(false);
  });
});

describe("Returns on a date axis", () => {
  it("leave the periods before their first value empty instead of 0", async () => {
    const { toChartModel } = await import("../features/dashboards/widgetData");
    const model = toChartModel(
      {
        data: [
          { month: "2026-06-01", depot: 100, benchmark: null },
          { month: "2026-07-01", depot: 102, benchmark: 100 },
        ],
        meta: {
          truncated: false,
          columns: [
            { name: "month", type: "date", role: "group" },
            { name: "depot", type: "float", role: "measure" },
            { name: "benchmark", type: "float", role: "measure" },
          ],
        },
      },
      {
        source: "accounts",
        groupBy: [{ column: "opened_on", bucket: "month", as: "month" }],
        measures: [
          { name: "depot", formula: "TWR(SUM(SUM(t.v)), 0, 100)" },
          { name: "benchmark", formula: "REBASE(LOOKUP_ASOF(\"p\", \"isin\", benchmark, \"date\", BUCKET_END(), \"price\"))" },
        ],
      },
      { from: "2026-06-01", to: "2026-08-01" } as never,
      undefined,
      new Date(Date.UTC(2026, 6, 15)),
    );
    expect(model.series.find((s) => s.name === "benchmark")?.values).toEqual([null, 100]);
  });
});
