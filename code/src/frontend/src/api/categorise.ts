import { apiClient } from "./client";

type DataEnvelope<T> = { data: T };

/** What a link or choice column can hold: a record (by id) or a choice, with its parent as group. */
export type CategoryOption = { value: string; label: string; group: string | null };

export type CategorySource = "rule" | "history" | "ai";

export type CategorySuggestion = {
  key: string;
  value: string;
  label: string;
  source: CategorySource;
  confidence: number | null;
  /** The stable part of the text that decides it, e.g. "REWE" — what a rule would match. */
  keyword: string | null;
  matchColumn: string | null;
  ruleId: number | null;
};

export type CategoriseResult = {
  column: string;
  options: CategoryOption[];
  /** The row's text columns that rules can match, in table order. */
  matchColumns: string[];
  suggestions: CategorySuggestion[];
  ai: { model: string; costUsd: number; rows: number; durationMs: number } | null;
  /** Why the AI did not run (not set up, budget used up, …); rules and earlier rows still apply. */
  aiError: string | null;
};

export type CategoriseRow = { key: string; values: Record<string, string | null> };

export type CategoryRule = {
  id: number;
  tableName: string;
  column: string;
  matchColumn: string;
  pattern: string;
  value: string;
  label: string | null;
  createdAt: string;
};

export type CategoryRuleInput = { column: string; matchColumn: string; pattern: string; value: string };

const tablePath = (table: string) => `/categorise/${encodeURIComponent(table)}`;

/** POST /api/v1/categorise/{table}/suggest — rules, then earlier rows, then (with `ai`) Bento's AI (Step 4.8). */
export async function suggestCategories(table: string, body: { column: string; rows: CategoriseRow[]; ai?: boolean }): Promise<CategoriseResult> {
  return (await apiClient.post<DataEnvelope<CategoriseResult>>(`${tablePath(table)}/suggest`, body)).data;
}

export async function listCategoryRules(table: string, column?: string): Promise<CategoryRule[]> {
  const query = column ? `?column=${encodeURIComponent(column)}` : "";
  return (await apiClient.get<DataEnvelope<CategoryRule[]>>(`${tablePath(table)}/rules${query}`)).data;
}

/** Saves rules; the same keyword for the same column replaces the old rule. Returns all rules of the table. */
export async function saveCategoryRules(table: string, rules: CategoryRuleInput[]): Promise<CategoryRule[]> {
  return (await apiClient.post<DataEnvelope<CategoryRule[]>>(`${tablePath(table)}/rules`, { rules })).data;
}

export async function deleteCategoryRule(table: string, id: number): Promise<void> {
  await apiClient.delete<void>(`${tablePath(table)}/rules/${id}`);
}
