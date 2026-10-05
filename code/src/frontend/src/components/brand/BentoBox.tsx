import type { CSSProperties } from "react";
import { SushiMascot } from "./SushiMascot";
import type { SushiExpression, SushiKind } from "./sushi";
import "./BentoBox.css";

/**
 * A pixel-art bento box with the whole squad in it, built from layers so
 * the mascots stand *inside*: back rim and floor, the back row, a divider,
 * the front row, then the front wall. Every layer works in box pixels
 * ("u"); the box is 80 × 40 of them.
 */

const BOX_W = 80;
const BOX_H = 40;

/** Each row of four stands in its own compartment. */
const BACK_ROW: SushiKind[] = ["shake", "maguro", "ebi", "tamago"];
const FRONT_ROW: SushiKind[] = ["maki", "ikura", "onigiri", "inari"];

const SLOT_X = [5, 23, 41, 59];
/** Top of each row's mascots and where its compartment wall hides them. */
const BACK_TOP = 0;
const BACK_HIDDEN_FROM = 13;
const FRONT_TOP = 12;
const FRONT_HIDDEN_FROM = 22;

const C = {
  ink: "#2a211c",
  lacquer: "#b8322a",
  rim: "#d9533f",
  shine: "#ee7f68",
  shade: "#7e1f1c",
  floor: "#5c201b",
  floorShade: "#3b1411",
  gold: "#efae25",
  goldLight: "#ffd447",
};

type Px = [x: number, y: number, w: number, h: number, fill: string];

/** "BENTO" in a tiny pixel font, for the front of the box. */
const LETTERS = [
  ["XX.", "X.X", "XX.", "X.X", "XX."],
  ["XXX", "X..", "XX.", "X..", "XXX"],
  ["X..X", "XX.X", "X.XX", "X..X", "X..X"],
  ["XXX", ".X.", ".X.", ".X.", ".X."],
  ["XXX", "X.X", "X.X", "X.X", "XXX"],
];

function lettering(top: number): Px[] {
  const width = LETTERS.reduce((sum, letter) => sum + letter[0].length, 0) + LETTERS.length - 1;
  let x = Math.round((BOX_W - width) / 2);
  const cells: Px[] = [];
  for (const letter of LETTERS) {
    letter.forEach((row, dy) =>
      [...row].forEach((cell, dx) => {
        if (cell === "X") cells.push([x + dx, top + dy, 1, 1, dy === 0 ? C.goldLight : C.gold]);
      }),
    );
    x += letter[0].length + 1;
  }
  return cells;
}

const BACK: Px[] = [
  // Back rim, seen from above, and the side walls.
  [1, 5, 78, 1, C.ink],
  [1, 6, 78, 2, C.rim],
  [0, 6, 1, 16, C.ink],
  [79, 6, 1, 16, C.ink],
  [1, 8, 2, 14, C.lacquer],
  [77, 8, 2, 14, C.lacquer],
  // The red floor of both compartments, darker right under the rims.
  [3, 8, 74, 14, C.floor],
  [3, 8, 74, 1, C.floorShade],
  [3, 13, 74, 1, C.floorShade],
];

const DIVIDER: Px[] = [
  [3, 10, 74, 1, C.ink],
  [3, 11, 74, 1, C.rim],
  [3, 12, 74, 1, C.lacquer],
];

const FRONT: Px[] = [
  [0, 22, 80, 1, C.ink],
  [0, 23, 1, 15, C.ink],
  [79, 23, 1, 15, C.ink],
  [1, 23, 78, 2, C.rim],
  [1, 25, 78, 11, C.lacquer],
  // Lacquer shine at the top left.
  [3, 26, 8, 1, C.shine],
  [3, 27, 3, 1, C.shine],
  ...lettering(28),
  [1, 34, 78, 1, C.gold],
  [1, 35, 78, 2, C.shade],
  [0, 37, 80, 1, C.ink],
  // Little feet.
  [5, 38, 7, 2, C.ink],
  [68, 38, 7, 2, C.ink],
];

function Layer({ cells, className }: { cells: Px[]; className: string }) {
  return (
    <svg className={`bento-box__layer ${className}`} viewBox={`0 0 ${BOX_W} ${BOX_H}`} shapeRendering="crispEdges" aria-hidden="true">
      {cells.map(([x, y, w, h, fill], i) => (
        <rect key={i} x={x} y={y} width={w} height={h} fill={fill} />
      ))}
    </svg>
  );
}

type BentoBoxProps = {
  /** Size of one box pixel in px; the box is 80 of them wide. */
  unit?: number;
  /** Everyone cheers: a jumping wave through the squad, again and again. */
  celebrate?: boolean;
  /** One face for the whole squad (their own blinks and smiles otherwise). */
  mood?: SushiExpression;
  className?: string;
};

export function BentoBox({ unit = 4, celebrate = false, mood, className = "" }: BentoBoxProps) {
  const row = (kinds: SushiKind[], top: number, hiddenFrom: number, offset: number) => (
    <div className="bento-box__row" style={{ top: top * unit, height: (hiddenFrom - top) * unit }}>
      {kinds.map((kind, i) => (
        <span
          key={kind}
          className="bento-box__slot"
          style={{ left: SLOT_X[i] * unit, "--i": offset + i } as CSSProperties}
        >
          <span className="bento-box__pop">
            <SushiMascot kind={kind} width={16 * unit} mood={mood} />
          </span>
        </span>
      ))}
    </div>
  );

  return (
    <div
      className={["bento-box", celebrate ? "bento-box--celebrate" : "", className].filter(Boolean).join(" ")}
      style={{ width: BOX_W * unit, height: BOX_H * unit, "--u": `${unit}px` } as CSSProperties}
      role="img"
      aria-label="A bento box with the whole sushi squad in it"
    >
      <Layer cells={BACK} className="bento-box__back" />
      {row(BACK_ROW, BACK_TOP, BACK_HIDDEN_FROM, 0)}
      <Layer cells={DIVIDER} className="bento-box__divider" />
      {row(FRONT_ROW, FRONT_TOP, FRONT_HIDDEN_FROM, 4)}
      <Layer cells={FRONT} className="bento-box__front" />
    </div>
  );
}
