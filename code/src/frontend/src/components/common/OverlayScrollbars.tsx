import { useEffect, useRef, useState, type PointerEvent as ReactPointerEvent } from "react";
import { createPortal } from "react-dom";
import "./OverlayScrollbars.css";

/** How long a bar stays after the last scroll or after the pointer left. */
const LINGER_MS = 900;
const INSET = 2;
const MIN_THUMB = 28;

type Axis = "y" | "x";

const ids = new WeakMap<HTMLElement, number>();
let nextId = 1;
function idFor(element: HTMLElement): number {
  let id = ids.get(element);
  if (id === undefined) {
    id = nextId;
    nextId += 1;
    ids.set(element, id);
  }
  return id;
}

type Bar = {
  key: string;
  axis: Axis;
  element: HTMLElement;
  top: number;
  left: number;
  length: number;
  visible: boolean;
};

type Tracked = {
  /** Last scroll of the element (ms). */
  scrolledAt: number;
  /** Pointer is over the element. */
  hovered: boolean;
  leftAt: number;
};

function canScroll(element: HTMLElement, axis: Axis): boolean {
  const style = getComputedStyle(element);
  const overflow = axis === "y" ? style.overflowY : style.overflowX;
  const isRoot = element === document.scrollingElement;
  if (!isRoot && overflow !== "auto" && overflow !== "scroll") return false;
  return axis === "y"
    ? element.scrollHeight > element.clientHeight + 1
    : element.scrollWidth > element.clientWidth + 1;
}

/** The innermost element under `node` that can scroll on either axis. */
function scrollableFrom(node: EventTarget | null): HTMLElement | null {
  let element = node instanceof Element ? node : null;
  while (element && element !== document.documentElement) {
    if (element instanceof HTMLElement && (canScroll(element, "y") || canScroll(element, "x"))) {
      return element;
    }
    element = element.parentElement;
  }
  const root = document.scrollingElement;
  return root instanceof HTMLElement && (canScroll(root, "y") || canScroll(root, "x")) ? root : null;
}

/**
 * Floating scrollbars for the whole app. Native scrollbars are hidden in CSS
 * (they took up room inside every scrolling box); this draws a thin bar over
 * the content of whatever is being scrolled or hovered, fading out shortly
 * after, like macOS overlay scrollbars. Bars can be dragged; wheel, trackpad
 * and keyboard scrolling are untouched. Works for every scroll container,
 * present and future, without opting in.
 */
export function OverlayScrollbars() {
  const tracked = useRef(new Map<HTMLElement, Tracked>());
  const [bars, setBars] = useState<Bar[]>([]);
  const frame = useRef(0);
  const dragging = useRef<{ element: HTMLElement; axis: Axis } | null>(null);

  useEffect(() => {
    if (typeof window === "undefined") return;

    const measure = () => {
      frame.current = 0;
      const now = performance.now();
      const next: Bar[] = [];
      for (const [element, state] of tracked.current) {
        const alive =
          element.isConnected &&
          (state.hovered ||
            dragging.current?.element === element ||
            now - state.scrolledAt < LINGER_MS ||
            now - state.leftAt < LINGER_MS);
        if (!alive) {
          tracked.current.delete(element);
          continue;
        }
        const isRoot = element === document.scrollingElement;
        const rect = isRoot
          ? { top: 0, left: 0, right: window.innerWidth, bottom: window.innerHeight }
          : element.getBoundingClientRect();
        const innerTop = rect.top + (isRoot ? 0 : element.clientTop);
        const innerLeft = rect.left + (isRoot ? 0 : element.clientLeft);
        const visible =
          state.hovered || dragging.current?.element === element || now - state.scrolledAt < LINGER_MS;

        if (canScroll(element, "y")) {
          const track = element.clientHeight - INSET * 2;
          const length = Math.max(MIN_THUMB, (element.clientHeight / element.scrollHeight) * track);
          const max = element.scrollHeight - element.clientHeight;
          const offset = max > 0 ? (element.scrollTop / max) * (track - length) : 0;
          next.push({
            key: `${idFor(element)}-y`,
            axis: "y",
            element,
            top: innerTop + INSET + offset,
            left: innerLeft + element.clientWidth - INSET,
            length,
            visible,
          });
        }
        if (canScroll(element, "x")) {
          const track = element.clientWidth - INSET * 2;
          const length = Math.max(MIN_THUMB, (element.clientWidth / element.scrollWidth) * track);
          const max = element.scrollWidth - element.clientWidth;
          const offset = max > 0 ? (element.scrollLeft / max) * (track - length) : 0;
          next.push({
            key: `${idFor(element)}-x`,
            axis: "x",
            element,
            top: innerTop + element.clientHeight - INSET,
            left: innerLeft + INSET + offset,
            length,
            visible,
          });
        }
      }
      setBars(next);
      if (tracked.current.size > 0) frame.current = requestAnimationFrame(measure);
    };
    const schedule = () => {
      if (!frame.current) frame.current = requestAnimationFrame(measure);
    };

    const onScroll = (event: Event) => {
      const target = event.target === document ? document.scrollingElement : event.target;
      if (!(target instanceof HTMLElement)) return;
      const state = tracked.current.get(target) ?? { scrolledAt: 0, hovered: false, leftAt: 0 };
      state.scrolledAt = performance.now();
      tracked.current.set(target, state);
      schedule();
    };

    let hoveredElement: HTMLElement | null = null;
    const onPointerMove = (event: PointerEvent) => {
      if (event.pointerType === "touch" || dragging.current) return;
      const element = scrollableFrom(event.target);
      if (element === hoveredElement) return;
      if (hoveredElement) {
        const previous = tracked.current.get(hoveredElement);
        if (previous) {
          previous.hovered = false;
          previous.leftAt = performance.now();
        }
      }
      hoveredElement = element;
      if (element) {
        const state = tracked.current.get(element) ?? { scrolledAt: 0, hovered: false, leftAt: 0 };
        state.hovered = true;
        tracked.current.set(element, state);
      }
      schedule();
    };

    document.addEventListener("scroll", onScroll, { capture: true, passive: true });
    document.addEventListener("pointermove", onPointerMove, { passive: true });
    return () => {
      document.removeEventListener("scroll", onScroll, { capture: true });
      document.removeEventListener("pointermove", onPointerMove);
      cancelAnimationFrame(frame.current);
    };
  }, []);

  function startDrag(event: ReactPointerEvent<HTMLDivElement>, bar: Bar) {
    event.preventDefault();
    event.stopPropagation();
    const { element, axis } = bar;
    const thumb = event.currentTarget;
    thumb.setPointerCapture(event.pointerId);
    dragging.current = { element, axis };
    const start = axis === "y" ? event.clientY : event.clientX;
    const startScroll = axis === "y" ? element.scrollTop : element.scrollLeft;
    const client = axis === "y" ? element.clientHeight : element.clientWidth;
    const content = axis === "y" ? element.scrollHeight : element.scrollWidth;
    const track = client - INSET * 2;
    const ratio = (content - client) / Math.max(1, track - bar.length);

    const onMove = (moveEvent: PointerEvent) => {
      const delta = (axis === "y" ? moveEvent.clientY : moveEvent.clientX) - start;
      if (axis === "y") element.scrollTop = startScroll + delta * ratio;
      else element.scrollLeft = startScroll + delta * ratio;
    };
    const onUp = () => {
      dragging.current = null;
      thumb.removeEventListener("pointermove", onMove);
      thumb.removeEventListener("pointerup", onUp);
      thumb.removeEventListener("pointercancel", onUp);
    };
    thumb.addEventListener("pointermove", onMove);
    thumb.addEventListener("pointerup", onUp);
    thumb.addEventListener("pointercancel", onUp);
  }

  if (typeof document === "undefined" || bars.length === 0) return null;

  return createPortal(
    <div className="overlay-scrollbars" aria-hidden="true">
      {bars.map((bar) => (
        <div
          key={bar.key}
          className={[
            "overlay-scrollbar",
            `overlay-scrollbar--${bar.axis}`,
            bar.visible ? "overlay-scrollbar--visible" : "",
          ]
            .filter(Boolean)
            .join(" ")}
          style={
            bar.axis === "y"
              ? { top: bar.top, left: bar.left, height: bar.length }
              : { top: bar.top, left: bar.left, width: bar.length }
          }
          onPointerDown={(event) => startDrag(event, bar)}
        />
      ))}
    </div>,
    document.body,
  );
}
