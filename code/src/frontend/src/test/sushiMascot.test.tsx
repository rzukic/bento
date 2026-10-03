import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { SushiMascot, SUSHI_KINDS } from "../components/brand";
import { SUSHI_SPRITES, pickDifferentSushi } from "../components/brand/sushi";

describe("sushi mascots", () => {
  it("every sprite is a 16 × 13 grid", () => {
    for (const kind of SUSHI_KINDS) {
      expect(SUSHI_SPRITES[kind]).toHaveLength(13);
      for (const row of SUSHI_SPRITES[kind]) expect(row).toHaveLength(16);
    }
  });

  it("never picks the sushi of the previous load", () => {
    for (const last of SUSHI_KINDS) {
      for (const random of [0, 0.25, 0.5, 0.75, 0.9999]) {
        expect(pickDifferentSushi(last, random)).not.toBe(last);
      }
    }
  });

  it("renders a chosen sushi, hidden from assistive tech unless labelled", () => {
    const { container, rerender } = render(<SushiMascot kind="onigiri" width={32} />);
    const svg = container.querySelector("svg")!;
    expect(svg).toHaveAttribute("data-sushi", "onigiri");
    expect(svg).toHaveAttribute("aria-hidden", "true");

    rerender(<SushiMascot kind="maki" labelled />);
    expect(container.querySelector("svg")).toHaveAttribute("aria-label", "maki roll");
  });
});
