import { create } from "zustand";

/**
 * How numbers, dates and amounts are written (beta feedback: "€1,250.00" in an
 * English browser). This browser's choice wins, then the workspace's (an admin
 * sets it under Settings), then the browser's language. Amounts use the
 * workspace's currency.
 */
const USER_LOCALE_KEY = "bento.locale";

let workspace: { locale: string | null; currency: string } = { locale: null, currency: "EUR" };
// When the browser keeps no storage (private mode), the choice lasts until the page reloads.
let unsaved: string | null | undefined;

export function browserLocale(): string {
  if (typeof navigator !== "undefined") {
    if (Array.isArray(navigator.languages) && navigator.languages[0]) return navigator.languages[0];
    if (navigator.language) return navigator.language;
  }
  return Intl.DateTimeFormat().resolvedOptions().locale;
}

/** This browser's own choice, if any. */
export function userLocale(): string | null {
  if (unsaved !== undefined) return unsaved;
  try {
    return window.localStorage.getItem(USER_LOCALE_KEY);
  } catch {
    return null;
  }
}

export function workspaceLocale(): string | null {
  return workspace.locale;
}

export function effectiveLocale(): string {
  return userLocale() ?? workspace.locale ?? browserLocale();
}

export function currentCurrency(): string {
  return workspace.currency;
}

/** Bumped when the format changes, so the page renders again with it. */
export const useFormatStore = create<{ version: number }>(() => ({ version: 0 }));
const bump = () => useFormatStore.setState((s) => ({ version: s.version + 1 }));

export function setWorkspaceFormat(locale: string | null | undefined, currency: string | null | undefined): void {
  const next = { locale: locale || null, currency: currency || "EUR" };
  if (next.locale === workspace.locale && next.currency === workspace.currency) return;
  workspace = next;
  bump();
}

/** null: follow the workspace (or the browser's language). */
export function setUserLocale(locale: string | null): void {
  try {
    if (locale) window.localStorage.setItem(USER_LOCALE_KEY, locale);
    else window.localStorage.removeItem(USER_LOCALE_KEY);
    unsaved = undefined;
  } catch {
    unsaved = locale;
  }
  bump();
}

/** Common choices for the menus; any BCP 47 tag works. */
export const LOCALE_CHOICES: { value: string; name: string }[] = [
  { value: "de-AT", name: "Deutsch (Österreich)" },
  { value: "de-DE", name: "Deutsch (Deutschland)" },
  { value: "de-CH", name: "Deutsch (Schweiz)" },
  { value: "en-GB", name: "English (UK)" },
  { value: "en-US", name: "English (US)" },
  { value: "fr-FR", name: "Français" },
  { value: "it-IT", name: "Italiano" },
  { value: "es-ES", name: "Español" },
  { value: "nl-NL", name: "Nederlands" },
];

/** "Deutsch (Österreich) — € 1.250,00": the name and how an amount looks there. */
export function localeLabel(locale: string, currency = workspace.currency): string {
  const name = LOCALE_CHOICES.find((c) => c.value === locale)?.name ?? locale;
  try {
    return `${name} — ${new Intl.NumberFormat(locale, { style: "currency", currency }).format(1250)}`;
  } catch {
    return name;
  }
}

export const CURRENCY_CHOICES = ["EUR", "USD", "CHF", "GBP", "SEK", "NOK", "DKK", "PLN", "CZK", "HUF", "JPY"];

/** Test-only. */
export function resetFormatForTest(): void {
  workspace = { locale: null, currency: "EUR" };
  unsaved = undefined;
}
