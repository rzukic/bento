import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { CategoriseResult, CategoryOption } from "../api/categorise";
import type { ImportAnalysis, ImportRunResult } from "../api/import";
import { seedSetupInitializedForTest } from "../features/setup";
import { createTestRouter } from "../router";
import { resetAppStoreForTest } from "../stores/appStore";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import { resetExplorerStoreForTest } from "../stores/explorerStore";
import { resetSchemaStoreForTest, useSchemaStore } from "../stores/schemaStore";
import type { ModelSnapshot } from "../types/api";

const importApi = vi.hoisted(() => ({ analyzeImport: vi.fn(), previewImport: vi.fn(), runImport: vi.fn(), listImportProfiles: vi.fn() }));
const categoriseApi = vi.hoisted(() => ({ suggestCategories: vi.fn(), listCategoryRules: vi.fn(), saveCategoryRules: vi.fn(), deleteCategoryRule: vi.fn() }));
const aiApi = vi.hoisted(() => ({ getAiStatus: vi.fn() }));
const dataApi = vi.hoisted(() => ({ fetchTableRows: vi.fn(), patchRow: vi.fn() }));
vi.mock("../api/import", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/import")>()), ...importApi }));
vi.mock("../api/categorise", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/categorise")>()), ...categoriseApi }));
vi.mock("../api/ai", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/ai")>()), ...aiApi }));
vi.mock("../api/data", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/data")>()), ...dataApi }));

const snapshot = {
  version: 1,
  tables: [
    { name: "categories", columns: [{ name: "id", type: "integer", primaryKey: true }, { name: "name", type: "text" }] },
    {
      name: "transactions",
      displayName: "Transactions",
      columns: [
        { name: "id", type: "integer", primaryKey: true },
        { name: "date", type: "date", nullable: false },
        { name: "payee", displayName: "Payee", type: "text" },
        { name: "amount", type: "currency", nullable: false },
        { name: "category_id", displayName: "Category", type: "reference", references: "categories" },
      ],
    },
  ],
  relations: [],
} as unknown as ModelSnapshot;

const options: CategoryOption[] = [
  { value: "1", label: "Rent", group: null },
  { value: "2", label: "Groceries", group: "Food" },
  { value: "3", label: "Health", group: null },
];

const analysis: ImportAnalysis = {
  settings: { encoding: "utf-8", delimiter: ";", headerLine: 0, decimalSeparator: ",", dateFormat: "dd.MM.yyyy" },
  columns: [
    { index: 0, name: "Buchungstag", samples: ["02.10.2026"], inferredType: "date", blank: 0, distinctCount: 3, distinct: [] },
    { index: 1, name: "Empfänger", samples: ["Vermieter"], inferredType: "text", blank: 0, distinctCount: 3, distinct: [] },
    { index: 2, name: "Betrag", samples: ["-950,00"], inferredType: "float", blank: 0, distinctCount: 3, distinct: [] },
  ],
  preamble: [],
  previewRows: [],
  rowCount: 3,
  options: { encodings: ["utf-8"], delimiters: [";"], decimalSeparators: [","], dateFormats: ["dd.MM.yyyy"] },
};

function preview(dryRun: boolean): ImportRunResult {
  return {
    dryRun,
    total: 3,
    new: 3,
    duplicates: 0,
    invalid: 0,
    inserted: dryRun ? 0 : 3,
    rowsTruncated: false,
    rows: [
      { line: 2, status: "new", display: { date: "2026-10-02", payee: "Vermieter Schmidt", amount: "-950.00", category_id: null }, errors: [] },
      { line: 3, status: "new", display: { date: "2026-10-03", payee: "REWE Markt Berlin", amount: "-54.20", category_id: null }, errors: [] },
      { line: 4, status: "new", display: { date: "2026-10-07", payee: "Apotheke am Markt", amount: "-9.95", category_id: null }, errors: [] },
    ],
  };
}

function result(suggestions: CategoriseResult["suggestions"], extra: Partial<CategoriseResult> = {}): CategoriseResult {
  return { column: "category_id", options, matchColumns: ["payee"], suggestions, ai: null, aiError: null, ...extra };
}

beforeEach(() => {
  resetAppStoreForTest();
  resetAuthStoreForTest();
  seedSetupInitializedForTest();
  resetSchemaStoreForTest();
  resetExplorerStoreForTest();
  useAuthStore.setState({ status: "authenticated", user: { id: 1, username: "admin", role: "admin" }, accessToken: "token", expiresAt: Date.now() + 60_000, loginInFlight: false, error: null });
  useSchemaStore.setState({ status: "ready", snapshot, error: null, lastLoadedAt: Date.now(), inFlight: null });
  importApi.listImportProfiles.mockResolvedValue([]);
  importApi.analyzeImport.mockResolvedValue(analysis);
  importApi.previewImport.mockResolvedValue(preview(true));
  importApi.runImport.mockResolvedValue(preview(false));
  categoriseApi.listCategoryRules.mockResolvedValue([]);
  categoriseApi.saveCategoryRules.mockImplementation(async (_table: string, rules: unknown[]) => rules.map((r, i) => ({ id: i + 1, ...(r as object) })));
  aiApi.getAiStatus.mockResolvedValue({ enabled: true, configured: true, model: "claude-opus-5-5", budgetLeft: true });
  vi.stubGlobal("fetch", vi.fn(() => Promise.reject(new Error("fetch must be mocked in tests"))));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe("Categorising on import", () => {
  it("fills what rules know, asks the AI for the rest, imports the choices and keeps corrections as rules", async () => {
    categoriseApi.suggestCategories
      .mockResolvedValueOnce(result([{ key: "2", value: "1", label: "Rent", source: "history", confidence: 1, keyword: "Vermieter Schmidt", matchColumn: "payee", ruleId: null }]))
      .mockResolvedValueOnce(
        result(
          [
            { key: "3", value: "2", label: "Groceries", source: "ai", confidence: 0.92, keyword: "REWE", matchColumn: "payee", ruleId: null },
            { key: "4", value: "2", label: "Groceries", source: "ai", confidence: 0.35, keyword: null, matchColumn: null, ruleId: null },
          ],
          { ai: { model: "claude-opus-5-5", costUsd: 0.012, rows: 2, durationMs: 4000 } },
        ),
      );
    const user = userEvent.setup({ pointerEventsCheck: 0 });
    render(<RouterProvider router={createTestRouter(["/data/transactions/import"])} />);

    await user.upload(await screen.findByLabelText("CSV file"), new File(["x"], "oktober.csv", { type: "text/csv" }));
    await user.click(await screen.findByRole("button", { name: "Check rows" }));

    // Rules and earlier rows first, without the AI.
    const bar = await screen.findByRole("group", { name: "Categories" });
    expect(await within(bar).findByText(/1 of 3 rows filled — 1 like earlier rows/)).toBeInTheDocument();
    expect(categoriseApi.suggestCategories.mock.calls[0]).toEqual([
      "transactions",
      { column: "category_id", rows: expect.arrayContaining([{ key: "3", values: { date: "2026-10-03", payee: "REWE Markt Berlin", amount: "-54.20" } }]) },
    ]);
    expect(screen.getByLabelText("Category for row 2")).toHaveValue("1");

    // ✨ for the two open rows only.
    await user.click(within(bar).getByRole("button", { name: "Suggest the rest (2)" }));
    expect(await within(bar).findByText(/3 of 3 rows filled — 1 like earlier rows, 2 by AI \(\$0\.01\)/)).toBeInTheDocument();
    expect(categoriseApi.suggestCategories.mock.calls[1][1]).toMatchObject({ ai: true, rows: [{ key: "3" }, { key: "4" }] });
    expect(screen.getByText("AI 35 %")).toBeInTheDocument();

    // The unsure one is wrong: the correction offers a rule on the payee.
    await user.selectOptions(screen.getByLabelText("Category for row 4"), "3");
    expect(screen.getByLabelText(/Rule for “Apotheke am Markt”/)).toBeChecked();

    await user.click(screen.getByRole("button", { name: "Import 3 rows" }));
    await waitFor(() => expect(importApi.runImport).toHaveBeenCalled());
    expect(importApi.runImport.mock.calls[0][1].assign).toEqual({ column: "category_id", values: { "2": "1", "3": "2", "4": "3" } });

    // The correction and the sure AI choice become rules; the unsure AI guess does not.
    await waitFor(() => expect(categoriseApi.saveCategoryRules).toHaveBeenCalled());
    expect(categoriseApi.saveCategoryRules.mock.calls[0][1]).toEqual(
      expect.arrayContaining([
        { column: "category_id", matchColumn: "payee", pattern: "REWE", value: "2" },
        { column: "category_id", matchColumn: "payee", pattern: "Apotheke am Markt", value: "3" },
      ]),
    );
    expect(categoriseApi.saveCategoryRules.mock.calls[0][1]).toHaveLength(2);
    expect(await screen.findByText(/Saved 2 rules for the next import/)).toBeInTheDocument();
  });

  it("without the AI set up, offers choosing by hand and no ✨", async () => {
    aiApi.getAiStatus.mockResolvedValue({ enabled: false, configured: false, model: "claude-opus-5-5", budgetLeft: true });
    categoriseApi.suggestCategories.mockResolvedValue(result([]));
    const user = userEvent.setup({ pointerEventsCheck: 0 });
    render(<RouterProvider router={createTestRouter(["/data/transactions/import"])} />);
    await user.upload(await screen.findByLabelText("CSV file"), new File(["x"], "oktober.csv", { type: "text/csv" }));
    await user.click(await screen.findByRole("button", { name: "Check rows" }));

    expect(await screen.findByText(/your choices become rules for the next import/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Suggest the rest/ })).not.toBeInTheDocument();
  });
});

describe("Categorising existing rows", () => {
  it("loads rows without a value, fills them and saves each row", async () => {
    dataApi.fetchTableRows.mockResolvedValue({
      data: [
        { id: 31, date: "2026-11-14", payee: "Cinema", amount: -24, category_id: null },
        { id: 30, date: "2026-11-13", payee: "Shell Station 42", amount: -65, category_id: null },
      ],
      meta: { page: 1, per_page: 200, total: 2 },
    });
    dataApi.patchRow.mockResolvedValue({});
    categoriseApi.suggestCategories.mockResolvedValue(
      result([{ key: "31", value: "3", label: "Health", source: "rule", confidence: 1, keyword: "Cinema", matchColumn: "payee", ruleId: 4 }]),
    );
    const user = userEvent.setup({ pointerEventsCheck: 0 });
    render(<RouterProvider router={createTestRouter(["/data/transactions/categorise"])} />);

    expect(await screen.findByRole("heading", { name: "Categorise Transactions" })).toBeInTheDocument();
    await waitFor(() => expect(dataApi.fetchTableRows).toHaveBeenCalled());
    expect(dataApi.fetchTableRows.mock.calls[0][1]).toMatchObject({ filters: [{ column: "category_id", op: "null" }], sort: [{ column: "id", dir: "desc" }] });
    expect(await screen.findByText("Rule")).toBeInTheDocument();

    await user.selectOptions(screen.getByLabelText("Category for row 30"), "1");
    await user.click(screen.getByRole("button", { name: "Save 2 rows" }));

    await waitFor(() => expect(dataApi.patchRow).toHaveBeenCalledTimes(2));
    expect(dataApi.patchRow).toHaveBeenCalledWith("transactions", "31", { category_id: 3 });
    expect(dataApi.patchRow).toHaveBeenCalledWith("transactions", "30", { category_id: 1 });
    expect(categoriseApi.saveCategoryRules.mock.calls[0][1]).toEqual([{ column: "category_id", matchColumn: "payee", pattern: "Shell Station 42", value: "1" }]);
    expect(await screen.findByText(/2 rows categorised\. Saved 1 rule/)).toBeInTheDocument();
  });
});
