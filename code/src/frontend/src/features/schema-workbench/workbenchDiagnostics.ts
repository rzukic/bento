import type { SchemaIssue } from "../../types/api";
import type { JsonDraftLocation } from "./draftJson";
import { resolveJsonPathLocation } from "./jsonPathLocation";
import type { WorkbenchDiagnostic } from "./types";

export function buildBackendDiagnostic(
  issue: SchemaIssue,
  severity: "error" | "warning",
  index: number,
  draftText: string,
): WorkbenchDiagnostic {
  const resolution = resolveJsonPathLocation(draftText, issue.path);

  return {
    code: issue.code,
    id: `backend:${severity}:${index}:${issue.path}`,
    location: resolution?.location ?? null,
    locationExact: resolution?.exact ?? false,
    message: issue.message,
    path: issue.path,
    severity,
    source: "backend",
  };
}

export function resolvePreviewErrorLocation(
  message: string,
  draftText: string,
): Pick<WorkbenchDiagnostic, "location" | "locationExact" | "path"> | null {
  const path = extractServerJsonPath(message);
  if (path) {
    const resolution = resolveJsonPathLocation(draftText, path);
    if (resolution) {
      return {
        location: resolution.location,
        locationExact: resolution.exact,
        path,
      };
    }
  }

  const lineByteLocation = extractServerLineByteLocation(message, draftText);
  if (lineByteLocation) {
    return {
      location: lineByteLocation,
      locationExact: true,
      path: path ?? null,
    };
  }

  const scalarPath = resolveQuotedScalarPath(message, draftText);
  if (scalarPath) {
    const resolution = resolveJsonPathLocation(draftText, scalarPath);
    if (resolution) {
      return {
        location: resolution.location,
        locationExact: resolution.exact,
        path: scalarPath,
      };
    }
  }

  return null;
}

function extractServerJsonPath(message: string): string | null {
  const match = message.match(/Path:\s*([^|]+?)(?:\s*\||$)/i);
  if (!match) {
    return null;
  }

  let path = match[1].trim();
  if (!path) {
    return null;
  }

  path = path
    .replace(/^\$/, "")
    .replace(/^\./, "")
    .replace(/\[['"]([^'"]+)['"]\]/g, ".$1");

  return path || "schema";
}

function extractServerLineByteLocation(
  message: string,
  draftText: string,
): JsonDraftLocation | null {
  const match = message.match(
    /LineNumber:\s*(\d+)\s*\|\s*BytePositionInLine:\s*(\d+)/i,
  );
  if (!match) {
    return null;
  }

  const lineNumber = Number.parseInt(match[1], 10);
  const bytePosition = Number.parseInt(match[2], 10);
  if (
    !Number.isFinite(lineNumber) ||
    !Number.isFinite(bytePosition) ||
    lineNumber < 0 ||
    bytePosition < 0
  ) {
    return null;
  }

  const line = lineNumber + 1;
  const column = bytePosition + 1;
  if (line > draftText.split("\n").length) {
    return null;
  }

  return { line, column };
}

function resolveQuotedScalarPath(
  message: string,
  draftText: string,
): string | null {
  const quotedValues = [...message.matchAll(/['"]([^'"]+)['"]/g)]
    .map((match) => match[1])
    .filter((value) => value.length > 0);

  if (quotedValues.length === 0) {
    return null;
  }

  try {
    const document = JSON.parse(draftText) as unknown;
    for (const value of quotedValues) {
      const path = findScalarValuePath(document, value);
      if (path) {
        return path;
      }
    }
  } catch {
    return null;
  }

  return null;
}

function findScalarValuePath(
  value: unknown,
  target: string,
  base = "",
): string | null {
  if (value === target) {
    return base || "schema";
  }

  if (Array.isArray(value)) {
    for (let index = 0; index < value.length; index += 1) {
      const path = findScalarValuePath(
        value[index],
        target,
        `${base}[${index}]`,
      );
      if (path) {
        return path;
      }
    }

    return null;
  }

  if (value && typeof value === "object") {
    for (const [key, child] of Object.entries(value)) {
      const path = findScalarValuePath(
        child,
        target,
        base ? `${base}.${key}` : key,
      );
      if (path) {
        return path;
      }
    }
  }

  return null;
}
