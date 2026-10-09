import { useId, useLayoutEffect, useRef, useState } from "react";
import "./Atmosphere.css";

/**
 * The slowly drifting light field with film grain used behind the public
 * pages (setup wizard, login). Decorative only: hidden from assistive tech,
 * pointer-events off, and still under `prefers-reduced-motion`.
 */
export function Sky({ hue = 0, className = "" }: { hue?: number; className?: string }) {
  const filterId = useId();
  return (
    <div className={["atmo", className].filter(Boolean).join(" ")} aria-hidden="true" style={{ filter: `hue-rotate(${hue}deg)` }}>
      <div className="atmo__pool atmo__pool--1" />
      <div className="atmo__pool atmo__pool--2" />
      <div className="atmo__pool atmo__pool--3" />
      <div className="atmo__vignette" />
      <svg className="atmo__grain" xmlns="http://www.w3.org/2000/svg">
        <filter id={filterId}>
          <feTurbulence type="fractalNoise" baseFrequency="0.9" numOctaves="2" stitchTiles="stitch" />
          <feColorMatrix type="saturate" values="0" />
        </filter>
        <rect width="100%" height="100%" filter={`url(#${filterId})`} />
      </svg>
    </div>
  );
}

export type GlyphTone = "default" | "ok" | "warn";

const GLYPH_STOPS: Record<GlyphTone, [string, string, string, string]> = {
  default: ["var(--aurora-1)", "var(--aurora-2)", "var(--aurora-3)", "var(--aurora-4)"],
  ok: ["#bbf7d0", "#34d399", "#2dd4bf", "#166534"],
  warn: ["#fde68a", "#f59e0b", "#fb7185", "#b45309"]
};

/** The glyph's frame in SVG units: two letters at 640px fill it. */
const GLYPH_WIDTH = 900;
const GLYPH_HEIGHT = 660;
/** Room on the right so the stroke of the last letter is not cut. */
const GLYPH_PADDING = 24;

/**
 * A very large hairline glyph (a step number, a word such as "Bento") stroked
 * with a slowly rotating gradient. Longer words get smaller letters: the
 * frame grows with the text and is scaled down into the same area, anchored
 * bottom left. Decorative; the real value is always rendered as text elsewhere.
 */
export function Glyph({ value, tone = "default", className = "" }: { value: string; tone?: GlyphTone; className?: string }) {
  const gradientId = useId();
  const stops = GLYPH_STOPS[tone];
  const textRef = useRef<SVGTextElement | null>(null);
  // Before measuring: about 0.62em per letter at 640px.
  const [textWidth, setTextWidth] = useState(() => value.length * 400);

  useLayoutEffect(() => {
    const measure = () => {
      const text = textRef.current;
      if (text && typeof text.getBBox === "function") {
        const width = text.getBBox().width;
        if (width > 0) setTextWidth(width);
      }
    };
    measure();
    // The display font may arrive after the first paint; measure again then.
    void document.fonts?.ready.then(measure);
  }, [value]);

  const width = Math.max(GLYPH_WIDTH, textWidth + GLYPH_PADDING);
  const height = (width * GLYPH_HEIGHT) / GLYPH_WIDTH;
  // Grow upwards: the baseline stays at the bottom of the area.
  const viewBox = `0 ${GLYPH_HEIGHT - height} ${width} ${height}`;

  return (
    <div className={["atmo-glyph", className].filter(Boolean).join(" ")} aria-hidden="true" key={value}>
      <svg viewBox={viewBox} preserveAspectRatio="xMinYMax meet" xmlns="http://www.w3.org/2000/svg">
        <defs>
          <linearGradient id={gradientId} x1="0" y1="0" x2="1" y2="1">
            <stop offset="0" stopColor={stops[0]} />
            <stop offset="0.4" stopColor={stops[1]} />
            <stop offset="0.7" stopColor={stops[2]} />
            <stop offset="1" stopColor={stops[3]} />
            <animateTransform attributeName="gradientTransform" type="rotate" from="0 0.5 0.5" to="360 0.5 0.5" dur="18s" repeatCount="indefinite" />
          </linearGradient>
        </defs>
        <text ref={textRef} className="atmo-glyph__text" x="0" y="600" fill="none" stroke={`url(#${gradientId})`} strokeWidth={1.25 * (width / GLYPH_WIDTH)}>
          {value}
        </text>
      </svg>
    </div>
  );
}
