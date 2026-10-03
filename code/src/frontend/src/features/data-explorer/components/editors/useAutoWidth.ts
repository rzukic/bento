import { useLayoutEffect, type RefObject } from "react";

let canvas: HTMLCanvasElement | null = null;

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
    canvas ??= document.createElement("canvas");
    const context = canvas.getContext("2d");
    if (!context) return;
    const style = getComputedStyle(input);
    context.font = `${style.fontStyle} ${style.fontWeight} ${style.fontSize} ${style.fontFamily}`;
    const measured = context.measureText(text || input.placeholder || "").width;
    const chrome =
      parseFloat(style.paddingLeft) +
      parseFloat(style.paddingRight) +
      parseFloat(style.borderLeftWidth) +
      parseFloat(style.borderRightWidth);
    input.style.width = `${measured + chrome + caretRoom}px`;
  }, [inputRef, text, enabled, caretRoom]);
}
