import { locationFromIndex, type JsonTextLocation } from "./jsonTextPosition";

type JsonPathToken = number | string;

type JsonObjectProperty = {
  key: string;
  keyStart: number;
  value: JsonSourceNode;
};

type JsonObjectNode = {
  type: "object";
  start: number;
  properties: Map<string, JsonObjectProperty>;
};

type JsonArrayNode = {
  type: "array";
  start: number;
  items: JsonSourceNode[];
};

type JsonScalarNode = {
  type: "literal" | "number" | "string";
  start: number;
};

type JsonSourceNode = JsonArrayNode | JsonObjectNode | JsonScalarNode;

export type JsonPathResolution = {
  exact: boolean;
  index: number;
  location: JsonTextLocation;
  path: string;
  resolvedPath: string;
};

export function resolveJsonPathLocation(text: string, path: string): JsonPathResolution | null {
  const trimmedPath = path.trim();
  if (!trimmedPath) {
    return null;
  }

  try {
    const root = new JsonSourceParser(text).parse();
    const tokens = tokenizeJsonPath(trimmedPath);
    let current = root;
    let resolvedPath = "";
    let targetIndex = root.start;

    if (trimmedPath === "schema") {
      return {
        exact: true,
        index: targetIndex,
        location: locationFromIndex(text, targetIndex),
        path: trimmedPath,
        resolvedPath: trimmedPath,
      };
    }

    for (const token of tokens) {
      if (typeof token === "number") {
        if (current.type !== "array") {
          return buildResolution(text, trimmedPath, resolvedPath, targetIndex, false);
        }

        const item = current.items[token];
        if (!item) {
          return buildResolution(text, trimmedPath, resolvedPath, current.start, false);
        }

        current = item;
        targetIndex = item.start;
        resolvedPath = appendResolvedPath(resolvedPath, token);
        continue;
      }

      if (current.type !== "object") {
        return buildResolution(text, trimmedPath, resolvedPath, targetIndex, false);
      }

      const property = current.properties.get(token);
      if (!property) {
        return buildResolution(text, trimmedPath, resolvedPath, current.start, false);
      }

      current = property.value;
      targetIndex = property.keyStart;
      resolvedPath = appendResolvedPath(resolvedPath, token);
    }

    return buildResolution(text, trimmedPath, resolvedPath, targetIndex, true);
  } catch {
    return null;
  }
}

function buildResolution(
  text: string,
  path: string,
  resolvedPath: string,
  index: number,
  exact: boolean,
): JsonPathResolution {
  return {
    exact,
    index,
    location: locationFromIndex(text, index),
    path,
    resolvedPath: resolvedPath || "schema",
  };
}

function appendResolvedPath(base: string, token: JsonPathToken): string {
  if (typeof token === "number") {
    return `${base}[${token}]`;
  }

  return base ? `${base}.${token}` : token;
}

function tokenizeJsonPath(path: string): JsonPathToken[] {
  if (path === "schema") {
    return [];
  }

  const tokens: JsonPathToken[] = [];
  const pattern = /([^[.\]]+)|\[(\d+)\]/g;

  for (const match of path.matchAll(pattern)) {
    if (match[1]) {
      tokens.push(match[1]);
      continue;
    }

    if (match[2]) {
      tokens.push(Number.parseInt(match[2], 10));
    }
  }

  return tokens;
}

class JsonSourceParser {
  private index = 0;

  constructor(private readonly text: string) {}

  parse(): JsonSourceNode {
    const value = this.parseValue();
    this.skipWhitespace();

    if (this.index !== this.text.length) {
      throw new Error("Unexpected trailing JSON content.");
    }

    return value;
  }

  private parseValue(): JsonSourceNode {
    this.skipWhitespace();
    const start = this.index;
    const current = this.text[this.index];

    if (current === "{") {
      return this.parseObject();
    }

    if (current === "[") {
      return this.parseArray();
    }

    if (current === "\"") {
      this.readString();
      return { type: "string", start };
    }

    if (current === "-" || isDigit(current)) {
      this.readNumber();
      return { type: "number", start };
    }

    if (current === "t") {
      this.expectLiteral("true");
      return { type: "literal", start };
    }

    if (current === "f") {
      this.expectLiteral("false");
      return { type: "literal", start };
    }

    if (current === "n") {
      this.expectLiteral("null");
      return { type: "literal", start };
    }

    throw new Error(`Unexpected JSON token at index ${this.index}.`);
  }

  private parseObject(): JsonObjectNode {
    const start = this.index;
    const properties = new Map<string, JsonObjectProperty>();
    this.index += 1;
    this.skipWhitespace();

    if (this.text[this.index] === "}") {
      this.index += 1;
      return { type: "object", start, properties };
    }

    while (this.index < this.text.length) {
      this.skipWhitespace();
      const keyStart = this.index;
      const key = this.readString();
      this.skipWhitespace();
      this.expectCharacter(":");
      this.index += 1;
      const value = this.parseValue();
      properties.set(key, { key, keyStart, value });
      this.skipWhitespace();

      if (this.text[this.index] === ",") {
        this.index += 1;
        continue;
      }

      this.expectCharacter("}");
      this.index += 1;
      break;
    }

    return { type: "object", start, properties };
  }

  private parseArray(): JsonArrayNode {
    const start = this.index;
    const items: JsonSourceNode[] = [];
    this.index += 1;
    this.skipWhitespace();

    if (this.text[this.index] === "]") {
      this.index += 1;
      return { type: "array", start, items };
    }

    while (this.index < this.text.length) {
      const value = this.parseValue();
      items.push(value);
      this.skipWhitespace();

      if (this.text[this.index] === ",") {
        this.index += 1;
        continue;
      }

      this.expectCharacter("]");
      this.index += 1;
      break;
    }

    return { type: "array", start, items };
  }

  private readString(): string {
    const start = this.index;
    this.expectCharacter("\"");
    this.index += 1;

    while (this.index < this.text.length) {
      const current = this.text[this.index];
      if (current === "\"") {
        this.index += 1;
        return JSON.parse(this.text.slice(start, this.index)) as string;
      }

      if (current === "\\") {
        const next = this.text[this.index + 1];
        this.index += next === "u" ? 6 : 2;
        continue;
      }

      this.index += 1;
    }

    throw new Error("Unterminated JSON string.");
  }

  private readNumber(): void {
    while (this.index < this.text.length) {
      const current = this.text[this.index];
      if (!/[0-9eE+\-.]/.test(current)) {
        return;
      }

      this.index += 1;
    }
  }

  private expectLiteral(value: string): void {
    if (this.text.slice(this.index, this.index + value.length) !== value) {
      throw new Error(`Expected literal '${value}'.`);
    }

    this.index += value.length;
  }

  private expectCharacter(value: string): void {
    if (this.text[this.index] !== value) {
      throw new Error(`Expected '${value}' at index ${this.index}.`);
    }
  }

  private skipWhitespace(): void {
    while (this.index < this.text.length && /\s/.test(this.text[this.index])) {
      this.index += 1;
    }
  }
}

function isDigit(value: string | undefined): boolean {
  return value !== undefined && /[0-9]/.test(value);
}
