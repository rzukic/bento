import { useMemo } from "react";
import {
  SESSION_SUSHI,
  SUSHI_COLUMNS,
  SUSHI_LABELS,
  SUSHI_PALETTE,
  SUSHI_ROWS,
  SUSHI_SPRITES,
  type SushiKind,
} from "./sushi";
import "./SushiMascot.css";

type SushiMascotProps = {
  /** Which sushi; defaults to the one of this app load. */
  kind?: SushiKind;
  /** Width in px; multiples of 16 keep every pixel the same size. */
  width?: number;
  /** Hop in place (loading screens); still with reduced motion. */
  bounce?: boolean;
  /** Announce it ("salmon nigiri") instead of hiding it from assistive tech. */
  labelled?: boolean;
  className?: string;
};

/** One of Bento's pixel-art sushi mascots. */
export function SushiMascot({
  kind = SESSION_SUSHI,
  width = 32,
  bounce = false,
  labelled = false,
  className = "",
}: SushiMascotProps) {
  const cells = useMemo(
    () =>
      SUSHI_SPRITES[kind].flatMap((row, y) =>
        [...row].map((cell, x) =>
          SUSHI_PALETTE[cell] ? (
            <rect key={`${x}-${y}`} x={x} y={y} width={1} height={1} fill={SUSHI_PALETTE[cell]} />
          ) : null,
        ),
      ),
    [kind],
  );

  return (
    <svg
      className={["sushi-mascot", bounce ? "sushi-mascot--bounce" : "", className]
        .filter(Boolean)
        .join(" ")}
      width={width}
      height={(width * SUSHI_ROWS) / SUSHI_COLUMNS}
      viewBox={`0 0 ${SUSHI_COLUMNS} ${SUSHI_ROWS}`}
      shapeRendering="crispEdges"
      data-sushi={kind}
      {...(labelled
        ? { role: "img", "aria-label": SUSHI_LABELS[kind] }
        : { "aria-hidden": true })}
    >
      {cells}
    </svg>
  );
}
