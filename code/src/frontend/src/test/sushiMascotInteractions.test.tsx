import { act, cleanup, fireEvent, render } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { SushiMascot, SUSHI_KINDS } from "../components/brand";
import { SUSHI_SPRITES, currentSushi, findFace, spriteWith } from "../components/brand/sushi";

function stubReducedMotion(reduce: boolean) {
  vi.stubGlobal("matchMedia", (query: string) => ({
    matches: reduce && query.includes("reduce"),
    media: query,
    addEventListener: () => {},
    removeEventListener: () => {},
  }));
}

beforeEach(() => {
  vi.useFakeTimers();
  stubReducedMotion(false);
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("mascot faces", () => {
  it("finds two eyes and a two-pixel mouth on every sushi", () => {
    for (const kind of SUSHI_KINDS) {
      const face = findFace(SUSHI_SPRITES[kind]);
      expect(face, kind).not.toBeNull();
      expect(face!.mouth, kind).toHaveLength(2);
      expect(face!.mouth[0].y, kind).toBe(face!.eyes[0].y + 1);
      expect(["K", "X"], kind).toContain(face!.eyeColor);
    }
    // Nori bodies have light eyes.
    expect(findFace(SUSHI_SPRITES.maki)!.eyeColor).toBe("X");
  });

  it("draws happy, closed and looking eyes and an open mouth", () => {
    const face = findFace(SUSHI_SPRITES.shake)!;
    const [left, right] = face.eyes;

    const happy = spriteWith("shake", "happy");
    expect(happy[left.y][left.x]).not.toBe("K");
    expect(happy[left.y - 1][left.x]).toBe("K");
    expect(happy[left.y][left.x - 1] + happy[left.y][left.x + 1]).toBe("KK");

    const blink = spriteWith("shake", "blink");
    expect(blink[left.y].slice(left.x - 1, left.x + 1)).toBe("KK");
    expect(blink[right.y].slice(right.x, right.x + 2)).toBe("KK");

    const lookingLeft = spriteWith("shake", "neutral", -1);
    expect(lookingLeft[left.y][left.x - 1]).toBe("K");
    expect(lookingLeft[left.y][left.x]).not.toBe("K");

    const excited = spriteWith("shake", "excited");
    for (const cell of face.mouth) expect(excited[cell.y + 1][cell.x]).toBe("K");

    // Every expression keeps the sprite's size.
    for (const kind of SUSHI_KINDS) {
      for (const expression of ["blink", "happy", "excited", "dizzy"] as const) {
        const sprite = spriteWith(kind, expression, 1);
        expect(sprite).toHaveLength(SUSHI_SPRITES[kind].length);
        for (const row of sprite) expect(row).toHaveLength(16);
      }
    }
  });
});

describe("interactive mascot", () => {
  const svgOf = (container: HTMLElement) => container.querySelector("svg.sushi-mascot")!;

  it("smiles and hops on hover, jumps with a heart on click", () => {
    const { container } = render(<SushiMascot kind="onigiri" width={64} />);
    const box = container.querySelector(".sushi-mascot-box")!;

    fireEvent.pointerEnter(box);
    expect(svgOf(container)).toHaveAttribute("data-expression", "happy");
    expect(svgOf(container)).toHaveClass("sushi-mascot--hop");

    fireEvent.click(box);
    expect(svgOf(container)).toHaveAttribute("data-expression", "excited");
    expect(svgOf(container)).toHaveClass("sushi-mascot--jump");
    expect(container.querySelectorAll(".sushi-mascot__heart")).toHaveLength(1);

    act(() => {
      vi.advanceTimersByTime(1000);
    });
    expect(svgOf(container)).toHaveAttribute("data-expression", "happy");
    expect(container.querySelectorAll(".sushi-mascot__heart")).toHaveLength(0);

    fireEvent.pointerLeave(box);
    expect(svgOf(container)).toHaveAttribute("data-expression", "neutral");
  });

  it("spins into another sushi after five quick clicks, for every mascot", () => {
    const before = currentSushi();
    const { container } = render(
      <>
        <SushiMascot width={32} />
        <SushiMascot width={64} />
      </>,
    );
    const [first] = container.querySelectorAll(".sushi-mascot-box");

    for (let i = 0; i < 5; i++) fireEvent.click(first);
    expect(container.querySelector("svg.sushi-mascot")).toHaveClass("sushi-mascot--spin");
    act(() => {
      vi.advanceTimersByTime(300);
    });

    const kinds = [...container.querySelectorAll("svg.sushi-mascot")].map((svg) => svg.getAttribute("data-sushi"));
    expect(kinds[0]).not.toBe(before);
    expect(kinds[1]).toBe(kinds[0]);
    expect(document.querySelector('link[rel="icon"]')).toHaveAttribute("data-sushi", kinds[0]);
  });

  it("only changes its face when motion is reduced", () => {
    stubReducedMotion(true);
    const { container } = render(<SushiMascot kind="maki" />);
    const box = container.querySelector(".sushi-mascot-box")!;

    fireEvent.pointerEnter(box);
    fireEvent.click(box);

    expect(svgOf(container)).toHaveAttribute("data-expression", "excited");
    expect(svgOf(container).getAttribute("class")).not.toMatch(/--hop|--jump/);
    expect(container.querySelectorAll(".sushi-mascot__heart")).toHaveLength(0);
  });

  it("stays a plain picture when not interactive", () => {
    const { container } = render(<SushiMascot kind="ebi" interactive={false} />);
    expect(container.querySelector(".sushi-mascot-box")).toBeNull();
    expect(container.querySelector("svg")).toHaveAttribute("data-sushi", "ebi");
  });
});
