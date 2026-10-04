import { useEffect, useRef } from "react";
import { BarChart, LineChart, PieChart } from "echarts/charts";
import { GridComponent, LegendComponent, TooltipComponent } from "echarts/components";
import * as echarts from "echarts/core";
import { SVGRenderer } from "echarts/renderers";
import type { EChartsCoreOption } from "echarts/core";

// Only what the widgets use; the rest of ECharts stays out of the bundle.
echarts.use([LineChart, BarChart, PieChart, GridComponent, TooltipComponent, LegendComponent, SVGRenderer]);

/**
 * One ECharts instance on a div that follows its container's size.
 * `onHoverSeries` reports the series under the pointer (null when none).
 */
export function EChart({ option, label, onHoverSeries }: { option: EChartsCoreOption; label: string; onHoverSeries?: (index: number | null) => void }) {
  const ref = useRef<HTMLDivElement | null>(null);
  const chart = useRef<echarts.ECharts | null>(null);
  const hover = useRef(onHoverSeries);

  useEffect(() => {
    hover.current = onHoverSeries;
  }, [onHoverSeries]);

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

  useEffect(() => {
    chart.current?.setOption(option, { notMerge: true });
  }, [option]);

  return <div ref={ref} className="widget-chart" role="img" aria-label={label} />;
}
