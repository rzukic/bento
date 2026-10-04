import type { DashboardDocument, DashboardWidget } from "../../api/dashboards";
import type { ModelTable, SchemaColumn, SchemaModelDocument, SchemaRelation, SchemaTable } from "../../types/api";
import { cashFlowDashboard } from "./starter";

// Step 3.9: one click sets up personal finance — accounts, categories,
// transactions, depot trades, prices and dividends, a cash-flow dashboard
// with an account filter and a wealth dashboard (net worth over time, depot
// value, gains, allocation, positions, dividends). The formulas are the ones
// FinanceTemplateTests runs on SQLite and Postgres.

export type FinanceNames = {
  accounts: string;
  categories: string;
  transactions: string;
  trades: string;
  prices: string;
  dividends: string;
};

const BASE: FinanceNames = {
  accounts: "accounts",
  categories: "categories",
  transactions: "transactions",
  trades: "trades",
  prices: "prices",
  dividends: "dividends",
};

const LABELS: Record<keyof FinanceNames, string> = {
  accounts: "Accounts",
  categories: "Categories",
  transactions: "Transactions",
  trades: "Trades",
  prices: "Prices",
  dividends: "Dividends",
};

export function financeNames(prefix: string): FinanceNames {
  const clean = prefix.trim().toLowerCase().replace(/[^a-z0-9_]/g, "_");
  return Object.fromEntries(Object.entries(BASE).map(([key, name]) => [key, `${clean}${name}`])) as FinanceNames;
}

/** The template's table names that already exist. */
export function financeConflicts(names: FinanceNames, existing: { name: string }[]): string[] {
  const taken = new Set(existing.map((t) => t.name.toLowerCase()));
  return Object.values(names).filter((name) => taken.has(name.toLowerCase()));
}

/** A prefix that avoids every existing table: none if the plain names are free. */
export function suggestedPrefix(existing: { name: string }[]): string {
  if (financeConflicts(financeNames(""), existing).length === 0) return "";
  for (const prefix of ["finance_", "my_", "fin_"]) if (financeConflicts(financeNames(prefix), existing).length === 0) return prefix;
  return "finance2_";
}

const id: SchemaColumn = { name: "id", type: "integer", primaryKey: true };

/** "Transactions", or with a prefix "Finance transactions", so pickers tell them from existing tables. */
export function financeLabel(n: FinanceNames, key: keyof FinanceNames): string {
  const prefix = n[key].slice(0, n[key].length - BASE[key].length).replace(/_+$/, "").replace(/_/g, " ").trim();
  return prefix ? `${prefix.charAt(0).toUpperCase()}${prefix.slice(1)} ${LABELS[key].toLowerCase()}` : LABELS[key];
}

function tables(n: FinanceNames): SchemaTable[] {
  const label = (key: keyof FinanceNames) => financeLabel(n, key);
  return [
    {
      name: n.accounts,
      displayName: label("accounts"),
      columns: [
        id,
        { name: "name", displayName: "Name", type: "text", nullable: false, unique: true },
        { name: "kind", displayName: "Kind", type: "enum", values: ["giro", "savings", "depot", "credit", "cash"] },
        { name: "opened_on", displayName: "Opened on", type: "date" },
        { name: "opening_balance", displayName: "Opening balance", type: "currency" },
        { name: "balance", displayName: "Balance", type: "computed", formula: `COALESCE(opening_balance, 0) + SUM(${n.transactions}.amount)` },
      ],
    },
    {
      name: n.categories,
      displayName: label("categories"),
      columns: [
        id,
        { name: "name", displayName: "Name", type: "text", nullable: false, unique: true },
        { name: "kind", displayName: "Kind", type: "enum", values: ["income", "expense", "savings", "transfer"] },
      ],
    },
    {
      name: n.transactions,
      displayName: label("transactions"),
      columns: [
        id,
        { name: "date", displayName: "Date", type: "date", nullable: false },
        { name: "amount", displayName: "Amount", type: "currency", nullable: false },
        { name: "payee", displayName: "Payee", type: "text" },
        { name: "purpose", displayName: "Purpose", type: "text" },
        { name: "account_id", displayName: "Account", type: "reference", references: n.accounts },
        { name: "category_id", displayName: "Category", type: "reference", references: n.categories },
      ],
    },
    {
      name: n.trades,
      displayName: label("trades"),
      columns: [
        id,
        { name: "date", displayName: "Date", type: "date", nullable: false },
        { name: "isin", displayName: "ISIN", type: "text", nullable: false },
        { name: "name", displayName: "Name", type: "text" },
        { name: "kind", displayName: "Kind", type: "enum", values: ["buy", "sell"] },
        { name: "shares", displayName: "Shares", type: "float", nullable: false },
        { name: "price", displayName: "Price", type: "currency", nullable: false },
        { name: "fees", displayName: "Fees", type: "currency" },
        { name: "account_id", displayName: "Account", type: "reference", references: n.accounts },
        { name: "signed_shares", displayName: "Shares (±)", type: "computed", formula: 'IF(kind = "sell", -shares, shares)' },
        { name: "invested", displayName: "Invested", type: "computed", formula: "signed_shares * price + COALESCE(fees, 0)" },
        { name: "value", displayName: "Value today", type: "computed", formula: `signed_shares * LOOKUP_ASOF("${n.prices}", "isin", isin, "date", TODAY(), "price")` },
      ],
    },
    {
      name: n.prices,
      displayName: label("prices"),
      columns: [
        id,
        { name: "isin", displayName: "ISIN", type: "text", nullable: false },
        { name: "date", displayName: "Date", type: "date", nullable: false },
        { name: "price", displayName: "Price", type: "currency", nullable: false },
      ],
    },
    {
      name: n.dividends,
      displayName: label("dividends"),
      columns: [
        id,
        { name: "date", displayName: "Date", type: "date", nullable: false },
        { name: "isin", displayName: "ISIN", type: "text" },
        { name: "name", displayName: "Name", type: "text" },
        { name: "amount", displayName: "Amount", type: "currency", nullable: false },
        { name: "tax", displayName: "Tax", type: "currency" },
        { name: "account_id", displayName: "Account", type: "reference", references: n.accounts },
      ],
    },
  ];
}

function relations(n: FinanceNames, taken: Set<string>): SchemaRelation[] {
  const unique = (name: string) => {
    let candidate = name;
    for (let i = 2; taken.has(candidate.toLowerCase()); i++) candidate = `${name}_${i}`;
    taken.add(candidate.toLowerCase());
    return candidate;
  };
  return [
    { name: unique(`${n.accounts}_${n.transactions}`), type: "1:N", from: n.accounts, to: n.transactions, column: "account_id" },
    { name: unique(`${n.categories}_${n.transactions}`), type: "1:N", from: n.categories, to: n.transactions, column: "category_id" },
    { name: unique(`${n.accounts}_${n.trades}`), type: "1:N", from: n.accounts, to: n.trades, column: "account_id" },
    { name: unique(`${n.accounts}_${n.dividends}`), type: "1:N", from: n.accounts, to: n.dividends, column: "account_id" },
  ];
}

/** The current model with the finance tables added; nothing existing changes. */
export function withFinanceTables(current: SchemaModelDocument, n: FinanceNames): SchemaModelDocument {
  const taken = new Set(current.relations.map((r) => r.name.toLowerCase()));
  return { ...current, tables: [...current.tables, ...tables(n)], relations: [...current.relations, ...relations(n, taken)] };
}

const at = (x: number, y: number, w: number, h: number) => ({ x, y, w, h });

/** Net worth over time, depot value and gains, allocation, positions and dividends. */
export function wealthDashboard(n: FinanceNames): DashboardDocument {
  const t = n.transactions;
  const tr = n.trades;
  const widgets: DashboardWidget[] = [
    {
      id: "net-worth",
      type: "kpi",
      title: "Net worth",
      position: at(0, 0, 3, 2),
      data: { query: { source: n.accounts, measures: [{ name: "net_worth", formula: `SUM(balance + SUM(${tr}.value))` }] } },
      display: { format: "currency", compare: false },
    },
    {
      id: "depot-value",
      type: "kpi",
      title: "Depot value",
      position: at(3, 0, 3, 2),
      data: { query: { source: tr, measures: [{ name: "value", formula: "SUM(value)" }] } },
      display: { format: "currency", compare: false },
    },
    {
      id: "gain",
      type: "kpi",
      title: "Unrealised gain",
      position: at(6, 0, 3, 2),
      data: { query: { source: tr, measures: [{ name: "gain", formula: "SUM(value - invested)" }] } },
      display: { format: "currency", compare: false },
    },
    {
      id: "dividends",
      type: "kpi",
      title: "Dividends in the range",
      position: at(9, 0, 3, 2),
      data: { query: { source: n.dividends, measures: [{ name: "dividends", fn: "sum", column: "amount" }] }, dateColumn: "date" },
      display: { format: "currency" },
    },
    {
      id: "net-worth-over-time",
      type: "area",
      title: "Net worth over time",
      position: at(0, 2, 8, 5),
      data: {
        // Values at each month end: cash from the balances, the depot from shares × price at that time.
        asOf: true,
        query: {
          source: n.accounts,
          groupBy: [{ column: "opened_on", bucket: "month", as: "month" }],
          measures: [
            { name: "cash", formula: `SUM(COALESCE(opening_balance, 0) + SUM(${t}.amount WHERE ${t}.date <= BUCKET_END()))`, color: "blue" },
            {
              name: "depot",
              formula: `SUM(SUM(${tr}.signed_shares * LOOKUP_ASOF("${n.prices}", "isin", ${tr}.isin, "date", BUCKET_END(), "price") WHERE ${tr}.date <= BUCKET_END()))`,
              color: "teal",
            },
          ],
        },
      },
      display: { format: "currency", stacked: true, totalLine: true },
    },
    {
      id: "allocation",
      type: "donut",
      title: "Allocation",
      position: at(8, 2, 4, 5),
      data: { query: { source: tr, groupBy: [{ column: "name", top: 8, other: true }], measures: [{ name: "value", formula: "SUM(value)" }] } },
      display: { format: "currency" },
    },
    {
      id: "positions",
      type: "table",
      title: "Positions",
      position: at(0, 7, 8, 4),
      data: {
        query: {
          source: tr,
          groupBy: [{ column: "name" }],
          measures: [
            { name: "shares", formula: "SUM(signed_shares)" },
            { name: "invested", formula: "SUM(invested)" },
            { name: "value", formula: "SUM(value)" },
            { name: "gain", formula: "SUM(value - invested)" },
          ],
          sort: ["-value"],
        },
      },
      display: { format: "auto", summary: true },
    },
    {
      id: "dividends-per-month",
      type: "bar",
      title: "Dividends per month",
      position: at(8, 7, 4, 4),
      data: { query: { source: n.dividends, groupBy: [{ column: "date", bucket: "month", as: "month" }], measures: [{ name: "dividends", fn: "sum", column: "amount", color: "green" }] } },
      display: { format: "currency" },
    },
    {
      id: "how-to",
      type: "text",
      title: "",
      position: at(0, 11, 12, 3),
      display: {
        text: [
          "# How to fill this",
          `- ${financeLabel(n, "accounts")}: one row per bank account or depot, with its opening balance`,
          `- ${financeLabel(n, "transactions")}: import your bank's CSV (Data → ${financeLabel(n, "transactions")} → Import)`,
          `- ${financeLabel(n, "trades")}: buys and sells with ISIN, shares and price — your broker's export`,
          `- ${financeLabel(n, "prices")}: a price per ISIN and date; month ends are enough (live prices come later)`,
          `- ${financeLabel(n, "dividends")}: payouts per ISIN`,
        ].join("\n"),
      },
    },
  ];
  return { dateRange: { preset: "last-12-months" }, refreshSeconds: 0, widgets };
}

/** The cash-flow dashboard of the template's transactions, with an account filter. */
export function financeCashFlow(transactions: ModelTable): DashboardDocument {
  return { ...cashFlowDashboard(transactions), filters: [{ column: "account_id", label: "Account" }] };
}

/** Example rows to try it out: two accounts, three months of bookings, an ETF, its prices and a dividend. */
export function exampleRows(today = new Date()) {
  const month = (back: number, day: number) => {
    const d = new Date(Date.UTC(today.getFullYear(), today.getMonth() - back, day));
    return d.toISOString().slice(0, 10);
  };
  const monthEnd = (back: number) => new Date(Date.UTC(today.getFullYear(), today.getMonth() - back + 1, 0)).toISOString().slice(0, 10);
  return {
    accounts: [
      { name: "Checking", kind: "giro", opened_on: month(12, 1), opening_balance: 1000 },
      { name: "Depot", kind: "depot", opened_on: month(12, 1), opening_balance: 0 },
    ],
    categories: [
      { name: "Salary", kind: "income" },
      { name: "Rent", kind: "expense" },
      { name: "Groceries", kind: "expense" },
      { name: "Leisure", kind: "expense" },
      { name: "Investing", kind: "savings" },
    ],
    // Account and category by position: 0 = Checking, 1 = Depot; Salary, Rent, Groceries, Leisure, Investing.
    transactions: [3, 2, 1].flatMap((back) => [
      { date: month(back, 1), amount: 3000, payee: "Employer", account: 0, category: 0 },
      { date: month(back, 2), amount: -1000, payee: "Landlord", account: 0, category: 1 },
      { date: month(back, 10), amount: -200, payee: "Supermarket", account: 0, category: 2 },
      { date: month(back, 20), amount: -100, payee: "Cinema", account: 0, category: 3 },
      ...(back > 1 ? [{ date: month(back, 14), amount: back === 3 ? -1000 : -500, payee: "Depot", account: 0, category: 4 }] : []),
    ]),
    trades: [
      { date: month(3, 15), isin: "IE00B4L5Y983", name: "MSCI World ETF", kind: "buy", shares: 10, price: 100, account: 1 },
      { date: month(2, 15), isin: "IE00B4L5Y983", name: "MSCI World ETF", kind: "buy", shares: 5, price: 100, account: 1 },
    ],
    prices: [
      { isin: "IE00B4L5Y983", date: monthEnd(3), price: 102 },
      { isin: "IE00B4L5Y983", date: monthEnd(2), price: 98 },
      { isin: "IE00B4L5Y983", date: monthEnd(1), price: 110 },
    ],
    dividends: [{ date: month(1, 15), isin: "IE00B4L5Y983", name: "MSCI World ETF", amount: 12.5, account: 1 }],
  };
}

export type CreateRow = (table: string, row: Record<string, unknown>) => Promise<Record<string, unknown>>;

/** Fills the template's tables with the example rows, linking by the ids the API returns. */
export async function seedExamples(n: FinanceNames, createRow: CreateRow, today = new Date()): Promise<void> {
  const rows = exampleRows(today);
  const ids = async (table: string, list: Record<string, unknown>[]) => {
    const created: unknown[] = [];
    for (const row of list) created.push((await createRow(table, row)).id);
    return created;
  };
  const accounts = await ids(n.accounts, rows.accounts);
  const categories = await ids(n.categories, rows.categories);
  for (const { account, category, ...row } of rows.transactions)
    await createRow(n.transactions, { ...row, account_id: accounts[account], category_id: categories[category] });
  for (const { account, ...row } of rows.trades) await createRow(n.trades, { ...row, account_id: accounts[account] });
  for (const row of rows.prices) await createRow(n.prices, row);
  for (const { account, ...row } of rows.dividends) await createRow(n.dividends, { ...row, account_id: accounts[account] });
}
