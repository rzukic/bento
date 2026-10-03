import {
  useLayoutEffect,
  useRef,
  useState,
  type CSSProperties,
  type ReactNode,
  type RefObject,
} from "react";
import { createPortal } from "react-dom";

const VIEWPORT_MARGIN = 8;

type Props = {
  /** Element the panel attaches to (the editor control). */
  anchorRef: RefObject<HTMLElement | null>;
  /** Exposes the panel element, e.g. for outside-click checks. */
  panelRef?: RefObject<HTMLDivElement | null>;
  /** Which anchor edge the panel lines up with. */
  align?: "start" | "end";
  /** Give the panel at least the anchor's width. */
  matchAnchorWidth?: boolean;
  /** Gap between anchor and panel in px. */
  gap?: number;
  children: ReactNode;
};

/**
 * Renders an editor popover (listbox, calendar) on document.body with fixed
 * positioning next to its anchor, so no scrolling or clipping container
 * (the grid card, its sticky header, the record editor columns) can cut it
 * off. Opens below the anchor and flips above when there is more room
 * there; follows the anchor on scroll and resize.
 */
export function FloatingPanel({
  anchorRef,
  panelRef,
  align = "start",
  matchAnchorWidth = false,
  gap = 6,
  children,
}: Props) {
  const ownRef = useRef<HTMLDivElement | null>(null);
  const [style, setStyle] = useState<CSSProperties>({ visibility: "hidden" });

  useLayoutEffect(() => {
    if (typeof window === "undefined") return;

    let frame = 0;
    let observer: ResizeObserver | null = null;
    const place = () => {
      // Read the elements on every pass: the anchor is an ancestor, and
      // React attaches its ref only after this child's layout effect ran.
      const anchor = anchorRef.current;
      const panel = ownRef.current;
      if (!anchor || !panel) {
        frame = requestAnimationFrame(place);
        return;
      }
      if (!observer && typeof ResizeObserver !== "undefined") {
        observer = new ResizeObserver(schedule);
        observer.observe(panel);
        observer.observe(anchor);
      }
      const rect = anchor.getBoundingClientRect();
      const height = panel.offsetHeight;
      const width = Math.max(panel.offsetWidth, matchAnchorWidth ? rect.width : 0);
      const spaceBelow = window.innerHeight - rect.bottom - gap - VIEWPORT_MARGIN;
      const spaceAbove = rect.top - gap - VIEWPORT_MARGIN;
      const up = height > spaceBelow && spaceAbove > spaceBelow;
      const top = up ? rect.top - gap - height : rect.bottom + gap;
      const preferredLeft = align === "end" ? rect.right - width : rect.left;
      const left = Math.min(
        Math.max(preferredLeft, VIEWPORT_MARGIN),
        Math.max(VIEWPORT_MARGIN, window.innerWidth - width - VIEWPORT_MARGIN),
      );
      setStyle({
        top: Math.max(VIEWPORT_MARGIN, top),
        left,
        minWidth: matchAnchorWidth ? rect.width : undefined,
      });
    };
    const schedule = () => {
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(place);
    };

    place();
    window.addEventListener("scroll", schedule, true);
    window.addEventListener("resize", schedule);
    return () => {
      cancelAnimationFrame(frame);
      window.removeEventListener("scroll", schedule, true);
      window.removeEventListener("resize", schedule);
      observer?.disconnect();
    };
  }, [anchorRef, align, matchAnchorWidth, gap]);

  if (typeof document === "undefined") return null;

  return createPortal(
    <div
      ref={(element) => {
        ownRef.current = element;
        if (panelRef) panelRef.current = element;
      }}
      className="floating-panel"
      style={style}
    >
      {children}
    </div>,
    document.body,
  );
}
