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
  resultType?: string | null;
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

/** Columns a formula can use: everything except JSON (computed columns included). */
export function usableColumns(columns: FormulaColumn[]): FormulaColumn[] {
  return columns.filter((column) => column.type !== "json");
}

/** A link a formula can follow with a dot, with the columns of the table it leads to. */
export type FormulaLink = {
  /** The name suggested first: the accessor or the reference column. */
  name: string;
  /** Every name that works: accessor, reference column, target table. */
  names: string[];
  target: string;
  /** Several rows (a relation): only usable in SUM, COUNT, AVG, MIN, MAX. */
  toMany: boolean;
  columns: FormulaColumn[];
};

type LinkModel = {
  tables: { name: string; columns: (FormulaColumn & { references?: unknown })[] }[];
  relations: {
    name: string;
    type: string;
    from: string;
    to: string;
    column?: string | null;
    fromAccessor?: string | null;
    toAccessor?: string | null;
  }[];
};

const same = (a: string | null | undefined, b: string | null | undefined) =>
  (a ?? "").toLowerCase() === (b ?? "").toLowerCase();

/** "account_id" → "account" (like the server's default accessor). */
function referenceAccessor(column: string): string {
  return column.toLowerCase().endsWith("_id") && column.length > 3 ? column.slice(0, -3) : column;
}

/**
 * The links of a table, mirroring the server: reference columns (one row),
 * 1:N relations from the table and N:M relations (several rows).
 */
export function formulaLinks(model: LinkModel, tableName: string): FormulaLink[] {
  const table = model.tables.find((t) => same(t.name, tableName));
  if (!table) return [];
  const columnsOf = (name: string) => model.tables.find((t) => same(t.name, name))?.columns ?? [];
  const links: FormulaLink[] = [];

  for (const column of table.columns) {
    if (column.type !== "reference" || typeof column.references !== "string") continue;
    const target = column.references;
    if (!model.tables.some((t) => same(t.name, target))) continue;
    const relation = model.relations.find((r) => r.type === "1:N" && same(r.to, table.name) && same(r.column, column.name));
    const accessor = relation ? relation.toAccessor || referenceAccessor(column.name) : null;
    const names = accessor && !same(accessor, column.name) ? [column.name, accessor] : [column.name];
    links.push({ name: names[names.length - 1], names, target, toMany: false, columns: columnsOf(target) });
  }

  for (const relation of model.relations) {
    if (relation.type === "1:N" && same(relation.from, table.name) && relation.column) {
      const accessor = relation.fromAccessor || relation.name;
      links.push({ name: accessor, names: [accessor, relation.to], target: relation.to, toMany: true, columns: columnsOf(relation.to) });
    }
    if (relation.type === "N:M" && same(relation.from, table.name)) {
      const accessor = relation.fromAccessor || relation.to;
      links.push({ name: accessor, names: [accessor, relation.to], target: relation.to, toMany: true, columns: columnsOf(relation.to) });
    }
    if (relation.type === "N:M" && same(relation.to, table.name)) {
      const accessor = relation.toAccessor || relation.from;
      links.push({ name: accessor, names: [accessor, relation.from], target: relation.from, toMany: true, columns: columnsOf(relation.from) });
    }
  }

  // A table name stands for a link only when exactly one link leads there.
  const counts = new Map<string, number>();
  for (const link of links) {
    if (link.toMany) counts.set(link.target.toLowerCase(), (counts.get(link.target.toLowerCase()) ?? 0) + 1);
  }
  return links.map((link) =>
    link.toMany && ((counts.get(link.target.toLowerCase()) ?? 0) > 1 || table.columns.some((c) => same(c.name, link.target)))
      ? { ...link, names: link.names.filter((name, index) => index === 0 || !same(name, link.target)) }
      : { ...link, names: [...new Set(link.names)] },
  );
}

/**
 * Completions at the cursor: columns, links and functions for a bare word,
 * the target's columns after "link.", display names after "[".
 */
export function formulaCompletions(
  columns: FormulaColumn[],
  functions: FormulaFunction[],
  links: FormulaLink[] = [],
): (context: CompletionContext) => CompletionResult | null {
  const usable = usableColumns(columns);

  return (context) => {
    const path = context.matchBefore(/[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_]?[A-Za-z0-9_]*$/);
    if (path) {
      const [linkName] = path.text.split(".");
      const link = links.find((candidate) => candidate.names.some((name) => same(name, linkName)));
      if (!link) return null;
      return {
        from: path.from + linkName.length + 1,
        options: usableColumns(link.columns).map((column) => ({
          label: column.name,
          type: "variable",
          detail: `${link.target} · ${column.type === "computed" ? column.resultType ?? "computed" : column.type}`,
          info: column.displayName && column.displayName !== column.name ? column.displayName : undefined,
        })),
        validFor: /^[A-Za-z0-9_]*$/,
      };
    }

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
        detail: column.type === "computed" ? `computed${column.resultType ? ` · ${column.resultType}` : ""}` : column.type,
        info: column.displayName && column.displayName !== column.name ? column.displayName : undefined,
        boost: 1,
      })),
      ...links
        .filter((link) => link.toMany || !usable.some((column) => same(column.name, link.name)))
        .map((link) => ({
          label: link.name,
          type: "class",
          detail: link.toMany ? `→ ${link.target} (several rows)` : `→ ${link.target}`,
          info: link.toMany
            ? `Related rows: SUM(${link.name}.column), COUNT(${link.name}), AVG, MIN, MAX`
            : `A value of the linked ${link.target} row: ${link.name}.column`,
          apply: `${link.name}.`,
        })),
      ...functions.map((fn) => ({
        label: fn.name,
        type: "function",
        detail: fn.signature,
        info: `${fn.description}\nExample: ${fn.example}`,
        apply: fn.apply ?? `${fn.name}(`,
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
