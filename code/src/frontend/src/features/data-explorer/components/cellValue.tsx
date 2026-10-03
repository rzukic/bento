import { getCurrentLocale } from "./editors/temporalEditing";
import type { ModelColumn } from "../../../types/api";

function normalizeCalendarPart(value: string): number | null {
  const normalized = Number.parseInt(value, 10);
  if (!Number.isFinite(normalized) || normalized <= 0) return null;
  return normalized;
}

function parseTemporalValue(
  value: string,
  type: ModelColumn["type"],
): { date: Date; timeZone?: string } | null {
  const trimmed = value.trim();
  if (!trimmed) return null;

  if (type === "date") {
    const dateOnlyMatch = trimmed.match(/^(\d{4})-(\d{1,3})-(\d{1,3})$/);
    if (dateOnlyMatch) {
      const [, year, monthRaw, dayRaw] = dateOnlyMatch;
      const month = normalizeCalendarPart(monthRaw);
      const day = normalizeCalendarPart(dayRaw);
      if (month === null || day === null) return null;
      return {
        date: new Date(Date.UTC(Number(year), month - 1, day)),
        timeZone: "UTC",
      };
    }
  }

  const isoLikeMatch = trimmed.match(
    /^(\d{4})-(\d{1,3})-(\d{1,3})[T\s](\d{2}):(\d{2})(?::(\d{2}))?(?:\.(\d{1,3}))?(Z|[+-]\d{2}:\d{2})?$/,
  );

  if (isoLikeMatch) {
    const [, year, monthRaw, dayRaw, hour, minute, second = "0", fraction = "0", zone] =
      isoLikeMatch;
    const month = normalizeCalendarPart(monthRaw);
    const day = normalizeCalendarPart(dayRaw);
    if (month === null || day === null) return null;

    if (zone) {
      const normalized = `${year}-${String(month).padStart(2, "0")}-${String(day).padStart(2, "0")}T${hour}:${minute}:${second}.${fraction.padEnd(3, "0")}${zone}`;
      const zonedDate = new Date(normalized);
      if (!Number.isNaN(zonedDate.getTime())) {
        return zone === "Z"
          ? { date: zonedDate, timeZone: "UTC" }
          : { date: zonedDate };
      }
    }

    return {
      date: new Date(
        Number(year),
        month - 1,
        day,
        Number(hour),
        Number(minute),
        Number(second),
        Number(fraction.padEnd(3, "0")),
      ),
    };
  }

  const fallback = new Date(trimmed);
  if (!Number.isNaN(fallback.getTime())) {
    return { date: fallback };
  }

  return null;
}

export function formatTemporalValue(value: string, type: ModelColumn["type"]): string {
  const parsed = parseTemporalValue(value, type);
  if (!parsed) return value;

  if (type === "date") {
    return new Intl.DateTimeFormat(getCurrentLocale(), {
      year: "numeric",
      month: "short",
      day: "2-digit",
      timeZone: parsed.timeZone,
    }).format(parsed.date);
  }

  return new Intl.DateTimeFormat(getCurrentLocale(), {
    year: "numeric",
    month: "short",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    timeZone: parsed.timeZone,
  }).format(parsed.date);
}

export function getJsonPreview(value: unknown): string {
  try {
    const raw = JSON.stringify(value);
    if (!raw) return "—";
    return raw.length > 80 ? `${raw.slice(0, 77)}...` : raw;
  } catch {
    return String(value);
  }
}

export function normalizeBooleanValue(value: unknown): boolean | null {
  if (typeof value === "boolean") return value;
  if (typeof value === "number") {
    if (value === 1) return true;
    if (value === 0) return false;
    return null;
  }

  if (typeof value === "string") {
    const normalized = value.trim().toLowerCase();
    if (normalized === "true" || normalized === "1") return true;
    if (normalized === "false" || normalized === "0") return false;
  }

  return null;
}

/** Categorical dot colours for enum values, picked by position in the column's value list. */
const ENUM_PALETTE = ["#60a5fa", "#2dd4bf", "#a78bfa", "#f472b6", "#fb923c", "#38bdf8", "#a3e635", "#e879f9"];

/** Common state words get semantic colours so status columns read at a glance. */
const ENUM_SEMANTIC: Array<[RegExp, string]> = [
  [/^(done|paid|complete|completed|active|approved|success|won|published|resolved|closed)$/i, "var(--success)"],
  [/^(cancel+ed|failed|error|rejected|lost|blocked|deleted|overdue)$/i, "var(--danger)"],
  [/^(pending|draft|waiting|open|todo|new|review|in[ _-]?review|on[ _-]?hold)$/i, "var(--warning)"],
  [/^(shipped|sent|in[ _-]?progress|processing|running|scheduled)$/i, "var(--info)"]
];

export function enumValueColor(column: ModelColumn, value: string): string {
  for (const [pattern, color] of ENUM_SEMANTIC) {
    if (pattern.test(value.trim())) return color;
  }
  const index = column.values?.indexOf(value) ?? -1;
  if (index >= 0) return ENUM_PALETTE[index % ENUM_PALETTE.length];
  let hash = 0;
  for (const char of value) hash = (hash * 31 + char.charCodeAt(0)) >>> 0;
  return ENUM_PALETTE[hash % ENUM_PALETTE.length];
}

export function renderCellValue(column: ModelColumn, value: unknown) {
  if (value === null || value === undefined || value === "") {
    return <span className="data-grid__cell-empty">—</span>;
  }

  if (column.type === "boolean") {
    const booleanValue = normalizeBooleanValue(value);
    if (booleanValue !== null) {
      return (
        <span
          className={`data-grid__boolean data-grid__boolean--${booleanValue ? "true" : "false"}`}
        >
          {booleanValue ? "Yes" : "No"}
        </span>
      );
    }
  }

  if (typeof value === "boolean") {
    return (
      <span
        className={`data-grid__boolean data-grid__boolean--${value ? "true" : "false"}`}
      >
        {value ? "Yes" : "No"}
      </span>
    );
  }

  if (typeof value === "number") {
    return <span className="data-grid__mono">{value}</span>;
  }

  if (typeof value === "string" && (column.type === "date" || column.type === "datetime")) {
    return <span className="data-grid__mono">{formatTemporalValue(value, column.type)}</span>;
  }

  if (column.type === "json" || typeof value === "object") {
    return (
      <code className="data-grid__code" title={getJsonPreview(value)}>
        {getJsonPreview(value)}
      </code>
    );
  }

  if (column.type === "enum" && typeof value === "string") {
    return (
      <span className="chip data-grid__enum">
        <span className="chip__dot" aria-hidden="true" style={{ background: enumValueColor(column, value) }} />
        {value}
      </span>
    );
  }

  if (column.type === "relation" || column.references) {
    return (
      <span className="data-grid__reference" title={String(value)}>
        {String(value)}
      </span>
    );
  }

  if (column.primaryKey) {
    return <span className="data-grid__mono">{String(value)}</span>;
  }

  return <span>{String(value)}</span>;
}
