/**
 * Bento's mascots: a family of pixel-art sushi on a 16 × 13 grid (one
 * character per cell, "." is empty). Every app load shows a different one.
 */

export const SUSHI_PALETTE: Record<string, string> = {
  "A": "#d99a3e",
  "G": "#7cc46b",
  "I": "#ff6a2b",
  "K": "#2a211c",
  "M": "#d7263d",
  "N": "#24342c",
  "O": "#ff8552",
  "P": "#f59fb1",
  "R": "#ff7a59",
  "T": "#e8463a",
  "W": "#fbf7ee",
  "X": "#fbf7ee",
  "Y": "#ffd447",
  "a": "#b97a22",
  "g": "#f0c06a",
  "i": "#ffd0b0",
  "m": "#f2788a",
  "n": "#3d5446",
  "o": "#ffc4a6",
  "r": "#fff1ea",
  "w": "#e4dccb",
  "y": "#efae25",
};

export const SUSHI_KINDS = ["shake", "maguro", "ebi", "tamago", "maki", "ikura", "onigiri", "inari"] as const;

export type SushiKind = (typeof SUSHI_KINDS)[number];

export const SUSHI_LABELS: Record<SushiKind, string> = {
  shake: "salmon nigiri",
  maguro: "tuna nigiri",
  ebi: "shrimp nigiri",
  tamago: "tamago nigiri",
  maki: "maki roll",
  ikura: "ikura gunkan",
  onigiri: "onigiri",
  inari: "inari",
};

export const SUSHI_SPRITES: Record<SushiKind, readonly string[]> = {
  shake: [
    "....KKKKKKKK....",
    "..KKOoOOOoOOKK..",
    ".KOoOOOoOOOoOOK.",
    ".KoOOOoOOOoOOoK.",
    "KOOOOOOOOOOOOOOK",
    ".KKWWWWWWWWWWKK.",
    ".KWWWWWWWWWWWWK.",
    ".KWWKWWWWWWKWWK.",
    ".KWPWWWKKWWWPWK.",
    ".KWWWWWWWWWWWWK.",
    ".KwwwwwwwwwwwwK.",
    "..KKKKKKKKKKKK..",
    "...KK......KK...",
  ],
  maguro: [
    "....KKKKKKKK....",
    "..KKMmMMMmMMKK..",
    ".KMmMMMmMMMmMMK.",
    ".KmMMMmMMMmMMmK.",
    "KMMMMMMMMMMMMMMK",
    ".KKWWWWWWWWWWKK.",
    ".KWWWWWWWWWWWWK.",
    ".KWWKWWWWWWKWWK.",
    ".KWPWWWKKWWWPWK.",
    ".KWWWWWWWWWWWWK.",
    ".KwwwwwwwwwwwwK.",
    "..KKKKKKKKKKKK..",
    "...KK......KK...",
  ],
  ebi: [
    "...........KK...",
    "....KKKKKKKTTK..",
    "..KKRRRrRRRrTTK.",
    ".KRrRRRrRRRrRRK.",
    "KRRrRRRrRRRrRRRK",
    ".KKWWWWWWWWWWKK.",
    ".KWWWWWWWWWWWWK.",
    ".KWWKWWWWWWKWWK.",
    ".KWPWWWKKWWWPWK.",
    ".KWWWWWWWWWWWWK.",
    ".KwwwwwwwwwwwwK.",
    "..KKKKKKKKKKKK..",
    "...KK......KK...",
  ],
  tamago: [
    "....KKKKKKKK....",
    "..KKYYYYNNYYKK..",
    ".KYYYYYYNNYYYYK.",
    ".KYYyYYYNNYYyYK.",
    "KYYYYYYYNNYYYYYK",
    ".KKWWWWWNNWWWKK.",
    ".KWWWWWWNNWWWWK.",
    ".KWWKWWWWWWKWWK.",
    ".KWPWWWKKWWWPWK.",
    ".KWWWWWWWWWWWWK.",
    ".KwwwwwwwwwwwwK.",
    "..KKKKKKKKKKKK..",
    "...KK......KK...",
  ],
  maki: [
    "................",
    "...KKKKKKKKKK...",
    "..KWWWWWWWWWWK..",
    ".KWWWOOOOGGWWWK.",
    ".KWWOOOOGGGWWWK.",
    ".KNWWWWWWWWWWNK.",
    ".KNNNWWWWWWNNNK.",
    ".KNNNNNNNNNNNNK.",
    ".KNNXNNNNNNXNNK.",
    ".KNPNNNXXNNNPNK.",
    ".KNNNNNNNNNNNNK.",
    "..KKKKKKKKKKKK..",
    "...KK......KK...",
  ],
  ikura: [
    "...KKKKKKKKKK...",
    "..KIiIIiIIiIIK..",
    ".KIIIiIIIiIIIIK.",
    ".KNNNNNNNNNNNNK.",
    ".KNNNNNNNNNNNNK.",
    ".KNNNNNNNNNNNNK.",
    ".KNNXNNNNNNXNNK.",
    ".KNPNNNXXNNNPNK.",
    ".KNNNNNNNNNNNNK.",
    ".KNNNNNNNNNNNNK.",
    "..KKKKKKKKKKKK..",
    "...KK......KK...",
    "................",
  ],
  onigiri: [
    ".......KK.......",
    "......KWWK......",
    ".....KWWWWK.....",
    "....KWWWWWWK....",
    "...KWWWWWWWWK...",
    "..KWWWWWWWWWWK..",
    ".KWWWKWWWWKWWWK.",
    ".KWWPWWKKWWPWWK.",
    "KNNNNNNNNNNNNNNK",
    "KNnNNNNNNNNNNnNK",
    "KNNNNNNNNNNNNNNK",
    ".KKKKKKKKKKKKKK.",
    "...KK......KK...",
  ],
  inari: [
    "....KKKKKKKK....",
    "..KKWWWWWWWWKK..",
    ".KAAWWWWWWWWAAK.",
    ".KgAAAAAAAAAAgK.",
    ".KAAAAAAAAAAAAK.",
    ".KAAAAAAAAAAAAK.",
    ".KAAKAAAAAAKAAK.",
    ".KAPAAAKKAAAPAK.",
    ".KAAAAAAAAAAAAK.",
    ".KaaaaaaaaaaaaK.",
    "..KKKKKKKKKKKK..",
    "...KK......KK...",
    "................",
  ],
};

export const SUSHI_COLUMNS = 16;
export const SUSHI_ROWS = 13;

const LAST_KEY = "bento.mascot.last";

function readLast(): string | null {
  try {
    return window.localStorage.getItem(LAST_KEY);
  } catch {
    return null;
  }
}

function writeLast(kind: SushiKind) {
  try {
    window.localStorage.setItem(LAST_KEY, kind);
  } catch {
    // Storage unavailable: the next load may repeat this one.
  }
}

/** Any sushi but `last`; `random` is a number in [0, 1). */
export function pickDifferentSushi(last: string | null, random: number): SushiKind {
  const choices = SUSHI_KINDS.filter((kind) => kind !== last);
  return choices[Math.floor(random * choices.length)] ?? "shake";
}

/** A different sushi than the previous app load. */
function pickSessionSushi(): SushiKind {
  if (typeof window === "undefined") return "shake";
  const kind = pickDifferentSushi(readLast(), Math.random());
  writeLast(kind);
  return kind;
}

/** The sushi of this app load; the same everywhere until the next reload. */
export const SESSION_SUSHI: SushiKind = pickSessionSushi();

/** The sprite as SVG markup, e.g. for the favicon. */
export function sushiSvg(kind: SushiKind): string {
  const cells = SUSHI_SPRITES[kind]
    .flatMap((row, y) =>
      [...row].map((cell, x) =>
        SUSHI_PALETTE[cell]
          ? `<rect x="${x}" y="${y}" width="1" height="1" fill="${SUSHI_PALETTE[cell]}"/>`
          : "",
      ),
    )
    .join("");
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 -1.5 16 16" shape-rendering="crispEdges">${cells}</svg>`;
}

// ── Faces ───────────────────────────────────────────────────────────────

type Cell = { x: number; y: number };

export type SushiFace = {
  eyes: [Cell, Cell];
  /** "K" on light bodies, "X" on dark nori. */
  eyeColor: string;
  mouth: Cell[];
};

const isFeature = (cell: string) => cell === "K" || cell === "X";

/**
 * Finds the face in a sprite: the first row with exactly two lone dark (or,
 * on nori, light) pixels inside the outline are the eyes; the pair below
 * them is the mouth.
 */
export function findFace(sprite: readonly string[]): SushiFace | null {
  const inner = (row: string) => {
    const marks = [...row].flatMap((cell, x) => (isFeature(cell) ? [x] : []));
    return marks.slice(1, -1);
  };
  for (let y = 0; y < sprite.length - 1; y++) {
    const row = sprite[y];
    const candidates = inner(row).filter((x) => !isFeature(row[x - 1]) && !isFeature(row[x + 1]));
    if (candidates.length !== 2) continue;
    const below = sprite[y + 1];
    const mouth = inner(below).filter((x) => isFeature(below[x]) && (isFeature(below[x - 1]) || isFeature(below[x + 1])));
    if (mouth.length !== 2) continue;
    return {
      eyes: [
        { x: candidates[0], y },
        { x: candidates[1], y },
      ],
      eyeColor: row[candidates[0]],
      mouth: mouth.map((x) => ({ x, y: y + 1 })),
    };
  }
  return null;
}

export type SushiExpression = "neutral" | "blink" | "happy" | "excited" | "dizzy";

/**
 * The sprite with a face: eyes looking left or right by a pixel, closed
 * (blink, dizzy), "^ ^" (happy) or "^ ^" with an open mouth (excited).
 */
export function spriteWith(kind: SushiKind, expression: SushiExpression, look: -1 | 0 | 1 = 0): readonly string[] {
  const sprite = SUSHI_SPRITES[kind];
  const face = findFace(sprite);
  if (!face || (expression === "neutral" && look === 0)) return sprite;

  const rows = sprite.map((row) => [...row]);
  const set = (x: number, y: number, cell: string) => {
    if (rows[y]?.[x] !== undefined) rows[y][x] = cell;
  };
  const { eyes, eyeColor, mouth } = face;
  // The body colour beside each eye is what an eye leaves behind when it moves.
  const body = eyes.map((eye) => sprite[eye.y][eye.x - 1]);
  eyes.forEach((eye, index) => set(eye.x, eye.y, body[index]));

  eyes.forEach((eye, index) => {
    const x = eye.x + (expression === "neutral" ? look : 0);
    switch (expression) {
      case "blink":
      case "dizzy":
        // A short line; the left eye reaches left, the right eye right.
        set(x, eye.y, eyeColor);
        set(index === 0 ? x - 1 : x + 1, eye.y, eyeColor);
        break;
      case "happy":
      case "excited":
        set(x - 1, eye.y, eyeColor);
        set(x, eye.y - 1, eyeColor);
        set(x + 1, eye.y, eyeColor);
        break;
      default:
        set(x, eye.y, eyeColor);
    }
  });
  if (expression === "excited") for (const cell of mouth) set(cell.x, cell.y + 1, eyeColor);
  return rows.map((row) => row.join(""));
}

// ── The sushi of this session, shared by every mascot ───────────────────

let current: SushiKind = SESSION_SUSHI;
const listeners = new Set<() => void>();

export function currentSushi(): SushiKind {
  return current;
}

export function subscribeSushi(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

/** Switches every mascot (and the favicon) to another sushi. */
export function setCurrentSushi(kind: SushiKind) {
  if (kind === current) return;
  current = kind;
  writeLast(kind);
  applyFavicon(kind);
  listeners.forEach((listener) => listener());
}

/** Points the tab icon at a sushi. */
export function applyFavicon(kind: SushiKind) {
  if (typeof document === "undefined") return;
  let link = document.querySelector<HTMLLinkElement>('link[rel="icon"][data-sushi]');
  if (!link) {
    link = document.createElement("link");
    link.rel = "icon";
    link.type = "image/svg+xml";
    link.dataset.sushi = "";
    document.head.appendChild(link);
  }
  link.dataset.sushi = kind;
  link.href = `data:image/svg+xml,${encodeURIComponent(sushiSvg(kind))}`;
}
