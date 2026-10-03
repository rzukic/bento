import type {
  Completion,
  CompletionContext,
  CompletionResult,
} from "@codemirror/autocomplete";
import { HighlightStyle, StreamLanguage, type StringStream } from "@codemirror/language";
import { tags } from "@lezer/highlight";
import type { FormulaFunction } from "../../api/formulas";

/**
 * Highlighting and autocomplete for formulas. The C# parser stays the only
 * parser (Part 5 R5.8): this tokenizer only colours text, and errors come
 * from the validate endpoint.
 */

export type FormulaColumn = {
  name: string;
  displayName?: string | null;
  type: string;
};

const keywords = new Set(["AND", "OR", "NOT"]);
const atoms = new Set(["TRUE", "FALSE", "NULL"]);

type FormulaTokenState = { inString: string | null };

/** One token; a free function because CodeMirror calls it without `this`. */
function readFormulaToken(stream: StringStream, state: FormulaTokenState): string | null {
  if (state.inString) {
    while (!stream.eol()) {
      const char = stream.next();
      if (char === state.inString) {
        if (stream.peek() === state.inString) {
          stream.next();
          continue;
        }
        state.inString = null;
        break;
      }
    }
    return "string";
  }

  if (stream.eatSpace()) return null;

  const char = stream.peek();
  if (char === '"' || char === "'") {
    stream.next();
    state.inString = char;
    return readFormulaToken(stream, state);
  }
  if (char === "[") {
    if (stream.skipTo("]")) stream.next();
    else stream.skipToEnd();
    return "variableName.special";
  }
  if (stream.match(/^\d+(\.\d+)?/) || stream.match(/^\.\d+/)) return "number";
  if (stream.match(/^[A-Za-z_][A-Za-z0-9_]*/)) {
    const word = stream.current().toUpperCase();
    if (stream.match(/^\s*\(/, false)) return "function";
    if (keywords.has(word)) return "keyword";
    if (atoms.has(word)) return "atom";
    return "variableName";
  }
  if (stream.match(/^(==|!=|<>|<=|>=|&&|\|\||[=<>&+\-*/%^!])/)) return "operator";
  if (stream.match(/^[(),;.]/)) return "punctuation";
  stream.next();
  return null;
}

export const formulaLanguage = StreamLanguage.define<FormulaTokenState>({
  name: "bento-formula",
  startState: () => ({ inString: null }),
  copyState: (state) => ({ ...state }),
  token: readFormulaToken,
  tokenTable: {
    function: tags.function(tags.variableName),
    "variableName.special": tags.special(tags.variableName),
    punctuation: tags.punctuation,
  },
});

export const formulaHighlightStyle = HighlightStyle.define([
  { tag: tags.string, class: "formula-tok-string" },
  { tag: tags.number, class: "formula-tok-number" },
  { tag: tags.keyword, class: "formula-tok-keyword" },
  { tag: tags.atom, class: "formula-tok-keyword" },
  { tag: tags.function(tags.variableName), class: "formula-tok-function" },
  { tag: tags.special(tags.variableName), class: "formula-tok-column" },
  { tag: tags.variableName, class: "formula-tok-column" },
  { tag: tags.operator, class: "formula-tok-operator" },
]);

/** Columns a formula can use: everything with a value of its own. */
export function usableColumns(columns: FormulaColumn[]): FormulaColumn[] {
  return columns.filter((column) => column.type !== "computed" && column.type !== "json");
}

/**
 * Completions at the cursor: columns and functions for a bare word,
 * display names after "[".
 */
export function formulaCompletions(
  columns: FormulaColumn[],
  functions: FormulaFunction[],
): (context: CompletionContext) => CompletionResult | null {
  const usable = usableColumns(columns);

  return (context) => {
    const bracket = context.matchBefore(/\[[^\]]*$/);
    if (bracket) {
      return {
        from: bracket.from,
        options: usable.map((column) => {
          const label = `[${column.displayName?.trim() || column.name}]`;
          return { label, type: "variable", detail: column.type, apply: label };
        }),
        validFor: /^\[[^\]]*$/,
      };
    }

    const word = context.matchBefore(/[A-Za-z_][A-Za-z0-9_]*/);
    if (!word && !context.explicit) return null;
    // Not inside a string literal.
    const before = context.state.sliceDoc(0, context.pos);
    if (isInsideString(before)) return null;

    const options: Completion[] = [
      ...usable.map((column) => ({
        label: column.name,
        type: "variable",
        detail: column.type,
        info: column.displayName && column.displayName !== column.name ? column.displayName : undefined,
        boost: 1,
      })),
      ...functions.map((fn) => ({
        label: fn.name,
        type: "function",
        detail: fn.signature,
        info: `${fn.description}\nExample: ${fn.example}`,
        apply: `${fn.name}(`,
      })),
    ];
    return { from: word?.from ?? context.pos, options, validFor: /^[A-Za-z_][A-Za-z0-9_]*$/ };
  };
}

function isInsideString(text: string): boolean {
  let quote: string | null = null;
  for (let i = 0; i < text.length; i++) {
    const char = text[i];
    if (quote) {
      if (char === quote) {
        if (text[i + 1] === quote) i++;
        else quote = null;
      }
    } else if (char === '"' || char === "'") {
      quote = char;
    } else if (char === "[") {
      const close = text.indexOf("]", i);
      if (close < 0) return false;
      i = close;
    }
  }
  return quote !== null;
}

/** Describes a result type for the status line: "Gives an amount". */
export function describeFormulaType(type: string): string {
  switch (type) {
    case "text":
      return "text";
    case "integer":
      return "a whole number";
    case "float":
      return "a number";
    case "currency":
      return "an amount";
    case "boolean":
      return "yes/no";
    case "date":
      return "a date";
    case "datetime":
      return "a date and time";
    default:
      return type;
  }
}
