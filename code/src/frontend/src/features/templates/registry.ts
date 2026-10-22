import { financeNames, type FinanceNames } from "../dashboards/financeTemplate";
import { appointmentsTemplate } from "./appointmentsTemplate";
import { habitsTemplate } from "./habitsTemplate";
import { installFinance, installSimple, type InstallOptions } from "./install";
import type { TemplateNames } from "./names";
import { projectsTemplate } from "./projectsTemplate";
import type { SimpleTemplate } from "./types";

/**
 * The templates Bento offers: each sets up new tables (nothing existing
 * changes), dashboards and forms for one use case, optionally with example
 * rows, and is noted for the workspace so the AI learns its tables. Bento
 * itself is not about any of them.
 */
export type TemplateEntry = {
  id: string;
  title: string;
  description: string;
  base: TemplateNames;
  prefixes: string[];
  /** Sets it up with these table names; returns the dashboard to open. */
  install(names: TemplateNames, options: InstallOptions): Promise<number | undefined>;
};

const simple = (template: SimpleTemplate): TemplateEntry => ({
  id: template.id,
  title: template.title,
  description: template.description,
  base: template.base,
  prefixes: template.prefixes,
  install: (names, options) => installSimple(template, names, options),
});

export const financeEntry: TemplateEntry = {
  id: "finance",
  title: "Wealth & cash flow",
  description:
    "Accounts, categories with budgets, transactions, depot trades, prices and dividends, and two dashboards: cash flow with an account filter, and wealth — net worth over time, depot value, realised and unrealised gains, returns, allocation, positions and dividends. The function avg_cost is added for realised gains unless you have it, and a form, Add transaction, for quick entry on the phone.",
  base: financeNames("") as unknown as TemplateNames,
  prefixes: ["finance_", "my_", "fin_"],
  install: (names, options) => installFinance(names as unknown as FinanceNames, options),
};

export const TEMPLATES: TemplateEntry[] = [simple(projectsTemplate), simple(habitsTemplate), simple(appointmentsTemplate), financeEntry];
