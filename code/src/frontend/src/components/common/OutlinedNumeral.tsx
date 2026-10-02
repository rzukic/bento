import { useId } from "react";

type OutlinedNumeralProps = {
  value: string;
  className?: string;
  /** Visual strength of the stroke; the stat cards use a quiet 0.5. */
  opacity?: number;
  tone?: "aurora" | "muted";
};

/**
 * A large hairline numeral stroked with the aurora gradient, used as a
 * decorative counterpart to a count (stat cards, step numbers). Hidden from
 * assistive tech: the real number is always rendered as text next to it.
 */
export function OutlinedNumeral({ value, className = "", opacity = 0.5, tone = "aurora" }: OutlinedNumeralProps) {
  const gradientId = useId();
  const stroke = tone === "aurora" ? `url(#${gradientId})` : "var(--line-2)";
  return (
    <svg
      className={["outlined-numeral", className].filter(Boolean).join(" ")}
      viewBox="0 0 160 160"
      aria-hidden="true"
      style={{ opacity }}
    >
      {tone === "aurora" ? (
        <defs>
          <linearGradient id={gradientId} x1="0" y1="0" x2="1" y2="1">
            <stop offset="0" stopColor="var(--aurora-1)" />
            <stop offset="0.5" stopColor="var(--aurora-2)" />
            <stop offset="1" stopColor="var(--aurora-3)" />
          </linearGradient>
        </defs>
      ) : null}
      <text
        x={value.length > 1 ? -30 : 0}
        y="150"
        fill="none"
        stroke={stroke}
        strokeWidth="1"
        style={{ font: "700 190px var(--font-display)", letterSpacing: "-0.06em" }}
      >
        {value}
      </text>
    </svg>
  );
}
