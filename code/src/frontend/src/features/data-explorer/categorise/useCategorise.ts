import { useCallback, useEffect, useRef, useState } from "react";
import {
  deleteCategoryRule,
  listCategoryRules,
  saveCategoryRules,
  suggestCategories,
  type CategoriseRow,
  type CategoryOption,
  type CategoryRule,
  type CategoryRuleInput,
  type CategorySource,
  type CategorySuggestion,
} from "../../../api/categorise";
import { ApiClientError } from "../../../api/client";
import type { ModelColumn, ModelTable } from "../../../types/api";

/** A row's value: suggested (rule, earlier rows, AI) or set by the user. */
export type CategoryChoice = {
  value: string;
  label: string;
  source: CategorySource | "you";
  confidence: number | null;
  keyword: string | null;
  matchColumn: string | null;
  /** For the user's own choice: save it as a rule ("REWE" → Groceries) after importing. */
  remember: boolean;
};

const CATEGORY_NAME = /categ|kategor/i;
/** AI choices at least this sure become rules on their keyword, when the user keeps them. */
export const SURE = 0.8;
/** The backend takes at most 500 rows per request. */
const CHUNK = 500;

async function suggestInChunks(table: string, column: string, rows: CategoriseRow[], ai: boolean) {
  const results = [];
  for (let start = 0; start < rows.length || start === 0; start += CHUNK) {
    results.push(await suggestCategories(table, { column, rows: rows.slice(start, start + CHUNK), ...(ai ? { ai: true } : {}) }));
    if (rows.length === 0) break;
  }
  const last = results[results.length - 1];
  const costs = results.map((r) => r.ai?.costUsd ?? 0);
  return {
    ...last,
    suggestions: results.flatMap((r) => r.suggestions),
    ai: results.some((r) => r.ai) ? { ...(results.find((r) => r.ai)!.ai!), costUsd: costs.reduce((a, b) => a + b, 0) } : null,
    aiError: results.map((r) => r.aiError).find(Boolean) ?? null,
  };
}

/** Link and choice columns a value can be suggested for. */
export function categorisableColumns(table: ModelTable | null): ModelColumn[] {
  return (table?.columns ?? []).filter(
    (c) => !c.primaryKey && !c.formula && ((c.type === "reference" && Boolean(c.references)) || (c.type === "enum" && (c.values?.length ?? 0) > 0)),
  );
}

/** The column to categorise by default: one named like a category. */
export function defaultCategoryColumn(table: ModelTable | null): string {
  return categorisableColumns(table).find((c) => CATEGORY_NAME.test(c.name) || CATEGORY_NAME.test(c.displayName ?? ""))?.name ?? "";
}

function message(cause: unknown, fallback: string): string {
  return cause instanceof ApiClientError ? cause.message : fallback;
}

/**
 * Categorising rows (Step 4.8), shared by the import review and the explorer:
 * rules and earlier rows first, Bento's AI for the rest on request; the
 * user's corrections become rules.
 */
export function useCategorise(table: string, column: string) {
  const [options, setOptions] = useState<CategoryOption[]>([]);
  const [matchColumns, setMatchColumns] = useState<string[]>([]);
  const [choices, setChoices] = useState<Record<string, CategoryChoice>>({});
  const [busy, setBusy] = useState<"rules" | "ai" | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [aiCost, setAiCost] = useState(0);
  const [rules, setRules] = useState<CategoryRule[]>([]);
  const [rememberAi, setRememberAi] = useState(true);
  const generation = useRef(0);
  const rowsRef = useRef<Record<string, CategoriseRow["values"]>>({});

  const loadRules = useCallback(async () => {
    if (!column) return;
    try {
      setRules(await listCategoryRules(table, column));
    } catch {
      setRules([]);
    }
  }, [table, column]);

  useEffect(() => {
    void loadRules();
  }, [loadRules]);

  const reset = useCallback(() => {
    generation.current += 1;
    setChoices({});
    setError(null);
    setAiCost(0);
    setBusy(null);
    rowsRef.current = {};
  }, []);

  useEffect(reset, [table, column, reset]);

  const apply = (suggestions: CategorySuggestion[]) =>
    setChoices((current) => {
      const next = { ...current };
      for (const s of suggestions) {
        if (next[s.key]?.source === "you") continue;
        next[s.key] = { value: s.value, label: s.label, source: s.source, confidence: s.confidence, keyword: s.keyword, matchColumn: s.matchColumn, remember: false };
      }
      return next;
    });

  /** Rules and earlier rows for these rows — no AI, no cost. */
  const start = useCallback(
    async (rows: CategoriseRow[]) => {
      generation.current += 1;
      setError(null);
      // The user's own choices survive checking the rows again.
      const keys = new Set(rows.map((r) => r.key));
      setChoices((current) => Object.fromEntries(Object.entries(current).filter(([key, choice]) => choice.source === "you" && keys.has(key))));
      if (!column) return;
      const run = generation.current;
      rowsRef.current = Object.fromEntries(rows.map((r) => [r.key, r.values]));
      setBusy("rules");
      try {
        const result = await suggestInChunks(table, column, rows, false);
        if (run !== generation.current) return;
        setOptions(result.options);
        setMatchColumns(result.matchColumns);
        apply(result.suggestions);
      } catch (cause) {
        if (run === generation.current) setError(message(cause, "Categories could not be suggested."));
      } finally {
        if (run === generation.current) setBusy(null);
      }
    },
    [table, column],
  );

  /** Asks the AI for the rows that have no value yet. */
  const suggestRest = useCallback(
    async (rows: CategoriseRow[]) => {
      if (!column || rows.length === 0) return;
      const run = generation.current;
      setBusy("ai");
      setError(null);
      try {
        const result = await suggestInChunks(table, column, rows, true);
        if (run !== generation.current) return;
        setOptions(result.options);
        apply(result.suggestions);
        if (result.ai) setAiCost((cost) => cost + result.ai!.costUsd);
        if (result.aiError) setError(result.aiError);
      } catch (cause) {
        if (run === generation.current) setError(message(cause, "The AI could not suggest categories."));
      } finally {
        if (run === generation.current) setBusy(null);
      }
    },
    [table, column],
  );

  /** The user's own choice; it becomes a rule on the row's keyword unless they untick it. */
  const choose = useCallback(
    (key: string, value: string) => {
      setChoices((current) => {
        const next = { ...current };
        if (!value) {
          delete next[key];
          return next;
        }
        const option = options.find((o) => o.value === value);
        const before = current[key];
        const values = rowsRef.current[key] ?? {};
        const matchColumn = before?.keyword ? before.matchColumn : (matchColumns.find((c) => values[c]) ?? null);
        const keyword = before?.keyword ?? (matchColumn ? (values[matchColumn] ?? null) : null);
        next[key] = { value, label: option?.label ?? value, source: "you", confidence: null, keyword, matchColumn, remember: Boolean(keyword && matchColumn) };
        return next;
      });
    },
    [options, matchColumns],
  );

  const setRemember = useCallback((key: string, remember: boolean) => {
    setChoices((current) => (current[key] ? { ...current, [key]: { ...current[key], remember } } : current));
  }, []);

  /** Saves the remembered corrections as rules; returns what was saved. */
  const saveRules = useCallback(
    async (keys?: string[]): Promise<CategoryRuleInput[]> => {
      const wanted = new Set(keys ?? Object.keys(choices));
      const byPattern = new Map<string, CategoryRuleInput>();
      for (const [key, choice] of Object.entries(choices)) {
        if (!wanted.has(key) || !choice.keyword || !choice.matchColumn) continue;
        const sureAi = rememberAi && choice.source === "ai" && (choice.confidence ?? 0) >= SURE;
        if (!(choice.source === "you" && choice.remember) && !sureAi) continue;
        byPattern.set(`${choice.matchColumn}\u001f${choice.keyword.toLowerCase()}`, { column, matchColumn: choice.matchColumn, pattern: choice.keyword, value: choice.value });
      }
      const inputs = [...byPattern.values()];
      if (inputs.length === 0) return [];
      setRules((await saveCategoryRules(table, inputs)).filter((r) => r.column === column));
      return inputs;
    },
    [choices, table, column, rememberAi],
  );

  const removeRule = useCallback(
    async (id: number) => {
      await deleteCategoryRule(table, id);
      setRules((current) => current.filter((r) => r.id !== id));
    },
    [table],
  );

  return { options, matchColumns, choices, busy, error, aiCost, rules, rememberAi, setRememberAi, start, suggestRest, choose, setRemember, saveRules, removeRule, reset };
}

export type Categorising = ReturnType<typeof useCategorise>;
