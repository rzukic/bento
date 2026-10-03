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
