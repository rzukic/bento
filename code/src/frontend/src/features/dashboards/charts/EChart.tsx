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
    const releaseHold = holdToScrub(element);
    return () => {
      releaseHold();
      observer?.disconnect();
      instance.dispose();
      chart.current = null;
    };
  }, [choose]);

  // A chart that cannot be drawn says so instead of taking the page down.
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    try {
      const touch = typeof window.matchMedia === "function" && window.matchMedia(COARSE_POINTER).matches;
      chart.current?.setOption(touch ? withTouchTooltip(option) : option, { notMerge: true });
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

/** The tooltip box ECharts measures (its size and the chart's). */
type TooltipSize = { contentSize: [number, number]; viewSize: [number, number] };

/**
 * On a touch screen the finger is where the pointer would be: the tooltip
 * goes to the top of the chart on the side away from the finger, below the
 * "Show rows" button, so holding and sliding along the chart never lands on
 * it. It also lets every touch through, should it be in the way anyway.
 */
export function withTouchTooltip(option: EChartsCoreOption): EChartsCoreOption {
  const tooltip = (option as { tooltip?: unknown }).tooltip;
  if (!tooltip || typeof tooltip !== "object" || Array.isArray(tooltip)) return option;
  return {
    ...option,
    tooltip: {
      ...(tooltip as Record<string, unknown>),
      confine: true,
      enterable: false,
      transitionDuration: 0,
      extraCssText: `${String((tooltip as { extraCssText?: string }).extraCssText ?? "")}pointer-events:none;`,
      position: (point: [number, number], _params: unknown, _dom: unknown, _rect: unknown, size: TooltipSize) => touchTooltipPosition(point, size),
    },
  };
}

/** Top of the chart, on the half the finger is not on. */
export function touchTooltipPosition(point: [number, number], size: TooltipSize): [number, number] {
  const [width] = size.contentSize;
  const [viewWidth] = size.viewSize;
  const top = 36;
  const fingerOnLeft = point[0] < viewWidth / 2;
  const x = fingerOnLeft ? Math.max(4, viewWidth - width - 4) : 4;
  return [x, top];
}

/** How long a finger rests before sliding moves along the chart instead of scrolling the page. */
export const HOLD_MS = 200;
const HOLD_SLOP = 8;

/**
 * Hold and slide: a finger that rests on the chart for a moment, then moves,
 * moves the tooltip along it — the page does not scroll, even when the slide
 * starts a little up or down. A quick swipe still scrolls. Returns the cleanup.
 */
export function holdToScrub(element: HTMLElement): () => void {
  let start: { x: number; y: number; at: number } | null = null;
  let holding = false;
  const onStart = (event: TouchEvent) => {
    const touch = event.touches[0];
    start = event.touches.length === 1 && touch ? { x: touch.clientX, y: touch.clientY, at: Date.now() } : null;
    holding = false;
  };
  const onMove = (event: TouchEvent) => {
    const touch = event.touches[0];
    if (!start || !touch) return;
    if (!holding) {
      const moved = Math.hypot(touch.clientX - start.x, touch.clientY - start.y);
      // Moved before resting: a swipe, the page's.
      if (Date.now() - start.at < HOLD_MS) {
        if (moved > HOLD_SLOP) start = null;
        return;
      }
      holding = true;
    }
    if (event.cancelable) event.preventDefault();
  };
  const onEnd = () => {
    start = null;
    holding = false;
  };
  element.addEventListener("touchstart", onStart, { passive: true });
  element.addEventListener("touchmove", onMove, { passive: false });
  element.addEventListener("touchend", onEnd);
  element.addEventListener("touchcancel", onEnd);
  return () => {
    element.removeEventListener("touchstart", onStart);
    element.removeEventListener("touchmove", onMove);
    element.removeEventListener("touchend", onEnd);
    element.removeEventListener("touchcancel", onEnd);
  };
}
