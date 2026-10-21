import { useFormatStore } from "./preferences";

/**
 * The workspace's calendar (Settings → Workspace), as the server keeps it:
 * the time zone "today" is in, the day weeks start on and the month the
 * (fiscal) year starts in. Date ranges, chart periods and their names follow
 * it, so a dashboard and a report agree on what "this week" is.
 */
export type WeekStart = "monday" | "sunday" | "saturday";
export type WorkspaceCalendar = { timeZone: string; weekStart: WeekStart; yearStart: number };

function browserTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
  } catch {
    return "UTC";
  }
}

let calendar: WorkspaceCalendar = { timeZone: browserTimeZone(), weekStart: "monday", yearStart: 1 };

export function workspaceCalendar(): WorkspaceCalendar {
  return calendar;
}

export function setWorkspaceCalendar(next: Partial<WorkspaceCalendar> | null | undefined): void {
  const merged: WorkspaceCalendar = {
    timeZone: next?.timeZone || browserTimeZone(),
    weekStart: next?.weekStart === "sunday" || next?.weekStart === "saturday" ? next.weekStart : "monday",
    yearStart: next?.yearStart && next.yearStart >= 1 && next.yearStart <= 12 ? next.yearStart : 1,
  };
  if (merged.timeZone === calendar.timeZone && merged.weekStart === calendar.weekStart && merged.yearStart === calendar.yearStart) return;
  calendar = merged;
  useFormatStore.setState((s) => ({ version: s.version + 1 }));
}

/** Every time zone this browser knows, for the setting. */
export function timeZoneChoices(): string[] {
  const supported = (Intl as unknown as { supportedValuesOf?: (key: string) => string[] }).supportedValuesOf;
  const zones = typeof supported === "function" ? supported("timeZone") : [];
  return zones.length > 0 ? zones : ["UTC", "Europe/Berlin", "Europe/London", "America/New_York", "America/Los_Angeles", "Asia/Tokyo", "Australia/Sydney"];
}

/** Today's date in the workspace's time zone, yyyy-MM-dd. */
export function todayIso(now: Date = new Date(), timeZone = calendar.timeZone): string {
  try {
    // en-CA writes dates as yyyy-MM-dd.
    return new Intl.DateTimeFormat("en-CA", { timeZone, year: "numeric", month: "2-digit", day: "2-digit" }).format(now);
  } catch {
    return now.toISOString().slice(0, 10);
  }
}

function parse(value: string): [number, number, number] {
  const [y, m, d] = value.slice(0, 10).split("-").map(Number);
  return [y, m, d];
}

/** yyyy-MM-dd of a day given as year, month (may run past 12 or below 1) and day; Date.UTC normalises. */
export function isoDay(year: number, month: number, day = 1): string {
  return new Date(Date.UTC(year, month - 1, day)).toISOString().slice(0, 10);
}

const FIRST_DAY: Record<WeekStart, number> = { monday: 1, sunday: 0, saturday: 6 };

export type CalendarUnit = "day" | "week" | "month" | "quarter" | "year";

/** The first day of the day, week, month, quarter or year holding the date. */
export function periodStart(value: string, unit: CalendarUnit, cal: WorkspaceCalendar = calendar): string {
  const [y, m, d] = parse(value);
  switch (unit) {
    case "day":
      return isoDay(y, m, d);
    case "week": {
      const weekday = new Date(Date.UTC(y, m - 1, d)).getUTCDay();
      return isoDay(y, m, d - ((weekday - FIRST_DAY[cal.weekStart] + 7) % 7));
    }
    case "month":
      return isoDay(y, m);
    default: {
      // Quarters and years count from the year's first month.
      const length = unit === "quarter" ? 3 : 12;
      let months = y * 12 + (m - 1) - (cal.yearStart - 1);
      months -= ((months % length) + length) % length;
      months += cal.yearStart - 1;
      return isoDay(Math.floor(months / 12), (months % 12) + 1);
    }
  }
}

/** The first day of the period after the one starting on `start`. */
export function nextPeriodStart(start: string, unit: CalendarUnit): string {
  const [y, m, d] = parse(start);
  switch (unit) {
    case "day":
      return isoDay(y, m, d + 1);
    case "week":
      return isoDay(y, m, d + 7);
    case "month":
      return isoDay(y, m + 1);
    case "quarter":
      return isoDay(y, m + 3);
    default:
      return isoDay(y + 1, m);
  }
}

/** Q3 2026 (short: Q3 26), or Q1 2026/27 in a year from April. */
export function quarterName(start: string, short = false, cal: WorkspaceCalendar = calendar): string {
  const [y, m] = parse(start);
  const yearStart = periodStart(start, "year", cal);
  const [fy, fm] = parse(yearStart);
  const quarter = Math.floor((y * 12 + m - (fy * 12 + fm)) / 3) + 1;
  return `Q${quarter} ${yearName(yearStart, short, cal)}`;
}

/** 2026 (short: 26), or 2026/27 for a year from April 2026. */
export function yearName(start: string, short = false, cal: WorkspaceCalendar = calendar): string {
  const [y] = parse(start);
  if (cal.yearStart === 1) return short ? String(y).slice(2) : String(y);
  const next = String((y + 1) % 100).padStart(2, "0");
  return short ? `${String(y).slice(2)}/${next}` : `${y}/${next}`;
}
