import { useEffect, useRef, useState } from "react";
import { BarChart, LineChart, PieChart, SankeyChart } from "echarts/charts";
import { GridComponent, LegendComponent, MarkAreaComponent, MarkLineComponent, TooltipComponent, VisualMapComponent } from "echarts/components";
import * as echarts from "echarts/core";
import { SVGRenderer } from "echarts/renderers";
import type { EChartsCoreOption } from "echarts/core";

// Only what the widgets use; the rest of ECharts stays out of the bundle.
echarts.use([LineChart, BarChart, PieChart, SankeyChart, GridComponent, TooltipComponent, LegendComponent, MarkLineComponent, MarkAreaComponent, VisualMapComponent, SVGRenderer]);

/**
 * One ECharts instance on a div that follows its container's size.
 * `onHoverSeries` reports the series under the pointer (null when none).
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
}: {
  option: EChartsCoreOption;
  label: string;
  onHoverSeries?: (index: number | null) => void;
  /** A click on a bar, point, slice, node or flow (drill-down). */
  onItemClick?: (click: ChartClick) => void;
}) {
  const ref = useRef<HTMLDivElement | null>(null);
  const chart = useRef<echarts.ECharts | null>(null);
  const hover = useRef(onHoverSeries);
  const click = useRef(onItemClick);

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
    instance.on("click", (event) => click.current?.(event as ChartClick));
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
  }, []);

  // A chart that cannot be drawn says so instead of taking the page down.
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    try {
      chart.current?.setOption(option, { notMerge: true });
      setFailed(false);
    } catch (error) {
      console.error("Chart could not be drawn", error);
      chart.current?.clear();
      setFailed(true);
    }
  }, [option]);

  return (
    <div className="widget-chart-frame">
      <div ref={ref} className="widget-chart" role="img" aria-label={label} hidden={failed} />
      {failed ? (
        <p className="widget-note widget-note--error" role="alert">
          This chart could not be drawn with these settings.
        </p>
      ) : null}
    </div>
  );
}
