import type { SchemaRelation } from "../../types/api";

const numericColumnTypeLabels = [
  "text",
  "integer",
  "float",
  "boolean",
  "date",
  "datetime",
  "json",
  "reference",
  "computed",
  "currency",
  "enum",
  "uuid",
  "file",
];

export function countLabel(
  count: number,
  singular: string,
  plural = `${singular}s`,
): string {
  return `${count} ${count === 1 ? singular : plural}`;
}

export function formatPreviewValue(value: unknown): string {
  if (value === undefined) {
    return "undefined";
  }

  try {
    const serialized = JSON.stringify(value);
    return serialized ?? String(value);
  } catch {
    return String(value);
  }
}

export function getRelationEndpoint(
  relation: SchemaRelation,
  preferredKey: "from" | "to",
): string {
  const fallbackKey = preferredKey === "from" ? "fromTable" : "toTable";
  const directValue = relation[preferredKey];
  const fallbackValue = relation[fallbackKey];

  return typeof directValue === "string"
    ? directValue
    : typeof fallbackValue === "string"
      ? fallbackValue
      : "Unknown";
}

export function formatMiniErdTypeValue(value: unknown): string {
  if (typeof value === "string") {
    return value.trim().toLowerCase();
  }

  if (typeof value === "number" && Number.isInteger(value)) {
    return numericColumnTypeLabels[value] ?? String(value);
  }

  return formatPreviewValue(value);
}
