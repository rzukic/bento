type ParseLenientNumberOptions = {
  locale?: string;
  integer?: boolean;
};

function getDefaultLocale(): string {
  if (typeof navigator !== "undefined") {
    if (Array.isArray(navigator.languages) && navigator.languages[0]) {
      return navigator.languages[0];
    }
    if (navigator.language) return navigator.language;
  }
  return Intl.NumberFormat().resolvedOptions().locale;
}

function getLocaleDecimalSeparator(locale: string): string {
  const parts = new Intl.NumberFormat(locale).formatToParts(1234.5);
  return parts.find((part) => part.type === "decimal")?.value ?? ".";
}

function normaliseLooseNumericString(raw: string, locale: string): string | null {
  const trimmed = raw.trim();
  if (trimmed === "") return null;

  const negative = trimmed.includes("-");
  const cleaned = trimmed.replace(/[^\d.,-]/g, "").replace(/-/g, "");
  if (!/\d/.test(cleaned)) return null;

  const lastDot = cleaned.lastIndexOf(".");
  const lastComma = cleaned.lastIndexOf(",");
  let decimalSeparator: "." | "," | null = null;

  if (lastDot >= 0 && lastComma >= 0) {
    decimalSeparator = lastDot > lastComma ? "." : ",";
  } else {
    const separator = lastDot >= 0 ? "." : lastComma >= 0 ? "," : null;
    if (separator) {
      const matches = cleaned.match(separator === "." ? /\./g : /,/g);
      const count = matches?.length ?? 0;
      const lastIndex = separator === "." ? lastDot : lastComma;
      const digitsBefore = cleaned.slice(0, lastIndex).replace(/[^\d]/g, "").length;
      const digitsAfter = cleaned.slice(lastIndex + 1).replace(/[^\d]/g, "").length;
      const localeDecimal = getLocaleDecimalSeparator(locale);

      if (digitsAfter === 0) {
        decimalSeparator = null;
      } else if (count > 1) {
        decimalSeparator = digitsAfter > 0 && digitsAfter <= 2 ? separator : null;
      } else if (digitsAfter !== 3) {
        decimalSeparator = separator;
      } else if (localeDecimal === separator && digitsBefore <= 2) {
        decimalSeparator = separator;
      }
    }
  }

  let normalised: string;
  if (decimalSeparator) {
    const decimalIndex = cleaned.lastIndexOf(decimalSeparator);
    const integerPart = cleaned.slice(0, decimalIndex).replace(/[^\d]/g, "");
    const fractionalPart = cleaned.slice(decimalIndex + 1).replace(/[^\d]/g, "");
    normalised = `${integerPart || "0"}.${fractionalPart}`;
  } else {
    normalised = cleaned.replace(/[^\d]/g, "");
  }

  if (normalised === "" || normalised === ".") return null;
  return negative ? `-${normalised}` : normalised;
}

export function parseLenientNumber(
  raw: string,
  options: ParseLenientNumberOptions = {},
): number | null {
  const locale = options.locale ?? getDefaultLocale();
  const normalised = normaliseLooseNumericString(raw, locale);
  if (!normalised) return null;

  const parsed = Number(normalised);
  if (!Number.isFinite(parsed)) return null;
  if (options.integer && !Number.isInteger(parsed)) return null;
  return parsed;
}
