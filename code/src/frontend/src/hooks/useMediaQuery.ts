import { useEffect, useState } from "react";

function matches(query: string): boolean {
  return typeof window !== "undefined" && typeof window.matchMedia === "function" && window.matchMedia(query).matches;
}

/** Whether a media query matches now, kept current as it changes (rotating a phone, resizing a window). */
export function useMediaQuery(query: string): boolean {
  const [value, setValue] = useState(() => matches(query));
  useEffect(() => {
    if (typeof window === "undefined" || typeof window.matchMedia !== "function") return;
    const media = window.matchMedia(query);
    const onChange = () => setValue(media.matches);
    onChange();
    media.addEventListener?.("change", onChange);
    return () => media.removeEventListener?.("change", onChange);
  }, [query]);
  return value;
}

/** The phone and small-tablet layout: tab bar at the bottom, the sidebar as a drawer. */
export const PHONE_LAYOUT = "(max-width: 900px)";

/** A touch screen without hover: a tap is the only way to point at something. */
export const COARSE_POINTER = "(hover: none) and (pointer: coarse)";
