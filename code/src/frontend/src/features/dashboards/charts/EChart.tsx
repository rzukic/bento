import { useCallback, useEffect, useRef, useState } from "react";
import { BarChart, LineChart, PieChart, SankeyChart } from "echarts/charts";
import { GridComponent, LegendComponent, MarkAreaComponent, MarkLineComponent, TitleComponent, TooltipComponent, VisualMapComponent } from "echarts/components";
import * as echarts from "echarts/core";
import { SVGRenderer } from "echarts/renderers";
import type { EChartsCoreOption } from "echarts/core";
import { COARSE_POINTER } from "../../../hooks/useMediaQuery";

// Only what the widgets use; the rest of ECharts stays out of the bundle.
echarts.use([LineChart, BarChart, PieChart, SankeyChart, GridComponent, TooltipComponent, LegendComponent, MarkLineComponent, MarkAreaComponent, TitleComponent, VisualMapComponent, SVGRenderer]);

/**
 * One ECharts instance on a div that follows its container's size.
 * `onHoverSeries` reports the series under the pointer (null when none).
 * On a touch screen a tap shows the values there, like hovering; opening the
 * rows behind a bar or point (drill-down) takes a second tap on it or the
 * "Show rows" button the first tap brings up.
 */
export type ChartClick = {
  componentType?: string;
  seriesIndex?: number;
  dataIndex?: number;
  name?: string;
  /** Sankey: "node" or "edge". */
  dataType?: string;
  data?: unknown;
};

export function EChart({
  option,
  label,
  onHoverSeries,
  onItemClick,
  describeItem,
}: {
  option: EChartsCoreOption;
  label: string;
  onHoverSeries?: (index: number | null) => void;
  /** A click on a bar, point, slice, node or flow (drill-down). */
  onItemClick?: (click: ChartClick) => void;
  /** Touch: what the "Show rows" button names, e.g. "August 2026" for 2026-08-01. */
  describeItem?: (click: ChartClick) => string;
}) {
  const ref = useRef<HTMLDivElement | null>(null);
  const chart = useRef<echarts.ECharts | null>(null);
  const hover = useRef(onHoverSeries);
  const click = useRef(onItemClick);
  const [pending, setPending] = useState<ChartClick | null>(null);
  const pendingRef = useRef<ChartClick | null>(null);
  const choose = useCallback((next: ChartClick | null) => {
    pendingRef.current = next;
    setPending(next);
  }, []);

  useEffect(() => {
    hover.current = onHoverSeries;
    click.current = onItemClick;
  }, [onHoverSeries, onItemClick]);

  useEffect(() => {
    const element = ref.current;
    if (!element) return;
    const instance = echarts.init(element, null, { renderer: "svg" });
    chart.current = instance;
    // The tooltip renders on pointer moves; when the hovered series changes
    // it is shown again at the pointer, so it marks the new series at once.
    let pointer: [number, number] | null = null;
    instance.getZr().on("mousemove", (event) => {
      pointer = [event.offsetX, event.offsetY];
    });
    const report = (index: number | null) => {
      hover.current?.(index);
      if (pointer) instance.dispatchAction({ type: "showTip", x: pointer[0], y: pointer[1] });
    };
    instance.on("mouseover", (event) => {
      if (event.componentType === "series") report(event.seriesIndex ?? null);
    });
    instance.on("mouseout", () => report(null));
    instance.on("click", (event) => {
      const target = event as ChartClick;
      if (!click.current) return;
      const touch = typeof window.matchMedia === "function" && window.matchMedia(COARSE_POINTER).matches;
      if (!touch) {
        click.current(target);
        return;
      }
      // First tap: the values (ECharts shows the tooltip); the same item again: its rows.
      const previous = pendingRef.current;
      if (previous && previous.seriesIndex === target.seriesIndex && previous.dataIndex === target.dataIndex && previous.dataType === target.dataType) {
        choose(null);
        click.current(target);
        return;
      }
      choose({ componentType: target.componentType, seriesIndex: target.seriesIndex, dataIndex: target.dataIndex, name: target.name, dataType: target.dataType, data: target.data });
    });
    // A tap on empty space puts the button away.
    instance.getZr().on("click", (event) => {
      if (!event.target) choose(null);
    });
    instance.on("globalout", () => {
      pointer = null;
      hover.current?.(null);
    });
    const observer = typeof ResizeObserver !== "undefined" ? new ResizeObserver(() => instance.resize()) : null;
    observer?.observe(element);
    return () => {
      observer?.disconnect();
      instance.dispose();
      chart.current = null;
    };
  }, [choose]);

  // A chart that cannot be drawn says so instead of taking the page down.
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    try {
      chart.current?.setOption(option, { notMerge: true });
      setFailed(false);
      choose(null);
    } catch (error) {
      console.error("Chart could not be drawn", error);
      chart.current?.clear();
      setFailed(true);
    }
  }, [option, choose]);

  return (
    <div className="widget-chart-frame">
      <div ref={ref} className="widget-chart" role="img" aria-label={label} hidden={failed} />
      {pending && onItemClick && !failed ? (
        <button
          type="button"
          className="widget-chart-rows"
          onClick={() => {
            const target = pending;
            choose(null);
            onItemClick(target);
          }}
        >
          Show rows{describeItem ? `: ${describeItem(pending)}` : pending.name ? `: ${pending.name}` : ""} →
        </button>
      ) : null}
      {failed ? (
        <p className="widget-note widget-note--error" role="alert">
          This chart could not be drawn with these settings.
        </p>
      ) : null}
    </div>
  );
}
