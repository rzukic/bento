import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { DashboardDocument } from "../api/dashboards";
import type { FormDefinition, SavedForm } from "../api/forms";
import { buildRow, defaultForm, initialState, missingRequired, todayIso } from "../features/forms/formModel";
import { formsForDashboard } from "../features/forms/quickAdd";
import { seedSetupInitializedForTest } from "../features/setup";
import { createTestRouter } from "../router";
import { resetAppStoreForTest } from "../stores/appStore";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import { resetFormsStoreForTest } from "../stores/formsStore";
import { resetSchemaStoreForTest, useSchemaStore } from "../stores/schemaStore";
import type { ModelSnapshot, ModelTable } from "../types/api";

const formsApi = vi.hoisted(() => ({ listForms: vi.fn(), getForm: vi.fn(), createForm: vi.fn(), updateForm: vi.fn(), deleteForm: vi.fn() }));
const dataApi = vi.hoisted(() => ({ createRow: vi.fn(), fetchTableRows: vi.fn() }));
vi.mock("../api/forms", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/forms")>()), ...formsApi }));
vi.mock("../api/data", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/data")>()), ...dataApi }));

const transactions: ModelTable = {
  name: "transactions",
  displayName: "Transactions",
  columns: [
    { name: "id", type: "integer", primaryKey: true },
    { name: "date", type: "date", nullable: false },
    { name: "amount", type: "currency", nullable: false },
    { name: "payee", type: "text" },
    { name: "kind", type: "enum", values: ["card", "transfer"] },
    { name: "account_id", type: "reference", references: "accounts" },
    { name: "category_id", type: "reference", references: "categories" },
    { name: "balance", type: "computed", formula: "1" },
  ],
};
const accounts: ModelTable = { name: "accounts", columns: [{ name: "id", type: "integer", primaryKey: true }, { name: "name", type: "text" }] };
const categories: ModelTable = { name: "categories", columns: [{ name: "id", type: "integer", primaryKey: true }, { name: "name", type: "text" }] };
const snapshot = { version: 1, tables: [transactions, accounts, categories], relations: [] } as unknown as ModelSnapshot;

const addTransaction: FormDefinition = {
  table: "transactions",
  pinned: true,
  fields: [
    { column: "amount", label: "Amount", sign: "negative" },
    { column: "payee" },
    { column: "category_id", label: "Category" },
    { column: "account_id", label: "Account", default: { kind: "last" } },
    { column: "date", default: { kind: "today" } },
    { section: "More" },
    { column: "kind", hidden: true, default: { kind: "value", value: "card" } },
  ],
};
const saved: SavedForm = { id: 7, name: "Add transaction", table: "transactions", isShared: true, isOwner: true, form: addTransaction, createdAt: "", updatedAt: "" };

function signIn(role: string) {
  useAuthStore.setState({ status: "authenticated", user: { id: 1, username: role, role }, accessToken: "token", expiresAt: Date.now() + 60_000, loginInFlight: false, error: null });
}

function memoryStorage(): Storage {
  const values = new Map<string, string>();
  return {
    get length() {
      return values.size;
    },
    clear: () => values.clear(),
    getItem: (key) => values.get(key) ?? null,
    key: (index) => Array.from(values.keys())[index] ?? null,
    removeItem: (key) => void values.delete(key),
    setItem: (key, value) => void values.set(key, String(value)),
  };
}

const rows = (names: string[]) => ({ data: names.map((name, i) => ({ id: i + 1, name })), meta: { page: 1, per_page: 50, total: names.length, total_pages: 1 } });

beforeEach(() => {
  resetAppStoreForTest();
  resetAuthStoreForTest();
  resetFormsStoreForTest();
  seedSetupInitializedForTest();
  resetSchemaStoreForTest();
  signIn("editor");
  useSchemaStore.setState({ status: "ready", snapshot, error: null, lastLoadedAt: Date.now(), inFlight: null });
  formsApi.listForms.mockResolvedValue([saved]);
  formsApi.getForm.mockResolvedValue(saved);
  dataApi.fetchTableRows.mockImplementation(async (table: string) => rows(table === "accounts" ? ["Checking", "Depot"] : ["Groceries", "Rent", "Leisure"]));
  vi.stubGlobal("fetch", vi.fn(() => Promise.reject(new Error("fetch must be mocked in tests"))));
  vi.stubGlobal("localStorage", memoryStorage());
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe("form model", () => {
  it("starts a table's form with every column it can fill", () => {
    const form = defaultForm(transactions);
    expect(form.fields).toEqual([
      { column: "date", default: { kind: "today" } },
      { column: "amount" },
      { column: "payee" },
      { column: "kind" },
      { column: "account_id", default: { kind: "last" } },
      { column: "category_id", default: { kind: "last" } },
    ]);
    expect(missingRequired({ table: "transactions", fields: [{ column: "date" }] }, transactions).map((c) => c.name)).toEqual(["amount"]);
  });

  it("fills defaults and builds the row with signs and the user's number notation", () => {
    const now = new Date(2026, 9, 7, 9, 30);
    const { values, signs } = initialState(addTransaction, transactions, { account_id: 2, amount: 40 }, now);
    expect(values).toMatchObject({ date: "2026-10-07", account_id: 2, kind: "card", amount: "" });
    expect(signs).toEqual({ amount: "negative" });

    const built = buildRow(addTransaction, transactions, { ...values, amount: "12,50", payee: "  Bakery " }, signs);
    expect(built.errors).toEqual({});
    expect(built.row).toEqual({ date: "2026-10-07", amount: -12.5, payee: "Bakery", account_id: 2, kind: "card" });

    expect(buildRow(addTransaction, transactions, { ...values, amount: "5" }, { amount: "positive" }).row.amount).toBe(5);
    expect(buildRow(addTransaction, transactions, { ...values, amount: "" }, signs).errors).toEqual({ amount: "Amount is required." });
    expect(buildRow(addTransaction, transactions, { ...values, amount: "abc" }, signs).errors).toEqual({ amount: "Enter a number, e.g. 12,50." });
  });

  it("offers pinned forms on dashboards that show their table", () => {
    const dashboard = (source: string, formula = "SUM(x)") =>
      ({ widgets: [{ id: "w", type: "kpi", position: { x: 0, y: 0, w: 3, h: 2 }, data: { query: { source, measures: [{ name: "m", formula }] } } }] }) as unknown as DashboardDocument;
    expect(formsForDashboard([saved], dashboard("transactions"))).toEqual([saved]);
    expect(formsForDashboard([saved], dashboard("accounts", "SUM(balance + SUM(transactions.amount))"))).toEqual([saved]);
    expect(formsForDashboard([saved], dashboard("accounts", "SUM(my_transactions.amount)"))).toEqual([]);
    expect(formsForDashboard([{ ...saved, form: { ...addTransaction, pinned: false } }], dashboard("transactions"))).toEqual([]);
  });
});

describe("Forms", () => {
  it("adds a transaction in a few taps and starts over for the next one", async () => {
    window.localStorage.setItem("bento.form.last.7", JSON.stringify({ account_id: 2 }));
    dataApi.createRow.mockImplementation(async (_t: string, row: Record<string, unknown>) => ({ ...row, id: 41 }));
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/forms/7"])} />);

    expect(await screen.findByRole("heading", { name: "Add transaction" })).toBeInTheDocument();
    const amount = screen.getByRole("textbox", { name: "Amount" });
    await waitFor(() => expect(amount).toHaveFocus());
    expect(screen.getByRole("button", { name: "Negative" })).toHaveAttribute("aria-pressed", "true");
    // Few accounts and categories: buttons, the account as last time.
    const account = await screen.findByRole("group", { name: "Account" });
    expect(within(account).getByRole("button", { name: "Depot" })).toHaveAttribute("aria-pressed", "true");
    expect(screen.queryByLabelText(/Kind/)).not.toBeInTheDocument();
    expect(document.querySelector("input[type=date]")).toHaveValue(todayIso());

    await user.type(amount, "12,50");
    await user.type(screen.getByLabelText("payee"), "Bakery");
    await user.click(within(await screen.findByRole("group", { name: "Category" })).getByRole("button", { name: "Groceries" }));
    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() =>
      expect(dataApi.createRow).toHaveBeenCalledWith("transactions", { amount: -12.5, payee: "Bakery", category_id: 1, account_id: 2, date: todayIso(), kind: "card" }),
    );
    expect(await screen.findByRole("status")).toHaveTextContent("Saved: -12.50 · Bakery");
    // Ready for the next one: amount empty, account and date kept, the payee suggested.
    expect(screen.getByRole("textbox", { name: "Amount" })).toHaveValue("");
    expect(within(await screen.findByRole("group", { name: "Account" })).getByRole("button", { name: "Depot" })).toHaveAttribute("aria-pressed", "true");
    expect(document.querySelector("datalist option")).toHaveAttribute("value", "Bakery");
  });

  it("says what is missing before sending, and shows the server's answer on the field", async () => {
    const { ApiClientError } = await import("../api/client");
    dataApi.createRow.mockRejectedValue(new ApiClientError("Payee is too long.", 400, "VALIDATION_FAILED", { field: "payee" }));
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/forms/7"])} />);

    await user.click(await screen.findByRole("button", { name: "Save" }));
    expect(screen.getByText("Amount is required.")).toBeInTheDocument();
    expect(dataApi.createRow).not.toHaveBeenCalled();

    await user.type(screen.getByRole("textbox", { name: "Amount" }), "3");
    await user.click(screen.getByRole("button", { name: "Save" }));
    expect(await screen.findByText("Payee is too long.")).toBeInTheDocument();
  });

  it("lets viewers see a form but not add rows", async () => {
    signIn("viewer");
    render(<RouterProvider router={createTestRouter(["/forms/7"])} />);
    expect(await screen.findByText("You can see this form; adding rows needs the editor role.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
  });

  it("builds a form from a table: order, hidden fields with defaults, quick add", async () => {
    formsApi.createForm.mockResolvedValue({ ...saved, id: 9 });
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/forms/new?table=transactions"])} />);

    expect(await screen.findByRole("heading", { name: "New form" })).toBeInTheDocument();
    expect(screen.getByLabelText("Form name")).toHaveValue("Add transaction");
    const fields = within(screen.getByRole("list", { name: "Fields" }));
    expect(fields.getAllByRole("listitem").map((li) => li.querySelector(".form-builder__item-title span")?.textContent)).toEqual([
      "date",
      "amount",
      "payee",
      "kind",
      "account_id",
      "category_id",
    ]);

    await user.click(fields.getByRole("button", { name: "Move amount up" }));
    await user.click(fields.getByRole("button", { name: "kind" }));
    expect(screen.getByRole("checkbox", { name: /Hidden/ })).toBeDisabled();
    await user.selectOptions(screen.getByLabelText("Default"), "value");
    await user.type(screen.getByLabelText("Fixed value"), "card");
    await user.click(screen.getByRole("checkbox", { name: /Hidden/ }));
    await user.click(fields.getByRole("button", { name: "Remove payee" }));
    await user.click(screen.getByRole("checkbox", { name: /Quick add/ }));
    // The preview shows the form as it will be filled.
    const preview = within(screen.getByRole("region", { name: "Preview" }));
    expect(preview.queryByLabelText(/Payee/)).not.toBeInTheDocument();
    expect(preview.getByRole("button", { name: "Save" })).toBeDisabled();

    await user.click(screen.getByRole("button", { name: "Create form" }));
    await waitFor(() =>
      expect(formsApi.createForm).toHaveBeenCalledWith(
        "Add transaction",
        {
          table: "transactions",
          pinned: true,
          fields: [
            { column: "amount" },
            { column: "date", default: { kind: "today" } },
            { column: "kind", default: { kind: "value", value: "card" }, hidden: true },
            { column: "account_id", default: { kind: "last" } },
            { column: "category_id", default: { kind: "last" } },
          ],
        },
        true,
      ),
    );
  });

  it("lists pinned forms under Quick add in the sidebar", async () => {
    render(<RouterProvider router={createTestRouter(["/forms"])} />);
    const nav = await screen.findByRole("navigation", { name: "Main navigation" });
    expect(await within(nav).findByRole("link", { name: "Add transaction" })).toHaveAttribute("href", "/forms/7");
    expect(within(nav).getByText("Quick add")).toBeInTheDocument();
    expect(await screen.findByText(/Transactions · quick add · shared/)).toBeInTheDocument();
  });
});
