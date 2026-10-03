import { useEffect, useRef } from "react";
import { BarChart, LineChart, PieChart } from "echarts/charts";
import { GridComponent, LegendComponent, TooltipComponent } from "echarts/components";
import * as echarts from "echarts/core";
import { SVGRenderer } from "echarts/renderers";
import type { EChartsCoreOption } from "echarts/core";

// Only what the widgets use; the rest of ECharts stays out of the bundle.
echarts.use([LineChart, BarChart, PieChart, GridComponent, TooltipComponent, LegendComponent, SVGRenderer]);

/** One ECharts instance on a div that follows its container's size. */
export function EChart({ option, label }: { option: EChartsCoreOption; label: string }) {
  const ref = useRef<HTMLDivElement | null>(null);
  const chart = useRef<echarts.ECharts | null>(null);

  useEffect(() => {
    const element = ref.current;
    if (!element) return;
    const instance = echarts.init(element, null, { renderer: "svg" });
    chart.current = instance;
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
