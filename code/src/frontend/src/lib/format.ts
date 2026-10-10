import { getCurrentLocale } from "../features/data-explorer/components/editors/temporalEditing";

/**
 * Amounts are shown in this currency. The model has no currency per column
 * yet; Bento is used in euros first.
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
export function formatAmount(value: number, locale = getCurrentLocale(), currency = DEFAULT_CURRENCY, signed = false): string {
  return formatter(locale, { style: "currency", currency, minimumFractionDigits: 2, maximumFractionDigits: 2, ...(signed ? { signDisplay: "exceptZero" } : {}) }).format(value);
}

/** `signed`: a + in front of positive values too (+10 %, +1.250,00 €), for changes. */
export function formatNumber(value: number, kind: NumberFormatKind = "number", locale = getCurrentLocale(), signed = false): string {
  const sign: Intl.NumberFormatOptions = signed ? { signDisplay: "exceptZero" } : {};
  switch (kind) {
    case "currency":
      return formatAmount(value, locale, DEFAULT_CURRENCY, signed);
    case "integer":
      return formatter(locale, { maximumFractionDigits: 0, ...sign }).format(value);
    case "percent":
      return formatter(locale, { style: "percent", maximumFractionDigits: 1, ...sign }).format(value);
    default:
      return formatter(locale, { maximumFractionDigits: 2, ...sign }).format(value);
  }
}

/** Short axis labels: 1.2K / 1,2 Tsd. */
export function formatCompact(value: number, kind: NumberFormatKind = "number", locale = getCurrentLocale()): string {
  if (kind === "percent") return formatNumber(value, "percent", locale);
  const options: Intl.NumberFormatOptions = { notation: "compact", maximumFractionDigits: 1 };
  if (kind === "currency") Object.assign(options, { style: "currency", currency: DEFAULT_CURRENCY });
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
