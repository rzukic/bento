import { useLayoutEffect, type RefObject } from "react";

let probe: HTMLSpanElement | null = null;

/** Width of `text` rendered exactly as `input` renders it (font, spacing, features). */
function measureText(input: HTMLInputElement, text: string): number {
  if (!probe) {
    probe = document.createElement("span");
    probe.setAttribute("aria-hidden", "true");
    Object.assign(probe.style, {
      position: "absolute",
      visibility: "hidden",
      whiteSpace: "pre",
      left: "-9999px",
      top: "0",
    });
    document.body.appendChild(probe);
  }
  const style = getComputedStyle(input);
  // Longhands: the computed `font` shorthand is empty in some engines.
  probe.style.fontFamily = style.fontFamily;
  probe.style.fontSize = style.fontSize;
  probe.style.fontWeight = style.fontWeight;
  probe.style.fontStyle = style.fontStyle;
  probe.style.fontStretch = style.fontStretch;
  probe.style.letterSpacing = style.letterSpacing;
  probe.style.fontFeatureSettings = style.fontFeatureSettings;
  probe.style.fontVariantNumeric = style.fontVariantNumeric;
  probe.style.textTransform = style.textTransform;
  probe.textContent = text;
  return probe.getBoundingClientRect().width;
}

/**
 * Sizes an input to its text (or placeholder) in its own computed font, so
 * a value edited in place keeps the width and position it had as text.
 */
export function useAutoWidth(
  inputRef: RefObject<HTMLInputElement | null>,
  text: string,
  enabled = true,
  /** Extra px after the text so the caret is never clipped (last input only). */
  caretRoom = 2,
) {
  useLayoutEffect(() => {
    const input = inputRef.current;
    if (!enabled || !input || typeof document === "undefined") return;
    const style = getComputedStyle(input);
    const measured = measureText(input, text || input.placeholder || "");
    const chrome =
      parseFloat(style.paddingLeft) +
      parseFloat(style.paddingRight) +
      parseFloat(style.borderLeftWidth) +
      parseFloat(style.borderRightWidth);
    input.style.width = `${measured + chrome + caretRoom}px`;
  }, [inputRef, text, enabled, caretRoom]);
}
