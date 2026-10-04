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
  /** Colour by sign: values from 0 up, and below 0. */
  positive: string;
  negative: string;
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
    positive: token("--success", "#4ade80"),
    negative: token("--danger", "#f87171"),
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

/** The tooltip's heading: "September 2026", "Q3 2026", "7 Sept 2026". */
export function formatBucketTitle(value: string, bucket: QueryBucket | undefined, locale = getCurrentLocale()): string {
  if (!bucket || !/^\d{4}-\d{2}-\d{2}$/.test(value)) return value;
  const [y, m, d] = value.split("-").map(Number);
  const date = new Date(Date.UTC(y, m - 1, d));
  switch (bucket) {
    case "year":
      return String(y);
    case "quarter":
      return `Q${Math.floor((m - 1) / 3) + 1} ${y}`;
    case "month":
      return new Intl.DateTimeFormat(locale, { month: "long", year: "numeric", timeZone: "UTC" }).format(date);
    default:
      return new Intl.DateTimeFormat(locale, { day: "numeric", month: "short", year: "numeric", timeZone: "UTC" }).format(date);
  }
}

/**
 * Tooltip rows in the order the points sit at that x, top to bottom: by
 * value, or — stacked — the positive stack from its top, then the negative
 * one downwards (ECharts stacks each sign on its own). Empty values last.
 */
export function orderTooltipRows<T extends { index: number; value: number | null }>(rows: T[], stacked: boolean): T[] {
  const known = rows.filter((row) => row.value !== null);
  const empty = rows.filter((row) => row.value === null);
  const value = (row: T) => row.value ?? 0;
  if (!stacked) return [...known.sort((a, b) => value(b) - value(a) || a.index - b.index), ...empty];
  const positive = known.filter((row) => value(row) > 0).sort((a, b) => b.index - a.index);
  const zero = known.filter((row) => value(row) === 0).sort((a, b) => a.index - b.index);
  const negative = known.filter((row) => value(row) < 0).sort((a, b) => a.index - b.index);
  return [...positive, ...zero, ...negative, ...empty];
}

type AxisTooltipParam = { seriesIndex: number; seriesName: string; value: unknown; color: unknown; axisValue: unknown };
type SeriesKind = "shifted" | "total" | undefined;

const escapeHtml = (text: string) => text.replace(/[&<>"']/g, (c) => `&#${c.charCodeAt(0)};`);

/**
 * The axis tooltip as HTML: the period, one row per series in chart order
 * (the hovered series marked), and for stacks the total. Series names are
 * data, so everything is escaped.
 */
export function axisTooltipHtml(
  params: AxisTooltipParam[],
  {
    bucket,
    stacked,
    hovered,
    format,
    kindOf = () => undefined,
  }: { bucket?: QueryBucket; stacked: boolean; hovered: number | null; format: (value: unknown) => string; kindOf?: (seriesIndex: number) => SeriesKind },
): string {
  if (params.length === 0) return "";
  const all = params.map((p) => ({
    index: p.seriesIndex,
    name: p.seriesName,
    value: typeof p.value === "number" && Number.isFinite(p.value) ? p.value : null,
    color: typeof p.color === "string" ? p.color : "currentColor",
    kind: kindOf(p.seriesIndex),
  }));
  // The stack (or the plain series) in chart order, then the earlier period;
  // a total line is the Total row below.
  const main = all.filter((row) => !row.kind);
  const rows = [...orderTooltipRows(main, stacked), ...orderTooltipRows(all.filter((row) => row.kind === "shifted"), false)];
  const active = rows.some((row) => row.index === hovered) ? hovered : null;
  const line = (content: string, style = "") =>
    `<div style="display:flex;align-items:center;gap:8px;padding:2px 6px;margin:0 -6px;border-radius:6px;${style}">${content}</div>`;
  const amount = (value: number | null) => `<span style="margin-left:auto;padding-left:16px;font-weight:600;font-variant-numeric:tabular-nums">${escapeHtml(format(value))}</span>`;
  const body = rows
    .map((row) => {
      const isActive = row.index === active;
      const dot = `<span style="width:8px;height:8px;border-radius:50%;flex:none;background:${escapeHtml(row.color)}"></span>`;
      const style = isActive ? "background:var(--hover);font-weight:600;" : active !== null ? "opacity:.62;" : "";
      return line(`${dot}<span>${escapeHtml(row.name)}</span>${amount(row.value)}`, style);
    })
    .join("");
  const values = main.map((row) => row.value).filter((value): value is number => value !== null);
  const total =
    stacked && values.length > 1
      ? `<div style="margin-top:6px;padding-top:6px;border-top:1px solid var(--line)">${line(`<span style="width:8px;flex:none"></span><span>Total</span>${amount(values.reduce((sum, value) => sum + value, 0))}`)}</div>`
      : "";
  const title = `<div style="margin-bottom:6px;color:var(--text-3)">${escapeHtml(formatBucketTitle(String(params[0].axisValue ?? ""), bucket))}</div>`;
  return `<div style="min-width:180px">${title}${body}${total}</div>`;
}

const SIGN_LIMIT = 1e15;

export type ChartOptions = {
  stacked?: boolean;
  /** The series under the pointer, read when the tooltip renders. */
  hovered?: () => number | null;
  /** Horizontal lines with their numbers. */
  referenceLines?: { value: number; label: string }[];
  /** One series: values below 0 in the danger colour, the rest in the success colour. */
  colorBySign?: boolean;
};

export function buildChartOption(type: WidgetType, model: ChartModel, tokens: ChartTokens, options: ChartOptions = {}): EChartsCoreOption {
  const { stacked = false, hovered = () => null, referenceLines = [], colorBySign = false } = options;
  const format = (value: unknown) => (typeof value === "number" ? formatNumber(value, model.format) : "—");
  const tooltip = {
    trigger: type === "donut" ? "item" : "axis",
    // Stays inside the chart, so the period on top is never cut off.
    confine: true,
    backgroundColor: tokens.popover,
    borderColor: tokens.line,
    textStyle: { color: tokens.text, fontSize: 12 },
    valueFormatter: format,
  };
  // One line that pages (‹ 1/2 ›) instead of wrapping into the axis; long
  // names are cut and shown in full on hover.
  const legend = {
    show: model.series.length > 1 || type === "donut",
    type: "scroll",
    bottom: 0,
    icon: "circle",
    itemWidth: 8,
    itemHeight: 8,
    textStyle: { color: tokens.muted, fontSize: 11, width: 120, overflow: "truncate" },
    pageIconColor: tokens.text,
    pageIconInactiveColor: tokens.line,
    pageIconSize: 9,
    pageTextStyle: { color: tokens.muted, fontSize: 11 },
    tooltip: { show: true, formatter: (params: { name?: string }) => escapeHtml(params.name ?? "") },
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
      legend,
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
  const kindOf = (index: number) => model.series[index]?.kind;
  // An earlier period takes the colour of the series it repeats.
  const main = model.series.filter((series) => !series.kind);
  const colorOf = (series: ChartModel["series"][number]) => {
    if (series.kind === "total") return tokens.text;
    const base = series.kind === "shifted" ? main.findIndex((m) => series.name.startsWith(`${m.name} (`)) : main.indexOf(series);
    return tokens.palette[Math.max(0, base) % tokens.palette.length];
  };
  const bySign = colorBySign && main.length === 1 && !stacked;
  const references = referenceLines.map((line) => line.value);
  const markLine =
    referenceLines.length > 0
      ? {
          silent: true,
          symbol: "none",
          lineStyle: { color: tokens.muted, type: "dashed", width: 1 },
          label: {
            color: tokens.muted,
            fontSize: 11,
            position: "insideEndTop",
            formatter: (p: { name?: string; value?: unknown }) => [p.name, format(p.value)].filter(Boolean).join(" · "),
          },
          data: referenceLines.map((line) => ({ yAxis: line.value, name: line.label })),
        }
      : undefined;
  return {
    ...base,
    tooltip: {
      ...tooltip,
      formatter: (params: unknown) =>
        axisTooltipHtml((Array.isArray(params) ? params : [params]) as AxisTooltipParam[], { bucket: model.bucket, stacked, hovered: hovered(), format, kindOf }),
    },
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
      // Reference lines stay in view even above or below every value.
      ...(references.length > 0
        ? {
            min: (extent: { min: number }) => Math.min(extent.min, ...references),
            max: (extent: { max: number }) => Math.max(extent.max, ...references),
          }
        : {}),
    },
    // Line and area by sign: the colour changes where the line crosses 0.
    ...(bySign && kind === "line"
      ? {
          visualMap: {
            show: false,
            seriesIndex: model.series.indexOf(main[0]),
            dimension: 1,
            // Open-ended pieces crash ECharts' line gradient (no colour stops), so the ends are finite.
            pieces: [
              { gte: -SIGN_LIMIT, lt: 0, color: tokens.negative },
              { gte: 0, lte: SIGN_LIMIT, color: tokens.positive },
            ],
          },
        }
      : {}),
    series: model.series.map((series, index) => {
      const color = colorOf(series);
      if (series.kind === "total") {
        return {
          name: series.name,
          type: "line",
          data: series.values,
          color,
          z: 5,
          symbolSize: 5,
          showSymbol: model.categories.length <= 16,
          lineStyle: { width: 2 },
          emphasis: { focus: "series" },
          triggerLineEvent: true,
        };
      }
      const shifted = series.kind === "shifted";
      return {
        name: series.name,
        type: kind,
        data: series.values,
        color,
        stack: stacked && !shifted ? "total" : undefined,
        showSymbol: !shifted && model.categories.length <= 16,
        symbolSize: 5,
        connectNulls: false,
        barMaxWidth: 28,
        lineStyle: kind === "line" ? { width: shifted ? 1.5 : 2, type: shifted ? "dashed" : "solid" } : undefined,
        areaStyle: type === "area" && !shifted ? { opacity: 0.16 } : undefined,
        itemStyle: shifted
          ? { opacity: kind === "bar" ? 0.4 : 0.8 }
          : bySign && kind === "bar"
            ? { color: (p: { value?: unknown }) => (typeof p.value === "number" && p.value < 0 ? tokens.negative : tokens.positive) }
            : undefined,
        markLine: index === 0 ? markLine : undefined,
        emphasis: { focus: "series" },
        // Hovering a line or an area (not only its points) marks the series in the tooltip.
        triggerLineEvent: kind === "line",
      };
    }),
  };
}
