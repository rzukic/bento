import type { EChartsCoreOption } from "echarts/core";
import type { QueryBucket } from "../../../api/query";
import type { WidgetType } from "../../../api/dashboards";
import { getCurrentLocale } from "../../data-explorer/components/editors/temporalEditing";
import { formatCompact, formatNumber } from "../../../lib/format";
import type { ChartModel } from "../widgetData";

/** Colors from the app's design tokens, read when a chart renders. */
export type ChartTokens = {
  text: string;
  muted: string;
  line: string;
  surface: string;
  popover: string;
  palette: string[];
};

const SERIES_COLORS = ["#14b8a6", "#f59e0b", "#a855f7", "#f43f5e", "#84cc16", "#06b6d4", "#fb923c", "#64748b"];

export function readChartTokens(): ChartTokens {
  const style = typeof document !== "undefined" ? getComputedStyle(document.documentElement) : null;
  const token = (name: string, fallback: string) => style?.getPropertyValue(name).trim() || fallback;
  return {
    text: token("--text-2", "#c8c8c8"),
    muted: token("--text-3", "#8a8a8a"),
    line: token("--line", "rgba(255,255,255,0.08)"),
    surface: token("--surface", "#141416"),
    popover: token("--popover", "#1c1c1f"),
    palette: [token("--accent", "#3b82f6"), ...SERIES_COLORS],
  };
}

/** "2026-09-01" → "Sep 26" (month), "Q3 26", "2026", "7 Sep" (week, day). */
export function formatBucketLabel(value: string, bucket: QueryBucket | undefined, locale = getCurrentLocale()): string {
  if (!bucket || !/^\d{4}-\d{2}-\d{2}$/.test(value)) return value;
  const [y, m, d] = value.split("-").map(Number);
  const date = new Date(Date.UTC(y, m - 1, d));
  switch (bucket) {
    case "year":
      return String(y);
    case "quarter":
      return `Q${Math.floor((m - 1) / 3) + 1} ${String(y).slice(2)}`;
    case "month":
      return new Intl.DateTimeFormat(locale, { month: "short", year: "2-digit", timeZone: "UTC" }).format(date);
    default:
      return new Intl.DateTimeFormat(locale, { day: "numeric", month: "short", timeZone: "UTC" }).format(date);
  }
}

export function buildChartOption(type: WidgetType, model: ChartModel, tokens: ChartTokens, stacked = false): EChartsCoreOption {
  const format = (value: unknown) => (typeof value === "number" ? formatNumber(value, model.format) : "—");
  const tooltip = {
    trigger: type === "donut" ? "item" : "axis",
    backgroundColor: tokens.popover,
    borderColor: tokens.line,
    textStyle: { color: tokens.text, fontSize: 12 },
    valueFormatter: format,
  };
  const legend = {
    show: model.series.length > 1 || type === "donut",
    bottom: 0,
    icon: "circle",
    itemWidth: 8,
    itemHeight: 8,
    textStyle: { color: tokens.muted, fontSize: 11 },
  };
  const base = { color: tokens.palette, animationDuration: 300, textStyle: { fontFamily: "inherit" } };

  if (type === "donut") {
    // Expenses are negative; a donut shows shares, so it uses amounts.
    const values = model.series[0]?.values ?? [];
    const data = model.categories
      .map((name, index) => ({ name, value: Math.abs(values[index] ?? 0) }))
      .filter((item) => item.value > 0);
    return {
      ...base,
      tooltip: { ...tooltip, valueFormatter: (value: unknown) => format(value) },
      legend: { ...legend, type: "scroll" },
      series: [
        {
          type: "pie",
          radius: ["52%", "78%"],
          center: ["50%", "45%"],
          avoidLabelOverlap: true,
          itemStyle: { borderColor: tokens.surface, borderWidth: 2 },
          label: { show: false },
          data,
        },
      ],
    };
  }

  const kind = type === "bar" ? "bar" : "line";
  return {
    ...base,
    tooltip,
    legend,
    grid: { left: 8, right: 12, top: 12, bottom: legend.show ? 32 : 8, containLabel: true },
    xAxis: {
      type: "category",
      data: model.categories,
      boundaryGap: kind === "bar",
      axisLine: { lineStyle: { color: tokens.line } },
      axisTick: { show: false },
      axisLabel: { color: tokens.muted, fontSize: 11, formatter: (value: string) => formatBucketLabel(value, model.bucket) },
    },
    yAxis: {
      type: "value",
      splitLine: { lineStyle: { color: tokens.line } },
      axisLabel: { color: tokens.muted, fontSize: 11, formatter: (value: number) => formatCompact(value, model.format) },
    },
    series: model.series.map((series) => ({
      name: series.name,
      type: kind,
      data: series.values,
      stack: stacked ? "total" : undefined,
      showSymbol: model.categories.length <= 16,
      symbolSize: 5,
      connectNulls: false,
      barMaxWidth: 28,
      lineStyle: kind === "line" ? { width: 2 } : undefined,
      areaStyle: type === "area" ? { opacity: 0.16 } : undefined,
      emphasis: { focus: "series" },
    })),
  };
}
