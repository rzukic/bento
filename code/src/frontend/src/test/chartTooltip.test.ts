import { describe, expect, it } from "vitest";
import { axisTooltipHtml, buildChartOption, formatBucketTitle, orderTooltipRows, type ChartTokens } from "../features/dashboards/charts/chartOptions";

const row = (index: number, value: number | null) => ({ index, value });
const order = (rows: { index: number; value: number | null }[], stacked: boolean) => orderTooltipRows(rows, stacked).map((r) => r.index);

const params = (values: (number | null)[], names = ["Lebensmittel", "Gehalt", "Miete", "Freizeit"]) =>
  values.map((value, index) => ({ seriesIndex: index, seriesName: names[index], value, color: "#123456", axisValue: "2026-09-01" }));
const format = (value: unknown) => (typeof value === "number" ? value.toFixed(2) : "—");
const namesIn = (html: string) => [...html.matchAll(/<span>([^<]*)<\/span>/g)].map((m) => m[1]);

describe("chart tooltip", () => {
  it("lists series by value, as the points sit at that date", () => {
    expect(order([row(0, -171.85), row(1, 3250), row(2, -1150), row(3, -55.5)], false)).toEqual([1, 3, 0, 2]);
  });

  it("puts empty values last and keeps series order on ties", () => {
    expect(order([row(0, null), row(1, 5), row(2, 5), row(3, 9)], false)).toEqual([3, 1, 2, 0]);
  });

  it("follows the stacks when stacked: the positive stack from its top, then the negative one downwards", () => {
    // ECharts stacks each sign on its own: 0 and 2 go up (2 on top), 1 and 3 go down (1 next to zero).
    expect(order([row(0, 10), row(1, -4), row(2, 6), row(3, -8), row(4, null)], true)).toEqual([2, 0, 1, 3, 4]);
  });

  it("marks the hovered series and dims the others", () => {
    const html = axisTooltipHtml(params([-171.85, 3250, -1150, -55.5]), { stacked: false, hovered: 1, format });
    expect(namesIn(html)).toEqual(["Gehalt", "Freizeit", "Lebensmittel", "Miete"]);
    expect(html).toMatch(/background:var\(--hover\);font-weight:600;"><span[^>]*><\/span><span>Gehalt/);
    expect(html.match(/opacity:\.62/g)).toHaveLength(3);
  });

  it("shows the total of a stack (the net with mixed signs), not of separate lines", () => {
    const stacked = axisTooltipHtml(params([-171.85, 3250, -1150, -55.5]), { stacked: true, hovered: null, format });
    expect(stacked).toContain("<span>Total</span>");
    expect(stacked).toContain("1872.65");
    expect(axisTooltipHtml(params([-171.85, 3250, -1150, -55.5]), { stacked: false, hovered: null, format })).not.toContain("Total");
  });

  it("escapes series names, which are data", () => {
    const html = axisTooltipHtml(params([1, 2], ['<img src=x onerror="alert(1)">', "A & B"]), { stacked: false, hovered: null, format });
    expect(html).not.toContain("<img");
    expect(html).toContain("&#60;img src=x onerror=&#34;alert(1)&#34;&#62;");
    expect(html).toContain("A &#38; B");
  });

  it("titles the period in full", () => {
    expect(formatBucketTitle("2026-09-01", "month", "en-GB")).toBe("September 2026");
    expect(formatBucketTitle("2026-07-01", "quarter", "en-GB")).toBe("Q3 2026");
    expect(formatBucketTitle("2026-09-07", "day", "en-GB")).toBe("7 Sept 2026");
    expect(formatBucketTitle("Groceries", undefined)).toBe("Groceries");
  });
});

describe("chart legend", () => {
  const tokens: ChartTokens = { text: "#eee", muted: "#999", line: "#333", surface: "#111", popover: "#222", palette: ["#3b82f6"], positive: "#4ade80", negative: "#f87171" };
  const model = {
    categories: ["2026-08-01", "2026-09-01"],
    bucket: "month" as const,
    format: "number" as const,
    series: ["Acme Corp", "Globex GmbH", "Initech", "Umbrella Ltd", "Stark Industries", "A very long customer name GmbH & Co. KG"].map((name) => ({ name, values: [1, 2] })),
  };

  it("stays on one line that pages, with long names cut and shown in full on hover", () => {
    for (const type of ["line", "bar", "donut"] as const) {
      const legend = (buildChartOption(type, model as never, tokens) as { legend: Record<string, unknown> }).legend;
      expect(legend).toMatchObject({ show: true, type: "scroll", textStyle: { width: 120, overflow: "truncate" }, tooltip: { show: true } });
      const formatter = (legend.tooltip as { formatter: (p: { name: string }) => string }).formatter;
      expect(formatter({ name: "A & <b>B</b>" })).toBe("A &#38; &#60;b&#62;B&#60;/b&#62;");
    }
  });
});
