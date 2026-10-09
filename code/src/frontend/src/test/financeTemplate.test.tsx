import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { DashboardDocument } from "../api/dashboards";
import type { ModelTable, SchemaModelDocument } from "../types/api";
import {
  ensureCostFunction,
  exampleRows,
  financeCashFlow,
  financeConflicts,
  financeNames,
  seedExamples,
  suggestedPrefix,
  wealthDashboard,
  withFinanceTables,
} from "../features/dashboards/financeTemplate";

const schemaApi = vi.hoisted(() => ({ getCanonicalSchemaSnapshot: vi.fn(), previewSchemaDraft: vi.fn(), applySchemaDraft: vi.fn() }));
const dashboardsApi = vi.hoisted(() => ({ createDashboard: vi.fn() }));
const dataApi = vi.hoisted(() => ({ createRow: vi.fn() }));
const functionsApi = vi.hoisted(() => ({ listFunctions: vi.fn(), listFunctionTemplates: vi.fn(), createFunction: vi.fn() }));
const formsApi = vi.hoisted(() => ({ createForm: vi.fn(), listForms: vi.fn() }));
vi.mock("../api/schema", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/schema")>()), ...schemaApi }));
vi.mock("../api/dashboards", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/dashboards")>()), ...dashboardsApi }));
vi.mock("../api/data", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/data")>()), ...dataApi }));
vi.mock("../api/functions", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/functions")>()), ...functionsApi }));
vi.mock("../api/forms", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/forms")>()), ...formsApi }));

const avgCostTemplate = {
  id: "avg-cost",
  title: "Realised gains by average cost",
  description: "By average cost",
  name: "avg_cost",
  arguments: [
    { name: "shares", type: "float" as const },
    { name: "price", type: "float" as const },
    { name: "fees", type: "float" as const },
    { name: "what", type: "text" as const },
  ],
  resultType: "float" as const,
  code: "return shares;",
  allowedHosts: [],
  credentials: [],
  mode: "rows" as const,
};

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

const existing: SchemaModelDocument = {
  version: 3,
  tables: [{ name: "girokonto", columns: [{ name: "id", type: "integer", primaryKey: true }] }, { name: "transactions", columns: [] }],
  relations: [{ name: "accounts_transactions", type: "1:N", from: "x", to: "y", column: "z" }],
};

/** Every formula in a widget or computed column of the template, with the plain names. */
function formulas(): string[] {
  const n = financeNames("");
  const fromColumns = withFinanceTables({ version: 1, tables: [], relations: [] }, n).tables.flatMap((t) => t.columns.map((c) => c.formula).filter(Boolean));
  const fromWidgets = wealthDashboard(n).widgets.flatMap((w) => (w.data?.query?.measures ?? []).map((m) => m.formula).filter(Boolean));
  return [...fromColumns, ...fromWidgets] as string[];
}

describe("finance template", () => {
  it("adds its tables and relations beside the existing ones, never changing them", () => {
    const n = financeNames("finance_");
    const draft = withFinanceTables(existing, n);
    expect(draft.tables.slice(0, 2)).toEqual(existing.tables);
    expect(draft.tables.slice(2).map((t) => t.name)).toEqual([
      "finance_accounts",
      "finance_categories",
      "finance_transactions",
      "finance_trades",
      "finance_prices",
      "finance_dividends",
    ]);
    expect(draft.relations.slice(1).map((r) => [r.from, r.to, r.column])).toEqual([
      ["finance_accounts", "finance_transactions", "account_id"],
      ["finance_categories", "finance_transactions", "category_id"],
      ["finance_categories", "finance_categories", "parent_id"],
      ["finance_accounts", "finance_trades", "account_id"],
      ["finance_accounts", "finance_dividends", "account_id"],
    ]);
    // Labels tell them from existing tables; formulas name the prefixed tables.
    expect(draft.tables.slice(2).map((t) => t.displayName)).toEqual([
      "Finance accounts",
      "Finance categories",
      "Finance transactions",
      "Finance trades",
      "Finance prices",
      "Finance dividends",
    ]);
    expect(withFinanceTables(existing, financeNames("")).tables[2].displayName).toBe("Accounts");
    expect(draft.tables.find((t) => t.name === "finance_trades")?.columns.find((c) => c.name === "value")?.formula).toContain('LOOKUP_ASOF("finance_prices"');
    expect(wealthDashboard(n).widgets.find((w) => w.id === "net-worth")?.data?.query?.measures?.[0].formula).toBe("SUM(balance + SUM(finance_trades.value))");
  });

  it("suggests a prefix only when a name is taken", () => {
    expect(suggestedPrefix([{ name: "girokonto" }])).toBe("");
    expect(suggestedPrefix(existing.tables)).toBe("finance_");
    expect(financeConflicts(financeNames(""), existing.tables)).toEqual(["transactions"]);
  });

  it("uses exactly the formulas the backend test runs on SQLite and Postgres", () => {
    const backendTest = readFileSync(resolve(__dirname, "../../../../tests/Bento.Tests/FinanceTemplateTests.cs"), "utf8");
    for (const formula of formulas()) expect(backendTest, formula).toContain(formula.replaceAll('"', '\\"'));
  });

  it("lays out the wealth dashboard without overlaps and the cash flow with an account filter", () => {
    const widgets = wealthDashboard(financeNames("")).widgets;
    for (const a of widgets)
      for (const b of widgets)
        if (a !== b) {
          const overlap = a.position.x < b.position.x + b.position.w && b.position.x < a.position.x + a.position.w && a.position.y < b.position.y + b.position.h && b.position.y < a.position.y + a.position.h;
          expect(overlap, `${a.id} / ${b.id}`).toBe(false);
        }
    const transactions = withFinanceTables({ version: 1, tables: [], relations: [] }, financeNames("")).tables.find((t) => t.name === "transactions") as unknown as ModelTable;
    const cashFlow = financeCashFlow(transactions);
    expect(cashFlow.filters).toEqual([{ column: "account_id", label: "Account" }]);
    expect(cashFlow.widgets.map((w) => w.title)).toContain("Money flow");
  });

  it("links the example rows by the ids the API returns", async () => {
    let next = 40;
    const created: [string, Record<string, unknown>][] = [];
    await seedExamples(financeNames(""), async (table, row) => {
      created.push([table, row]);
      return { ...row, id: next++ };
    });
    const rows = exampleRows();
    expect(created.filter(([t]) => t === "transactions")).toHaveLength(rows.transactions.length);
    // Accounts got 40 and 41, categories 42–46.
    expect(created.find(([t, r]) => t === "transactions" && r.payee === "Landlord")?.[1]).toMatchObject({ account_id: 40, category_id: 43 });
    expect(created.find(([t]) => t === "trades")?.[1]).toMatchObject({ account_id: 41, isin: "IE00B4L5Y983" });
  });

  it("sets everything up from the dashboards page and opens the wealth dashboard", async () => {
    const { FinanceSetup } = await import("../features/dashboards/FinanceSetup");
    const { useSchemaStore } = await import("../stores/schemaStore");
    schemaApi.getCanonicalSchemaSnapshot.mockResolvedValue(existing);
    schemaApi.previewSchemaDraft.mockResolvedValue({ isValid: true, errors: [] });
    schemaApi.applySchemaDraft.mockResolvedValue({});
    dataApi.createRow.mockImplementation(async (_t: string, row: Record<string, unknown>) => ({ ...row, id: 1 }));
    functionsApi.listFunctions.mockResolvedValue([]);
    functionsApi.listFunctionTemplates.mockResolvedValue([avgCostTemplate]);
    functionsApi.createFunction.mockResolvedValue({});
    formsApi.createForm.mockResolvedValue({ id: 3 });
    formsApi.listForms.mockResolvedValue([]);
    dashboardsApi.createDashboard.mockImplementation(async (name: string, document: DashboardDocument) => ({ id: name === "Wealth" ? 9 : 8, name, dashboard: document }));
    const applied = withFinanceTables(existing, financeNames("finance_"));
    useSchemaStore.setState({ load: vi.fn(async () => applied as never) });
    const user = userEvent.setup();
    render(
      <MemoryRouter initialEntries={["/dashboards"]}>
        <Routes>
          <Route path="/dashboards" element={<FinanceSetup tables={existing.tables as unknown as ModelTable[]} onError={() => {}} />} />
          <Route path="/dashboards/:id" element={<p>opened</p>} />
        </Routes>
      </MemoryRouter>,
    );
    expect(screen.getByLabelText("Table name prefix")).toHaveValue("finance_");
    await user.click(screen.getByRole("button", { name: "Set up" }));
    expect(await screen.findByText("opened")).toBeInTheDocument();
    expect(schemaApi.applySchemaDraft).toHaveBeenCalledWith(applied);
    await waitFor(() => expect(dashboardsApi.createDashboard.mock.calls.map(([name]) => name)).toEqual(["Cash flow", "Wealth"]));
    expect(dataApi.createRow).toHaveBeenCalledWith("finance_accounts", expect.objectContaining({ name: "Checking" }));
    // The function the trades' realised gains call is created before the tables are checked.
    expect(functionsApi.createFunction).toHaveBeenCalledWith(expect.objectContaining({ name: "avg_cost", mode: "rows", code: "return shares;" }));
    expect(functionsApi.createFunction.mock.invocationCallOrder[0]).toBeLessThan(schemaApi.previewSchemaDraft.mock.invocationCallOrder[0]);
    // Quick entry: a shared, pinned form for the new transactions.
    expect(formsApi.createForm).toHaveBeenCalledWith("Add transaction", expect.objectContaining({ table: "finance_transactions", pinned: true }), true);
  });

  it("uses an existing avg_cost over rows and refuses one that is not", async () => {
    const create = vi.fn();
    const templates = vi.fn(async () => [avgCostTemplate]);
    const existing = { ...avgCostTemplate, version: 1, createdAt: "", updatedAt: "", usedBy: [], usableInFormulas: true, description: null };
    await ensureCostFunction({ list: async () => [existing], templates, create });
    expect(create).not.toHaveBeenCalled();
    await expect(ensureCostFunction({ list: async () => [{ ...existing, mode: "row" }], templates, create })).rejects.toThrow(
      "A function named avg_cost exists already and does not run over a group of rows; rename it under Functions first.",
    );
  });
});

describe("cash-flow starter", () => {
  it("splits by the category, not by the first link (the account)", async () => {
    const { pickColumns } = await import("../features/dashboards/starter");
    const transactions = withFinanceTables({ version: 1, tables: [], relations: [] }, financeNames("")).tables.find((t) => t.name === "transactions") as unknown as ModelTable;
    expect(pickColumns(transactions).category?.name).toBe("category_id");
    const cashFlow = financeCashFlow(transactions);
    expect(cashFlow.widgets.find((w) => w.id === "money-flow")?.data?.query?.groupBy?.[0].column).toBe("category_id");
    // Step 4.5: spending by main category, a slice opens its categories; budgets of this month.
    const spending = cashFlow.widgets.find((w) => w.id === "spending-by-category");
    expect(spending?.title).toBe("Spending by category");
    expect(spending?.data?.query?.groupBy).toEqual([{ column: "main_category" }]);
    expect(spending?.display?.drill).toBe("category_id");
    expect(cashFlow.widgets.find((w) => w.type === "budget")?.data?.budget).toEqual({
      source: "transactions",
      amount: "amount",
      date: "date",
      category: "category_id",
      limit: "budget",
      expenses: "negative",
      period: "month",
    });
    const overlaps = cashFlow.widgets.flatMap((a, i) =>
      cashFlow.widgets.slice(i + 1).filter((b) => a.position.x < b.position.x + b.position.w && b.position.x < a.position.x + a.position.w && a.position.y < b.position.y + b.position.h && b.position.y < a.position.y + a.position.h).map((b) => `${a.id}/${b.id}`),
    );
    expect(overlaps).toEqual([]);
  });

  it("seeds subcategories after their parent, with budgets, and this month's groceries", async () => {
    const created: [string, Record<string, unknown>][] = [];
    let next = 1;
    await seedExamples(financeNames(""), async (table, row) => {
      created.push([table, row]);
      return { ...row, id: next++ };
    });
    const categories = created.filter(([t]) => t === "categories").map(([, row]) => row);
    // Ids count up: two accounts (1, 2), then the categories from 3 in their order.
    const food = 3 + categories.findIndex((c) => c.name === "Food");
    expect(categories.find((c) => c.name === "Groceries")).toMatchObject({ budget: 250, parent_id: food });
    expect(categories.findIndex((c) => c.name === "Food")).toBeLessThan(categories.findIndex((c) => c.name === "Groceries"));
    const now = new Date();
    const thisMonth = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, "0")}`;
    expect(created.filter(([t, r]) => t === "transactions" && String(r.date).startsWith(thisMonth)).map(([, r]) => r.amount)).toEqual([-205]);
  });
});
