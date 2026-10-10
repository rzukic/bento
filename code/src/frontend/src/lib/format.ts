import { getCurrentLocale } from "../features/data-explorer/components/editors/temporalEditing";
import { currentCurrency } from "./preferences";

/**
 * Amounts are shown in the workspace's currency (Settings; EUR unless an admin
 * chose another). The model has no currency per column yet.
 */
export const DEFAULT_CURRENCY = "EUR";

export type NumberFormatKind = "currency" | "number" | "integer" | "percent";

const cache = new Map<string, Intl.NumberFormat>();

function formatter(locale: string, options: Intl.NumberFormatOptions): Intl.NumberFormat {
  const key = `${locale}|${JSON.stringify(options)}`;
  let format = cache.get(key);
  if (!format) {
    format = new Intl.NumberFormat(locale, options);
    cache.set(key, format);
  }
  return format;
}

export function toNumber(value: unknown): number | null {
  if (typeof value === "number") return Number.isFinite(value) ? value : null;
  if (typeof value === "string" && value.trim() !== "") {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : null;
  }
  return null;
}

/** "-1150" → "−1.150,00 €" (de-DE) or "-€1,150.00" (en-US). */
export function formatAmount(value: number, locale = getCurrentLocale(), currency = currentCurrency()): string {
  return formatter(locale, { style: "currency", currency, minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value);
}

export function formatNumber(value: number, kind: NumberFormatKind = "number", locale = getCurrentLocale()): string {
  switch (kind) {
    case "currency":
      return formatAmount(value, locale);
    case "integer":
      return formatter(locale, { maximumFractionDigits: 0 }).format(value);
    case "percent":
      return formatter(locale, { style: "percent", maximumFractionDigits: 1 }).format(value);
    default:
      return formatter(locale, { maximumFractionDigits: 2 }).format(value);
  }
}

/** Short axis labels: 1.2K / 1,2 Tsd. */
export function formatCompact(value: number, kind: NumberFormatKind = "number", locale = getCurrentLocale()): string {
  if (kind === "percent") return formatNumber(value, "percent", locale);
  const options: Intl.NumberFormatOptions = { notation: "compact", maximumFractionDigits: 1 };
  if (kind === "currency") Object.assign(options, { style: "currency", currency: currentCurrency() });
  return formatter(locale, options).format(value);
}

/** The number format a column or result type suggests. */
export function formatKindForType(type: string | undefined): NumberFormatKind {
  switch (type) {
    case "currency":
      return "currency";
    case "integer":
    case "int64":
    case "reference":
      return "integer";
    default:
      return "number";
  }
}
