import type { BudgetConfig } from "../../api/dashboards";
import type { AggregationQuery, QueryResult } from "../../api/query";
import { valueTypeOf } from "../../lib/columns";
import { toNumber } from "../../lib/format";
import type { ModelSnapshot, ModelTable } from "../../types/api";
import { getReferenceTableName } from "../data-explorer/components/editors/referenceMetadata";
import type { ResolvedRange } from "./dateRange";
import { globalFilterFor, mergeFilters } from "./dashboardFilters";
import { cashFlowColumns } from "./starter";
import { rangeFilter } from "./widgetData";

// Step 4.5: budgets — a monthly limit per category (a column of the
// categories table) against what was spent in the dashboard's range.

const NUMERIC = new Set(["currency", "float", "integer", "decimal", "double"]);
const LIMIT = /budget|limit|plan|ziel/i;

/** The table the category column links to. */
export function limitTable(config: BudgetConfig, snapshot: ModelSnapshot | null): ModelTable | undefined {
  const source = snapshot?.tables.find((t) => t.name === config.source);
  const column = source?.columns.find((c) => c.name === config.category);
  const target = column ? getReferenceTableName(column) : null;
  return snapshot?.tables.find((t) => t.name === target);
}

/** Number columns of the categories table that can hold a monthly limit; a column named like a budget first. */
export function limitColumns(table: ModelTable | undefined) {
  const numbers = (table?.columns ?? []).filter((c) => !c.primaryKey && NUMERIC.has(valueTypeOf(c)));
  return [...numbers.filter((c) => LIMIT.test(`${c.name} ${c.displayName ?? ""}`)), ...numbers.filter((c) => !LIMIT.test(`${c.name} ${c.displayName ?? ""}`))];
}

/**
 * A first configuration from the tables: a spending table with a date, an
 * amount and a link to categories, and a number column there for the limit.
 */
export function defaultBudget(tables: ModelTable[], snapshot: ModelSnapshot | null): BudgetConfig | undefined {
  for (const table of tables) {
    const columns = cashFlowColumns(table);
    if (!columns?.category || columns.category.type !== "reference") continue;
    const config: BudgetConfig = { source: table.name, amount: columns.amount.name, date: columns.date.name, category: columns.category.name, limit: "", expenses: "negative", period: "month" };
    return { ...config, limit: limitColumns(limitTable(config, snapshot))[0]?.name ?? "" };
  }
  return undefined;
}

const pad = (n: number) => String(n).padStart(2, "0");

/**
 * The period a budget compares: the last month of the range (default), or
 * the whole range with its number of calendar months (to is exclusive). An
 * open range ("all time") counts as this month.
 */
export function budgetMonths(range: ResolvedRange, period: BudgetConfig["period"] = "month", today = new Date()): { from: string; to: string; months: number; month: string | null } {
  const monthOf = (year: number, month: number) => {
    const start = new Date(Date.UTC(year, month, 1));
    const next = new Date(Date.UTC(year, month + 1, 1));
    const iso = (d: Date) => `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-01`;
    return { from: iso(start), to: iso(next), months: 1, month: iso(start).slice(0, 7) };
  };
  if (!range.to) return monthOf(today.getFullYear(), today.getMonth());
  const last = new Date(Date.parse(`${range.to}T00:00:00Z`) - 86_400_000);
  if (period !== "range" || !range.from) return monthOf(last.getUTCFullYear(), last.getUTCMonth());
  const [fy, fm] = range.from.split("-").map(Number);
  const months = (last.getUTCFullYear() - fy!) * 12 + (last.getUTCMonth() + 1 - fm!) + 1;
  return { from: range.from, to: range.to, months: Math.max(1, months), month: null };
}

/** What was spent per category in the range: the expenses (by sign) grouped by the category link. */
export function spentQuery(config: BudgetConfig, range: ResolvedRange): AggregationQuery {
  const { from, to } = budgetMonths(range, config.period);
  const sign = config.expenses === "positive" ? { gt: 0 } : { lt: 0 };
  return {
    source: config.source,
    filter: mergeFilters({ [config.amount]: sign }, mergeFilters(rangeFilter(config.date, { ...range, from, to }), globalFilterFor(config.source, range))),
    groupBy: [{ column: config.category }],
    measures: [{ name: "spent", fn: "sum", column: config.amount }],
  };
}

export type BudgetRow = { id: unknown; label: string; spent: number; limit: number | null; share: number | null };

/**
 * One row per category with a limit (scaled to the months of the range),
 * most used first; categories with spending but no limit are summed apart.
 */
export function budgetRows(
  result: QueryResult | null,
  categories: Record<string, unknown>[],
  config: BudgetConfig,
  displayColumn: string,
  keyColumn: string,
  months: number,
): { rows: BudgetRow[]; unbudgeted: { spent: number; count: number }; total: { spent: number; limit: number } } {
  const spentBy = new Map<string, number>();
  for (const row of result?.data ?? []) {
    const value = toNumber(row.spent) ?? 0;
    spentBy.set(String(row[config.category] ?? ""), Math.abs(value));
  }
  const rows: BudgetRow[] = [];
  const seen = new Set<string>();
  for (const category of categories) {
    const key = String(category[keyColumn]);
    const monthly = toNumber(category[config.limit] as never);
    if (monthly === null || monthly <= 0) continue;
    seen.add(key);
    const limit = monthly * months;
    const spent = spentBy.get(key) ?? 0;
    rows.push({ id: category[keyColumn], label: String(category[displayColumn] ?? key), spent, limit, share: spent / limit });
  }
  rows.sort((a, b) => (b.share ?? 0) - (a.share ?? 0) || a.label.localeCompare(b.label));
  let unbudgetedSpent = 0;
  let count = 0;
  for (const [key, spent] of spentBy) {
    if (seen.has(key) || spent === 0) continue;
    unbudgetedSpent += spent;
    count++;
  }
  const total = rows.reduce((sum, row) => ({ spent: sum.spent + row.spent, limit: sum.limit + (row.limit ?? 0) }), { spent: 0, limit: 0 });
  return { rows, unbudgeted: { spent: unbudgetedSpent, count }, total };
}

/** Under 80 % fine, up to 100 % close, above over. */
export function budgetState(share: number | null): "ok" | "close" | "over" {
  if (share === null) return "ok";
  if (share > 1) return "over";
  return share >= 0.8 ? "close" : "ok";
}
