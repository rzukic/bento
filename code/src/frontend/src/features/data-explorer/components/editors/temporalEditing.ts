import { effectiveLocale } from "../../../../lib/preferences";
/** The locale numbers and dates are written in: this browser's choice, the workspace's, or the browser's language. */
export function getCurrentLocale(): string {
  return effectiveLocale();
}

function pad(value: number): string {
  return String(value).padStart(2, "0");
}

export type CalendarDay = {
  isoDate: string;
  dayOfMonth: number;
  inMonth: boolean;
  isToday: boolean;
};

function inferDateOrder(locale: string): Array<"day" | "month" | "year"> {
  const parts = new Intl.DateTimeFormat(locale, {
    day: "2-digit",
    month: "2-digit",
    year: "numeric",
    timeZone: "UTC",
  }).formatToParts(new Date(Date.UTC(2006, 10, 24)));

  return parts
    .filter(
      (part): part is Intl.DateTimeFormatPart & {
        type: "day" | "month" | "year";
      } => part.type === "day" || part.type === "month" || part.type === "year",
    )
    .map((part) => part.type);
}

function normalizeYear(raw: number): number {
  if (raw >= 100) return raw;
  return raw >= 70 ? 1900 + raw : 2000 + raw;
}

function isValidDateParts(year: number, month: number, day: number): boolean {
  const candidate = new Date(Date.UTC(year, month - 1, day));
  return (
    candidate.getUTCFullYear() === year &&
    candidate.getUTCMonth() === month - 1 &&
    candidate.getUTCDate() === day
  );
}

/**
 * The date as the grid shows it (e.g. "08. Sept. 2026" / "Sep 08, 2026"),
 * so entering a date field shows the same text it displayed.
 */
export function formatDateDraft(value: string | null, locale = getCurrentLocale()): string {
  if (!value) return "";
  const match = value.match(/^(\d{4})-(\d{2})-(\d{2})/);
  if (!match) return "";
  const [, year, month, day] = match;
  const date = new Date(Date.UTC(Number(year), Number(month) - 1, Number(day)));
  return new Intl.DateTimeFormat(locale, {
    day: "2-digit",
    month: "short",
    year: "numeric",
    timeZone: "UTC",
  }).format(date);
}

/** The time as the grid shows it in this locale ("14:40" / "02:40 PM"). */
export function formatTimeDraft(value: string | null, locale = getCurrentLocale()): string {
  if (!value) return "";
  const match = value.match(/(?:^|[T\s])(\d{2}):(\d{2})/);
  if (!match) return "";
  const date = new Date(Date.UTC(2000, 0, 1, Number(match[1]), Number(match[2])));
  return new Intl.DateTimeFormat(locale, {
    hour: "2-digit",
    minute: "2-digit",
    timeZone: "UTC",
  }).format(date);
}

/** What the grid puts between date and time (", " in most locales). */
export function getDateTimeSeparator(locale = getCurrentLocale()): string {
  const parts = new Intl.DateTimeFormat(locale, {
    day: "2-digit",
    month: "short",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit",
    timeZone: "UTC",
  }).formatToParts(new Date(Date.UTC(2006, 10, 24, 13, 45)));
  const yearIndex = parts.findIndex((part) => part.type === "year");
  const hourIndex = parts.findIndex((part) => part.type === "hour");
  if (yearIndex === -1 || hourIndex !== yearIndex + 2) return ", ";
  return parts[yearIndex + 1]?.value ?? ", ";
}

function normalizeMonthToken(token: string): string {
  return token.toLocaleLowerCase().replace(/[.\s]/g, "");
}

/** Month number (1-12) for a written month in this locale or English. */
function parseMonthName(token: string, locale: string): number | null {
  const wanted = normalizeMonthToken(token);
  if (wanted.length < 3) return null;
  for (const candidateLocale of [locale, "en"]) {
    for (let month = 0; month < 12; month += 1) {
      const date = new Date(Date.UTC(2006, month, 15));
      for (const style of ["short", "long"] as const) {
        const name = normalizeMonthToken(
          new Intl.DateTimeFormat(candidateLocale, { month: style, timeZone: "UTC" }).format(date),
        );
        if (name === wanted || name.startsWith(wanted)) return month + 1;
      }
    }
  }
  return null;
}

export function parseDateDraft(
  raw: string,
  locale = getCurrentLocale(),
): string | null {
  const trimmed = raw.trim();
  if (trimmed === "") return null;

  const groups = trimmed.match(/\d+/g);
  if (!groups) return null;

  // Written month ("08. Sept. 2026", "Sep 08, 2026"): the two numbers are
  // day and year.
  const word = trimmed.match(/\p{L}{3,}\.?/u)?.[0];
  if (word && groups.length === 2) {
    const month = parseMonthName(word, locale);
    if (month === null) return null;
    const [a, b] = groups.map((part) => Number.parseInt(part, 10)) as [number, number];
    const aIsYear = (groups[0] ?? "").length > 2 || a > 31;
    const day = aIsYear ? b : a;
    const year = normalizeYear(aIsYear ? a : b);
    if (!isValidDateParts(year, month, day)) return null;
    return `${year}-${pad(month)}-${pad(day)}`;
  }
  if (groups.length < 3) return null;

  const [first, second, third] = groups.slice(0, 3).map((part) => Number.parseInt(part, 10));
  if (![first, second, third].every(Number.isFinite)) return null;

  const byType = Object.fromEntries(
    inferDateOrder(locale).map((type, index) => [type, [first, second, third][index] ?? null]),
  ) as Record<"day" | "month" | "year", number | null>;

  const day = byType.day;
  const month = byType.month;
  const year = byType.year !== null ? normalizeYear(byType.year) : null;
  if (day === null || month === null || year === null) return null;
  if (!isValidDateParts(year, month, day)) return null;

  return `${year}-${pad(month)}-${pad(day)}`;
}

export function parseTimeDraft(raw: string): string | null {
  const trimmed = raw.trim();
  if (trimmed === "") return null;

  const groups = trimmed.match(/\d+/g);
  if (!groups || groups.length === 0) return null;

  let hour: number;
  let minute: number;

  if (groups.length >= 2) {
    hour = Number.parseInt(groups[0] ?? "", 10);
    minute = Number.parseInt(groups[1] ?? "", 10);
  } else {
    const digits = groups[0] ?? "";
    if (digits.length <= 2) {
      hour = Number.parseInt(digits, 10);
      minute = 0;
    } else {
      hour = Number.parseInt(digits.slice(0, -2), 10);
      minute = Number.parseInt(digits.slice(-2), 10);
    }
  }

  if (!Number.isFinite(hour) || !Number.isFinite(minute)) return null;
  // 12-hour input ("02:40 PM", "2:40pm").
  const meridiem = trimmed.match(/\b([ap])\.?\s?m\.?\b/i)?.[1]?.toLowerCase();
  if (meridiem) {
    if (hour < 1 || hour > 12) return null;
    if (meridiem === "p" && hour < 12) hour += 12;
    if (meridiem === "a" && hour === 12) hour = 0;
  }
  if (hour < 0 || hour > 23 || minute < 0 || minute > 59) return null;
  return `${pad(hour)}:${pad(minute)}`;
}

export function combineDateTime(dateIso: string, timeValue: string): string {
  return `${dateIso}T${timeValue}`;
}

export function getTodayIsoDate(): string {
  const now = new Date();
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

export function getMonthKey(value: string | null): string {
  const dateIso = value ? parseDateDraft(value) ?? value.slice(0, 10) : getTodayIsoDate();
  const match = dateIso.match(/^(\d{4})-(\d{2})/);
  if (!match) {
    const today = getTodayIsoDate();
    return today.slice(0, 7);
  }
  return `${match[1]}-${match[2]}`;
}

export function shiftMonth(monthKey: string, delta: number): string {
  const match = monthKey.match(/^(\d{4})-(\d{2})$/);
  if (!match) return getTodayIsoDate().slice(0, 7);
  const date = new Date(Date.UTC(Number(match[1]), Number(match[2]) - 1 + delta, 1));
  return `${date.getUTCFullYear()}-${pad(date.getUTCMonth() + 1)}`;
}

export function getMonthLabel(monthKey: string, locale = getCurrentLocale()): string {
  const match = monthKey.match(/^(\d{4})-(\d{2})$/);
  if (!match) return "";
  const date = new Date(Date.UTC(Number(match[1]), Number(match[2]) - 1, 1));
  return new Intl.DateTimeFormat(locale, {
    month: "long",
    year: "numeric",
    timeZone: "UTC",
  }).format(date);
}

export function getMonthName(monthIndex: number, locale = getCurrentLocale()): string {
  const date = new Date(Date.UTC(2026, monthIndex, 1));
  return new Intl.DateTimeFormat(locale, {
    month: "long",
    timeZone: "UTC",
  }).format(date);
}

export function getMonthButtonLabel(monthIndex: number, locale = getCurrentLocale()): string {
  const date = new Date(Date.UTC(2026, monthIndex, 1));
  return new Intl.DateTimeFormat(locale, {
    month: "short",
    timeZone: "UTC",
  }).format(date);
}

export function getMonthParts(monthKey: string): { year: number; monthIndex: number } {
  const match = monthKey.match(/^(\d{4})-(\d{2})$/);
  if (!match) {
    const today = getTodayIsoDate();
    return {
      year: Number(today.slice(0, 4)),
      monthIndex: Number(today.slice(5, 7)) - 1,
    };
  }
  return {
    year: Number(match[1]),
    monthIndex: Number(match[2]) - 1,
  };
}

export function buildMonthKey(year: number, monthIndex: number): string {
  return `${year}-${pad(monthIndex + 1)}`;
}

export function buildYearOptions(anchorYear: number, count = 12): number[] {
  const start = anchorYear - (anchorYear % count);
  return Array.from({ length: count }, (_, index) => start + index);
}

export function getYearRangeLabel(anchorYear: number, count = 12): string {
  const years = buildYearOptions(anchorYear, count);
  return `${years[0]}-${years[years.length - 1]}`;
}

export function getWeekdayLabels(locale = getCurrentLocale()): string[] {
  const formatter = new Intl.DateTimeFormat(locale, {
    weekday: "short",
    timeZone: "UTC",
  });
  const start = new Date(Date.UTC(2026, 10, 23)); // Monday
  return Array.from({ length: 7 }, (_, index) => {
    const day = new Date(start);
    day.setUTCDate(start.getUTCDate() + index);
    return formatter.format(day);
  });
}

export function buildCalendarDays(monthKey: string): CalendarDay[] {
  const match = monthKey.match(/^(\d{4})-(\d{2})$/);
  if (!match) return [];
  const year = Number(match[1]);
  const month = Number(match[2]) - 1;
  const firstOfMonth = new Date(Date.UTC(year, month, 1));
  const startOffset = (firstOfMonth.getUTCDay() + 6) % 7;
  const start = new Date(firstOfMonth);
  start.setUTCDate(firstOfMonth.getUTCDate() - startOffset);

  const today = getTodayIsoDate();
  return Array.from({ length: 42 }, (_, index) => {
    const current = new Date(start);
    current.setUTCDate(start.getUTCDate() + index);
    const isoDate = `${current.getUTCFullYear()}-${pad(current.getUTCMonth() + 1)}-${pad(
      current.getUTCDate(),
    )}`;
    return {
      isoDate,
      dayOfMonth: current.getUTCDate(),
      inMonth: current.getUTCMonth() === month,
      isToday: isoDate === today,
    };
  });
}

export function buildTimeOptions(stepMinutes = 15): string[] {
  const options: string[] = [];
  for (let hour = 0; hour < 24; hour += 1) {
    for (let minute = 0; minute < 60; minute += stepMinutes) {
      options.push(`${pad(hour)}:${pad(minute)}`);
    }
  }
  return options;
}
