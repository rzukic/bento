import type { DashboardDateRange, DateRangePreset } from "../../api/dashboards";

export const DATE_RANGE_PRESETS: { value: DateRangePreset; label: string }[] = [
  { value: "last-7-days", label: "Last 7 days" },
  { value: "this-month", label: "This month" },
  { value: "last-3-months", label: "Last 3 months" },
  { value: "last-6-months", label: "Last 6 months" },
  { value: "last-12-months", label: "Last 12 months" },
  { value: "this-year", label: "This year" },
  { value: "last-year", label: "Last year" },
  { value: "last-3-years", label: "Last 3 years" },
  { value: "last-5-years", label: "Last 5 years" },
  { value: "next-12-months", label: "Next 12 months" },
  { value: "next-5-years", label: "Next 5 years" },
  { value: "next-10-years", label: "Next 10 years" },
  { value: "next-20-years", label: "Next 20 years" },
  { value: "all", label: "All time" },
];

/**
 * A resolved range: `from` inclusive, `to` exclusive, both `yyyy-MM-dd`;
 * null = open. It also carries the dashboard's chosen filters, so every
 * query that takes the range takes them too.
 */
export type ResolvedRange = {
  from: string | null;
  to: string | null;
  filters?: AppliedFilter[];
  /**
   * The range reaches past today on purpose (a "next …" preset, or a chosen
   * end in the future): charts and values at period ends go up to its end.
   * Otherwise they stop at today — "This year" in October draws no November.
   */
  future?: boolean;
};

/** A dashboard filter with values chosen: it narrows queries on the listed tables. */
export type AppliedFilter = { column: string; values: (string | number | boolean)[]; tables: string[] };

function iso(year: number, month: number, day = 1): string {
  // month may run past 12 or below 1; Date.UTC normalises it.
  const date = new Date(Date.UTC(year, month - 1, day));
  return date.toISOString().slice(0, 10);
}

/** Whole months and years, so a chart's first and last bucket are complete. */
export function resolveRange(range: DashboardDateRange | undefined, today = new Date()): ResolvedRange {
  if (!range) return { from: null, to: null };
  const year = today.getFullYear();
  const month = today.getMonth() + 1;
  if ("from" in range) {
    // A custom "to" is the last day shown; the filter needs the day after.
    const [y, m, d] = range.to.split("-").map(Number);
    const future = !!range.to && range.to > iso(year, month, today.getDate());
    return { from: range.from || null, to: range.to ? iso(y, m, d + 1) : null, ...(future ? { future } : {}) };
  }
  switch (range.preset) {
    case "last-7-days":
      // Today and the six days before it.
      return { from: iso(year, month, today.getDate() - 6), to: iso(year, month, today.getDate() + 1) };
    case "this-month":
      return { from: iso(year, month), to: iso(year, month + 1) };
    case "last-3-months":
      return { from: iso(year, month - 2), to: iso(year, month + 1) };
    case "last-6-months":
      return { from: iso(year, month - 5), to: iso(year, month + 1) };
    case "last-12-months":
      return { from: iso(year, month - 11), to: iso(year, month + 1) };
    case "this-year":
      return { from: iso(year, 1), to: iso(year + 1, 1) };
    case "last-year":
      return { from: iso(year - 1, 1), to: iso(year, 1) };
    // Whole months, like the 12-month range: the current one and the 35 or 59 before.
    case "last-3-years":
      return { from: iso(year, month - 35), to: iso(year, month + 1) };
    case "last-5-years":
      return { from: iso(year, month - 59), to: iso(year, month + 1) };
    // Forecasts: from the current month on, whole months.
    case "next-12-months":
      return { from: iso(year, month), to: iso(year, month + 12), future: true };
    case "next-5-years":
      return { from: iso(year, month), to: iso(year, month + 60), future: true };
    case "next-10-years":
      return { from: iso(year, month), to: iso(year, month + 120), future: true };
    case "next-20-years":
      return { from: iso(year, month), to: iso(year, month + 240), future: true };
    default:
      return { from: null, to: null };
  }
}

/** ?range=last-12-months or ?from=2026-01-01&to=2026-06-30 */
export function rangeFromSearch(params: URLSearchParams): DashboardDateRange | null {
  const preset = params.get("range");
  if (preset && DATE_RANGE_PRESETS.some((p) => p.value === preset)) return { preset: preset as DateRangePreset };
  const from = params.get("from");
  const to = params.get("to");
  if (from && to && /^\d{4}-\d{2}-\d{2}$/.test(from) && /^\d{4}-\d{2}-\d{2}$/.test(to)) return { from, to };
  return null;
}

export function rangeToSearch(range: DashboardDateRange, params: URLSearchParams): URLSearchParams {
  const next = new URLSearchParams(params);
  next.delete("range");
  next.delete("from");
  next.delete("to");
  if ("preset" in range) next.set("range", range.preset);
  else {
    next.set("from", range.from);
    next.set("to", range.to);
  }
  return next;
}

export function rangeKey(range: DashboardDateRange | undefined): string {
  if (!range) return "none";
  return "preset" in range ? range.preset : `${range.from}..${range.to}`;
}
