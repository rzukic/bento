import { locationFromIndex, type JsonTextLocation } from "./jsonTextPosition";

export type JsonDraftLocation = JsonTextLocation;

export type JsonDraftParseSuccess = {
  status: "valid";
  value: unknown;
  formattedText: string;
};

export type JsonDraftParseFailure = {
  status: "invalid";
  detail: string;
  location: JsonDraftLocation | null;
  message: string;
};

export type JsonDraftParseResult = JsonDraftParseSuccess | JsonDraftParseFailure;

const LINE_COLUMN_PATTERN = /line (\d+) column (\d+)/i;
const POSITION_PATTERN = /position (\d+)/i;

export function formatJsonValue(value: unknown): string {
  return JSON.stringify(value, null, 2);
}

export function parseJsonDraft(text: string): JsonDraftParseResult {
  try {
    const value = JSON.parse(text) as unknown;

    return {
      status: "valid",
      value,
      formattedText: formatJsonValue(value),
    };
  } catch (error) {
    const detail =
      error instanceof Error ? error.message : "The draft could not be parsed as JSON.";
    const location = extractJsonLocation(text, detail);
    const message = location
      ? `The draft is not valid JSON yet. Fix line ${location.line}, column ${location.column} to continue formatting or previewing.`
      : "The draft is not valid JSON yet. Fix the syntax to continue formatting or previewing.";

    return {
      status: "invalid",
      detail,
      location,
      message,
    };
  }
}

function extractJsonLocation(text: string, detail: string): JsonDraftLocation | null {
  const lineColumnMatch = detail.match(LINE_COLUMN_PATTERN);
  if (lineColumnMatch) {
    const line = Number.parseInt(lineColumnMatch[1], 10);
    const column = Number.parseInt(lineColumnMatch[2], 10);

    if (Number.isFinite(line) && Number.isFinite(column)) {
      return { line, column };
    }
  }

  const positionMatch = detail.match(POSITION_PATTERN);
  if (!positionMatch) {
    return null;
  }

  const index = Number.parseInt(positionMatch[1], 10);
  if (!Number.isFinite(index) || index < 0) {
    return null;
  }

  return locationFromIndex(text, index);
}
