import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore, type CSSProperties } from "react";
import {
  SUSHI_COLUMNS,
  SUSHI_LABELS,
  SUSHI_PALETTE,
  SUSHI_ROWS,
  currentSushi,
  pickDifferentSushi,
  setCurrentSushi,
  spriteWith,
  subscribeSushi,
  type SushiExpression,
  type SushiKind,
} from "./sushi";
import "./SushiMascot.css";

type SushiMascotProps = {
  /** Which sushi; defaults to the one of this session (shared by every mascot). */
  kind?: SushiKind;
  /** Width in px; multiples of 16 keep every pixel the same size. */
  width?: number;
  /** Hop in place (loading screens); still with reduced motion. */
  bounce?: boolean;
  /** Announce it ("salmon nigiri") instead of hiding it from assistive tech. */
  labelled?: boolean;
  /** Reacts to the pointer: looks at it, hops on hover, jumps on click. */
  interactive?: boolean;
  className?: string;
};

/** Five clicks within this window make the mascot spin into another sushi. */
const SPIN_CLICKS = 5;
const SPIN_WINDOW_MS = 1600;

const HEART = [".RR.RR.", "RRRRRRR", "RRRRRRR", ".RRRRR.", "..RRR..", "...R..."];

function prefersReducedMotion(): boolean {
  return typeof window !== "undefined" && window.matchMedia?.("(prefers-reduced-motion: reduce)").matches === true;
}

type Heart = { id: number; drift: number };

/** One of Bento's pixel-art sushi mascots. */
export function SushiMascot({
  kind,
  width = 32,
  bounce = false,
  labelled = false,
  interactive = true,
  className = "",
}: SushiMascotProps) {
  const sessionKind = useSyncExternalStore(subscribeSushi, currentSushi, currentSushi);
  const shown = kind ?? sessionKind;

  const [expression, setExpression] = useState<SushiExpression>("neutral");
  const [look, setLook] = useState<-1 | 0 | 1>(0);
  const [motion, setMotion] = useState<{ name: "hop" | "jump" | "spin"; key: number } | null>(null);
  const [hearts, setHearts] = useState<Heart[]>([]);
  const box = useRef<HTMLSpanElement | null>(null);
  const hovered = useRef(false);
  const clicks = useRef<number[]>([]);
  const timers = useRef<number[]>([]);

  const later = useCallback((fn: () => void, ms: number) => {
    timers.current.push(window.setTimeout(fn, ms));
  }, []);
  useEffect(() => () => timers.current.forEach((t) => window.clearTimeout(t)), []);

  // Blinks now and then while nothing else is going on.
  useEffect(() => {
    if (!interactive || prefersReducedMotion()) return;
    let timer = 0;
    const schedule = () => {
      timer = window.setTimeout(() => {
        setExpression((current) => (current === "neutral" ? "blink" : current));
        later(() => setExpression((current) => (current === "blink" ? "neutral" : current)), 140);
        schedule();
      }, 2500 + Math.random() * 4000);
    };
    schedule();
    return () => window.clearTimeout(timer);
  }, [interactive, later]);

  // The eyes follow the pointer by a pixel.
  useEffect(() => {
    if (!interactive || prefersReducedMotion()) return;
    let frame = 0;
    const onMove = (event: PointerEvent) => {
      if (frame) return;
      frame = window.requestAnimationFrame(() => {
        frame = 0;
        const rect = box.current?.getBoundingClientRect();
        if (!rect) return;
        const dx = event.clientX - (rect.left + rect.width / 2);
        setLook(dx < -rect.width ? -1 : dx > rect.width ? 1 : 0);
      });
    };
    window.addEventListener("pointermove", onMove, { passive: true });
    return () => {
      window.removeEventListener("pointermove", onMove);
      if (frame) window.cancelAnimationFrame(frame);
    };
  }, [interactive]);

  const play = (name: "hop" | "jump" | "spin") => {
    if (prefersReducedMotion()) return;
    setMotion({ name, key: Date.now() });
  };

  const onPointerEnter = () => {
    hovered.current = true;
    setExpression("happy");
    if (!bounce) play("hop");
  };

  const onPointerLeave = () => {
    hovered.current = false;
    setExpression((current) => (current === "happy" ? "neutral" : current));
  };

  const onClick = () => {
    const now = Date.now();
    clicks.current = [...clicks.current.filter((t) => now - t < SPIN_WINDOW_MS), now];

    if (clicks.current.length >= SPIN_CLICKS) {
      // Enough poking: it spins and comes back as another sushi.
      clicks.current = [];
      setExpression("dizzy");
      play("spin");
      later(() => setCurrentSushi(pickDifferentSushi(currentSushi(), Math.random())), prefersReducedMotion() ? 0 : 280);
      later(() => setExpression(hovered.current ? "happy" : "neutral"), 900);
      return;
    }

    setExpression("excited");
    if (!bounce) play("jump");
    later(() => setExpression((current) => (current === "excited" ? (hovered.current ? "happy" : "neutral") : current)), 650);
    if (!prefersReducedMotion()) {
      const heart = { id: now, drift: Math.round((Math.random() - 0.5) * width * 0.8) };
      setHearts((current) => [...current.slice(-4), heart]);
      later(() => setHearts((current) => current.filter((h) => h.id !== heart.id)), 1000);
    }
  };

  const sprite = useMemo(() => spriteWith(shown, expression, look), [shown, expression, look]);
  const cells = useMemo(
    () =>
      sprite.flatMap((row, y) =>
        [...row].map((cell, x) =>
          SUSHI_PALETTE[cell] ? <rect key={`${x}-${y}`} x={x} y={y} width={1} height={1} fill={SUSHI_PALETTE[cell]} /> : null,
        ),
      ),
    [sprite],
  );

  const svg = (
    <svg
      key={motion ? `${motion.name}-${motion.key}` : "still"}
      className={[
        "sushi-mascot",
        bounce ? "sushi-mascot--bounce" : "",
        motion ? `sushi-mascot--${motion.name}` : "",
        interactive ? "" : className,
      ]
        .filter(Boolean)
        .join(" ")}
      width={width}
      height={(width * SUSHI_ROWS) / SUSHI_COLUMNS}
      viewBox={`0 0 ${SUSHI_COLUMNS} ${SUSHI_ROWS}`}
      shapeRendering="crispEdges"
      data-sushi={shown}
      data-expression={expression}
      onAnimationEnd={() => setMotion(null)}
      {...(labelled ? { role: "img", "aria-label": SUSHI_LABELS[shown] } : { "aria-hidden": true })}
    >
      {cells}
    </svg>
  );

  if (!interactive) return svg;

  const heartSize = Math.max(8, Math.round(width * 0.32));
  return (
    <span
      ref={box}
      className={["sushi-mascot-box", className].filter(Boolean).join(" ")}
      onPointerEnter={onPointerEnter}
      onPointerLeave={onPointerLeave}
      onClick={onClick}
    >
      {svg}
      {hearts.map((heart) => (
        <svg
          key={heart.id}
          className="sushi-mascot__heart"
          width={heartSize}
          height={(heartSize * HEART.length) / HEART[0].length}
          viewBox={`0 0 ${HEART[0].length} ${HEART.length}`}
          shapeRendering="crispEdges"
          style={{ "--drift": `${heart.drift}px` } as CSSProperties}
          aria-hidden="true"
        >
          {HEART.flatMap((row, y) => [...row].map((cell, x) => (cell === "R" ? <rect key={`${x}-${y}`} x={x} y={y} width={1} height={1} fill="#ff5d73" /> : null)))}
        </svg>
      ))}
    </span>
  );
}
