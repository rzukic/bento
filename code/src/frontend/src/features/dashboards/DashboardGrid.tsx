import { useLayoutEffect, useRef, useState, type PointerEvent as ReactPointerEvent, type ReactNode } from "react";
import { GRID_COLUMNS, layoutHeight, moveItem, resizeItem, type LayoutItem } from "./layout";

const ROW_HEIGHT = 76;
const GAP = 16;
const NARROW = 720;
/** On a phone: up to a third of the grid and two rows is a small tile, two to a row. */
const isSmall = (item: LayoutItem) => item.w <= 4 && item.h <= 2;
const SMALL_HEIGHT = 112;
const MIN_STACKED = 220;
const MAX_STACKED = 360;

type Gesture = {
  id: string;
  kind: "move" | "resize";
  startX: number;
  startY: number;
  origin: LayoutItem;
  layout: LayoutItem[];
};

/**
 * Widgets on a 12-column grid. In edit mode a widget moves by its header
 * (`[data-drag-handle]`) and resizes by its corner (`[data-resize-handle]`);
 * the others make room while it moves. Narrow screens (phones) put them in
 * two columns: small ones such as key figures side by side and compact,
 * charts and tables across, no taller than a phone screen shows comfortably.
 */
export function DashboardGrid({
  layout,
  editing,
  onLayoutChange,
  renderItem,
}: {
  layout: LayoutItem[];
  editing: boolean;
  onLayoutChange: (layout: LayoutItem[]) => void;
  renderItem: (id: string) => ReactNode;
}) {
  const container = useRef<HTMLDivElement | null>(null);
  const [width, setWidth] = useState(0);
  const [preview, setPreview] = useState<LayoutItem[] | null>(null);
  const [ghost, setGhost] = useState<{ id: string; left: number; top: number; width: number; height: number } | null>(null);
  const gesture = useRef<Gesture | null>(null);

  // Measured before the first paint, so the grid does not flash stacked.
  useLayoutEffect(() => {
    const element = container.current;
    if (!element) return;
    const measure = () => setWidth(element.clientWidth);
    measure();
    if (typeof ResizeObserver === "undefined") return;
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  // Unmeasured (first render, tests) counts as narrow: widgets stack.
  const narrow = width < NARROW;
  const columnWidth = width > 0 ? (width - GAP * (GRID_COLUMNS - 1)) / GRID_COLUMNS : 0;
  const stepX = columnWidth + GAP;
  const stepY = ROW_HEIGHT + GAP;
  const items = preview ?? layout;

  const box = (item: LayoutItem) =>
    narrow
      ? null
      : {
          left: item.x * stepX,
          top: item.y * stepY,
          width: item.w * columnWidth + (item.w - 1) * GAP,
          height: item.h * ROW_HEIGHT + (item.h - 1) * GAP,
        };

  const onPointerDown = (event: ReactPointerEvent<HTMLDivElement>, item: LayoutItem) => {
    if (!editing || narrow || event.button !== 0) return;
    const target = event.target as HTMLElement;
    const kind = target.closest("[data-resize-handle]") ? "resize" : target.closest("[data-drag-handle]") ? "move" : null;
    // Buttons in the header (configure, delete) stay clickable.
    if (!kind || target.closest("button, a, input, select, textarea")) return;
    event.preventDefault();
    (event.currentTarget as HTMLElement).setPointerCapture?.(event.pointerId);
    gesture.current = { id: item.id, kind, startX: event.clientX, startY: event.clientY, origin: item, layout };
  };

  const onPointerMove = (event: ReactPointerEvent<HTMLDivElement>) => {
    const current = gesture.current;
    if (!current) return;
    const dx = event.clientX - current.startX;
    const dy = event.clientY - current.startY;
    const origin = box(current.origin)!;
    if (current.kind === "move") {
      const x = Math.round(current.origin.x + dx / stepX);
      const y = Math.max(0, Math.round(current.origin.y + dy / stepY));
      setPreview(moveItem(current.layout, current.id, x, y));
      setGhost({ id: current.id, left: origin.left + dx, top: Math.max(0, origin.top + dy), width: origin.width, height: origin.height });
    } else {
      const w = Math.max(1, Math.round(current.origin.w + dx / stepX));
      const h = Math.max(1, Math.round(current.origin.h + dy / stepY));
      setPreview(resizeItem(current.layout, current.id, w, h));
      setGhost({ id: current.id, left: origin.left, top: origin.top, width: Math.max(columnWidth, origin.width + dx), height: Math.max(ROW_HEIGHT, origin.height + dy) });
    }
  };

  const finish = () => {
    if (!gesture.current) return;
    gesture.current = null;
    if (preview) onLayoutChange(preview);
    setPreview(null);
    setGhost(null);
  };

  const height = layoutHeight(items);
  const ordered = narrow ? [...items].sort((a, b) => a.y - b.y || a.x - b.x) : items;

  return (
    <div
      ref={container}
      className={["dashboard-grid", editing ? "dashboard-grid--editing" : "", narrow ? "dashboard-grid--stacked" : ""].filter(Boolean).join(" ")}
      style={narrow ? undefined : { height: Math.max(height * stepY - GAP, ROW_HEIGHT) }}
    >
      {editing && !narrow && width > 0 ? (
        <div className="dashboard-grid__guides" aria-hidden="true" style={{ backgroundSize: `${stepX}px ${stepY}px` }} />
      ) : null}
      {ordered.map((item) => {
        const placed = box(item);
        const dragging = ghost?.id === item.id;
        return (
          <div key={item.id} className={narrow && isSmall(item) ? "dashboard-grid__slot dashboard-grid__slot--half" : "dashboard-grid__slot"}>
            {dragging && placed ? <div className="dashboard-grid__drop" style={placed} aria-hidden="true" /> : null}
            <div
              className={dragging ? "dashboard-grid__item dashboard-grid__item--dragging" : "dashboard-grid__item"}
              style={dragging && ghost ? ghost : placed ?? { height: stackedHeight(item) }}
              onPointerDown={(event) => onPointerDown(event, item)}
              onPointerMove={onPointerMove}
              onPointerUp={finish}
              onPointerCancel={finish}
            >
              {renderItem(item.id)}
            </div>
          </div>
        );
      })}
    </div>
  );
}

/** A widget's height when the grid stacks (phones). */
function stackedHeight(item: LayoutItem): number {
  if (isSmall(item)) return SMALL_HEIGHT;
  return Math.min(MAX_STACKED, Math.max(MIN_STACKED, item.h * ROW_HEIGHT + (item.h - 1) * GAP));
}
