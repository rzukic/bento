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
  if (positionMatch) {
    const index = Number.parseInt(positionMatch[1], 10);
    if (Number.isFinite(index) && index >= 0) {
      return locationFromIndex(text, index);
    }
  }

  const fallbackIndex = findJsonSyntaxErrorIndex(text);
  return fallbackIndex === null ? null : locationFromIndex(text, fallbackIndex);
}

class JsonSyntaxErrorWithIndex extends Error {
  constructor(readonly index: number) {
    super(`Invalid JSON at index ${index}.`);
  }
}

class JsonSyntaxLocator {
  private index = 0;

  constructor(private readonly text: string) {}

  parse(): void {
    this.parseValue();
    this.skipWhitespace();

    if (this.index !== this.text.length) {
      this.failAt(this.index);
    }
  }

  private parseValue(): void {
    this.skipWhitespace();

    if (this.index >= this.text.length) {
      this.failAt(this.index);
    }

    const current = this.text[this.index];
    if (current === "{") {
      this.parseObject();
      return;
    }

    if (current === "[") {
      this.parseArray();
      return;
    }

    if (current === "\"") {
      this.readString();
      return;
    }

    if (current === "-" || isDigit(current)) {
      this.readNumber();
      return;
    }

    if (current === "t") {
      this.expectLiteral("true");
      return;
    }

    if (current === "f") {
      this.expectLiteral("false");
      return;
    }

    if (current === "n") {
      this.expectLiteral("null");
      return;
    }

    this.failAt(this.index);
  }

  private parseObject(): void {
    this.expectCharacter("{");
    this.index += 1;
    this.skipWhitespace();

    if (this.text[this.index] === "}") {
      this.index += 1;
      return;
    }

    while (this.index < this.text.length) {
      this.skipWhitespace();
      if (this.text[this.index] !== "\"") {
        this.failAt(this.index);
      }

      this.readString();
      this.skipWhitespace();
      this.expectCharacter(":");
      this.index += 1;
      this.parseValue();
      this.skipWhitespace();

      if (this.text[this.index] === ",") {
        this.index += 1;
        continue;
      }

      this.expectCharacter("}");
      this.index += 1;
      return;
    }

    this.failAt(this.index);
  }

  private parseArray(): void {
    this.expectCharacter("[");
    this.index += 1;
    this.skipWhitespace();

    if (this.text[this.index] === "]") {
      this.index += 1;
      return;
    }

    while (this.index < this.text.length) {
      this.parseValue();
      this.skipWhitespace();

      if (this.text[this.index] === ",") {
        this.index += 1;
        continue;
      }

      this.expectCharacter("]");
      this.index += 1;
      return;
    }

    this.failAt(this.index);
  }

  private readString(): void {
    this.expectCharacter("\"");
    this.index += 1;

    while (this.index < this.text.length) {
      const current = this.text[this.index];
      if (current === "\"") {
        this.index += 1;
        return;
      }

      if (current === "\n" || current === "\r") {
        this.failAt(this.index);
      }

      if (current === "\\") {
        this.readEscapeSequence();
        continue;
      }

      this.index += 1;
    }

    this.failAt(this.text.length);
  }

  private readEscapeSequence(): void {
    this.index += 1;
    const escaped = this.text[this.index];

    if (!escaped) {
      this.failAt(this.index);
    }

    if (escaped === "u") {
      for (let offset = 1; offset <= 4; offset += 1) {
        const digit = this.text[this.index + offset];
        if (!digit || !/[0-9a-f]/i.test(digit)) {
          this.failAt(this.index + offset);
        }
      }

      this.index += 5;
      return;
    }

    if (!/["\\/bfnrt]/.test(escaped)) {
      this.failAt(this.index);
    }

    this.index += 1;
  }

  private readNumber(): void {
    if (this.text[this.index] === "-") {
      this.index += 1;
    }

    if (this.text[this.index] === "0") {
      this.index += 1;
    } else if (isDigit(this.text[this.index])) {
      while (isDigit(this.text[this.index])) {
        this.index += 1;
      }
    } else {
      this.failAt(this.index);
    }

    if (this.text[this.index] === ".") {
      this.index += 1;
      if (!isDigit(this.text[this.index])) {
        this.failAt(this.index);
      }

      while (isDigit(this.text[this.index])) {
        this.index += 1;
      }
    }

    if (this.text[this.index] === "e" || this.text[this.index] === "E") {
      this.index += 1;
      if (this.text[this.index] === "+" || this.text[this.index] === "-") {
        this.index += 1;
      }

      if (!isDigit(this.text[this.index])) {
        this.failAt(this.index);
      }

      while (isDigit(this.text[this.index])) {
        this.index += 1;
      }
    }
  }

  private expectLiteral(value: string): void {
    for (let offset = 0; offset < value.length; offset += 1) {
      if (this.text[this.index + offset] !== value[offset]) {
        this.failAt(this.index + offset);
      }
    }

    this.index += value.length;
  }

  private expectCharacter(value: string): void {
    if (this.text[this.index] !== value) {
      this.failAt(this.index);
    }
  }

  private skipWhitespace(): void {
    while (this.index < this.text.length && /\s/.test(this.text[this.index])) {
      this.index += 1;
    }
  }

  private failAt(index: number): never {
    throw new JsonSyntaxErrorWithIndex(Math.max(0, index));
  }
}

function findJsonSyntaxErrorIndex(text: string): number | null {
  try {
    new JsonSyntaxLocator(text).parse();
    return null;
  } catch (error) {
    return error instanceof JsonSyntaxErrorWithIndex ? error.index : null;
  }
}

function isDigit(value: string | undefined): boolean {
  return value !== undefined && /[0-9]/.test(value);
}
