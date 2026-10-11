import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ImportAnalysis, ImportRunResult } from "../api/import";
import { seedSetupInitializedForTest } from "../features/setup";
import { createTestRouter } from "../router";
import { resetAppStoreForTest } from "../stores/appStore";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import { resetExplorerStoreForTest, useExplorerStore } from "../stores/explorerStore";
import { resetSchemaStoreForTest, useSchemaStore } from "../stores/schemaStore";
import { freshTableView } from "../stores/explorer/helpers";
import type { ModelSnapshot } from "../types/api";

const api = vi.hoisted(() => ({
  analyzeImport: vi.fn(),
  previewImport: vi.fn(),
  runImport: vi.fn(),
  listImportProfiles: vi.fn(),
  createImportProfile: vi.fn(),
  updateImportProfile: vi.fn(),
  deleteImportProfile: vi.fn(),
}));

const dataApi = vi.hoisted(() => ({ fetchTableRows: vi.fn() }));
vi.mock("../api/data", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/data")>()), ...dataApi }));

vi.mock("../api/import", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../api/import")>()),
  ...api,
}));

const snapshot = {
  version: 1,
  tables: [
    { name: "categories", columns: [{ name: "id", type: "integer", primaryKey: true }, { name: "name", type: "text" }] },
    {
      name: "transactions",
      displayName: "Transactions",
      columns: [
        { name: "id", type: "integer", primaryKey: true },
        { name: "booked_on", displayName: "Booked on", type: "date", nullable: false },
        { name: "purpose", type: "text" },
        { name: "amount", type: "currency", nullable: false },
        { name: "category_id", displayName: "Category", type: "reference", references: "categories" },
      ],
    },
  ],
  relations: [],
} as unknown as ModelSnapshot;

const analysis: ImportAnalysis = {
  settings: { encoding: "windows-1252", delimiter: ";", headerLine: 2, decimalSeparator: ",", dateFormat: "dd.MM.yyyy" },
  columns: [
    { index: 0, name: "Buchungstag", samples: ["01.09.2026"], inferredType: "date", blank: 0, distinctCount: 3, distinct: ["01.09.2026", "02.09.2026", "15.09.2026"] },
    { index: 1, name: "Verwendungszweck", samples: ["Einkauf"], inferredType: "text", blank: 0, distinctCount: 3, distinct: ["Einkauf", "Gehalt", "Haftpflicht"] },
    { index: 2, name: "Betrag (EUR)", samples: ["-45,90"], inferredType: "float", blank: 0, distinctCount: 3, distinct: ["-45,90", "3.250,00", "-58,20"] },
    { index: 3, name: "Kategorie", samples: ["Lebensmittel"], inferredType: "text", blank: 0, distinctCount: 2, distinct: ["Lebensmittel", "Gehalt"] },
  ],
  preamble: [["Kontonummer:", "DE00 1234"], ["Kontostand:", "2.481,37 EUR"]],
  previewRows: [],
  rowCount: 3,
  options: {
    encodings: ["utf-8", "windows-1252"],
    delimiters: [";", ","],
    decimalSeparators: [",", "."],
    dateFormats: ["dd.MM.yyyy", "yyyy-MM-dd"],
  },
};

function runResult(dryRun: boolean): ImportRunResult {
  return {
    dryRun,
    total: 3,
    new: 1,
    duplicates: 1,
    invalid: 1,
    inserted: dryRun ? 0 : 1,
    rowsTruncated: false,
    rows: [
      { line: 4, status: "new", display: { booked_on: "2026-09-01", purpose: "Einkauf", amount: "-45.90", category_id: "Lebensmittel" }, errors: [] },
      { line: 5, status: "duplicate", display: { booked_on: "2026-09-02", purpose: "Gehalt", amount: "3250.00", category_id: "Gehalt" }, errors: [] },
      {
        line: 6,
        status: "invalid",
        display: { booked_on: "2026-09-15", purpose: "Haftpflicht", amount: "-58.20", category_id: "Versicherung" },
        errors: [{ field: "category_id", message: "'Versicherung' is not in Categories." }],
      },
    ],
  };
}

beforeEach(() => {
  resetAppStoreForTest();
  resetAuthStoreForTest();
  seedSetupInitializedForTest();
  resetSchemaStoreForTest();
  resetExplorerStoreForTest();
  useAuthStore.setState({
    status: "authenticated",
    user: { id: 1, username: "admin", role: "admin" },
    accessToken: "token",
    expiresAt: Date.now() + 60_000,
    loginInFlight: false,
    error: null,
  });
  useSchemaStore.setState({ status: "ready", snapshot, error: null, lastLoadedAt: Date.now(), inFlight: null });
  api.listImportProfiles.mockResolvedValue([]);
  api.analyzeImport.mockResolvedValue(analysis);
  api.previewImport.mockResolvedValue(runResult(true));
  api.runImport.mockResolvedValue(runResult(false));
  vi.stubGlobal("fetch", vi.fn(() => Promise.reject(new Error("fetch must be mocked in tests"))));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe("CSV import page", () => {
  it("reads a bank export, suggests the mapping, checks the rows and imports the new ones", async () => {
    const user = userEvent.setup({ pointerEventsCheck: 0 });
    render(<RouterProvider router={createTestRouter(["/data/transactions/import"])} />);

    expect(await screen.findByRole("heading", { name: "Import into Transactions" })).toBeInTheDocument();

    const file = new File([new Uint8Array([0x22, 0x42, 0x22])], "girokonto.csv", { type: "text/csv" });
    await user.upload(screen.getByLabelText("File to import"), file);

    await waitFor(() => expect(api.analyzeImport).toHaveBeenCalledTimes(1));
    expect(await screen.findByText("2 lines above the header are skipped")).toBeInTheDocument();
    expect(screen.getByLabelText("Encoding")).toHaveValue("windows-1252");
    expect(screen.getByLabelText("File column for Booked on")).toHaveValue("Buchungstag");
    expect(screen.getByLabelText("File column for purpose")).toHaveValue("Verwendungszweck");
    expect(screen.getByLabelText("File column for amount")).toHaveValue("Betrag (EUR)");
    expect(screen.getByLabelText("File column for Category")).toHaveValue("Kategorie");
    expect(screen.getByLabelText("Booked on identifies a row")).toBeChecked();
    expect(screen.getByLabelText("Category identifies a row")).not.toBeChecked();

    await user.click(screen.getByRole("button", { name: "Check rows" }));

    const payload = api.previewImport.mock.calls[0][1];
    expect(api.previewImport.mock.calls[0][0]).toBe("transactions");
    expect(payload.mapping).toEqual([
      { source: "Buchungstag", target: "booked_on" },
      { source: "Verwendungszweck", target: "purpose" },
      { source: "Betrag (EUR)", target: "amount" },
      { source: "Kategorie", target: "category_id" },
    ]);
    expect(payload.keyColumns).toEqual(["booked_on", "purpose", "amount"]);
    expect(payload.settings.decimalSeparator).toBe(",");

    expect(await screen.findByText("'Versicherung' is not in Categories.")).toBeInTheDocument();
    const summary = screen.getByRole("tablist", { name: "Rows by status" });
    expect(within(summary).getByRole("tab", { name: /1\s*New/ })).toBeInTheDocument();
    expect(within(summary).getByRole("tab", { name: /1\s*Already there/ })).toBeInTheDocument();

    await user.click(within(summary).getByRole("tab", { name: /Problems/ }));
    const review = within(document.querySelector<HTMLElement>(".import-review__table")!);
    expect(review.queryByText("Einkauf")).not.toBeInTheDocument();
    expect(review.getByText("Haftpflicht")).toBeInTheDocument();

    // The table was open before: its cached rows must be reloaded after the import.
    useExplorerStore.setState((state) => ({
      views: { ...state.views, transactions: { ...freshTableView(), status: "ready" } },
    }));
    await user.click(screen.getByRole("button", { name: "Import 1 row" }));

    expect(api.runImport).toHaveBeenCalledWith("transactions", payload);
    expect(await screen.findByText("1 row imported into Transactions")).toBeInTheDocument();
    expect(useExplorerStore.getState().views.transactions?.status).toBe("idle");
    expect(screen.getByText("Skipped 1 row already in the table and 1 row with problems.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Open Transactions" })).toBeInTheDocument();
  });

  it("updates rows that changed at the source, showing what changes", async () => {
    const changed: ImportRunResult = {
      dryRun: true,
      total: 2,
      new: 1,
      duplicates: 0,
      invalid: 0,
      inserted: 0,
      changed: 1,
      rowsTruncated: false,
      rows: [
        { line: 4, status: "changed", display: { booked_on: "2026-09-01", purpose: "Einkauf", amount: "-46.10" }, errors: [], before: { amount: "-45.9" } },
        { line: 5, status: "new", display: { booked_on: "2026-09-04", purpose: "Strom", amount: "-60.00" }, errors: [] },
      ],
    };
    api.previewImport.mockResolvedValue(changed);
    api.runImport.mockResolvedValue({ ...changed, dryRun: false, inserted: 1, updated: 1 });
    const user = userEvent.setup({ pointerEventsCheck: 0 });
    render(<RouterProvider router={createTestRouter(["/data/transactions/import"])} />);
    await user.upload(await screen.findByLabelText("File to import"), new File(["x"], "girokonto.csv", { type: "text/csv" }));
    await waitFor(() => expect(api.analyzeImport).toHaveBeenCalledTimes(1));

    await user.click(screen.getByRole("checkbox", { name: /Update rows that changed/ }));
    await user.click(screen.getByRole("button", { name: "Check rows" }));
    expect(api.previewImport.mock.calls[0][1].updateChanged).toBe(true);

    const review = within(document.querySelector<HTMLElement>(".import-review__table")!);
    await review.findByText("-45.9");
    expect(review.getByText("-45.9").tagName).toBe("DEL");
    expect(review.getByText("-46.10").tagName).toBe("INS");
    expect(within(screen.getByRole("tablist", { name: "Rows by status" })).getByRole("tab", { name: /1\s*Changed/ })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Import 1 row and update 1" }));
    expect(await screen.findByText("1 row imported into Transactions, 1 updated")).toBeInTheDocument();
    expect(screen.getByText("Updated 1 row that changed. Every row of the file is in the table now.")).toBeInTheDocument();
  });

  it("reads an Excel file's sheet and lets another one be chosen", async () => {
    api.analyzeImport.mockImplementation(async (_content: string, settings: { sheet?: string }) => ({
      ...analysis,
      preamble: [],
      settings: { ...analysis.settings, encoding: "utf-8", delimiter: ",", headerLine: 0, decimalSeparator: ".", format: "xlsx", sheet: settings.sheet ?? "Konto", sheets: ["Konto", "Depot"] },
    }));
    const user = userEvent.setup({ pointerEventsCheck: 0 });
    render(<RouterProvider router={createTestRouter(["/data/transactions/import"])} />);
    await user.upload(await screen.findByLabelText("File to import"), new File(["x"], "konto.xlsx"));

    expect(await screen.findByLabelText("Sheet")).toHaveValue("Konto");
    expect(screen.queryByLabelText("Encoding")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Separator")).not.toBeInTheDocument();

    await user.selectOptions(screen.getByLabelText("Sheet"), "Depot");
    await waitFor(() => expect(api.analyzeImport).toHaveBeenCalledTimes(2));
    expect(api.analyzeImport.mock.calls[1][1]).toMatchObject({ sheet: "Depot", format: "xlsx" });
    expect(await screen.findByLabelText("Sheet")).toHaveValue("Depot");
  });

  it("fills a column with the same value for every row, a link by its record's name", async () => {
    dataApi.fetchTableRows.mockResolvedValue({ data: [{ id: 1, name: "Gehalt" }, { id: 2, name: "Lebensmittel" }], meta: { total: 2 } });
    const user = userEvent.setup({ pointerEventsCheck: 0 });
    render(<RouterProvider router={createTestRouter(["/data/transactions/import"])} />);
    await user.upload(await screen.findByLabelText("File to import"), new File(["x"], "konto.csv", { type: "text/csv" }));
    await waitFor(() => expect(screen.getByLabelText("File column for Category")).toHaveValue("Kategorie"));

    await user.selectOptions(screen.getByLabelText("File column for Category"), "Same value for every row…");
    const account = await screen.findByLabelText("Value of Category for every row");
    await waitFor(() => expect(within(account).getByRole("option", { name: "Lebensmittel" })).toBeInTheDocument());
    await user.selectOptions(account, "Lebensmittel");
    await user.selectOptions(screen.getByLabelText("File column for purpose"), "Same value for every row…");
    await user.type(screen.getByLabelText("Value of purpose for every row"), "Kontoauszug");

    await user.click(screen.getByRole("button", { name: "Check rows" }));
    expect(api.previewImport.mock.calls.at(-1)![1].mapping).toEqual([
      { source: "Buchungstag", target: "booked_on" },
      { target: "purpose", value: "Kontoauszug" },
      { source: "Betrag (EUR)", target: "amount" },
      { target: "category_id", value: "Lebensmittel" },
    ]);
  });

  it("makes a changed mapping invalidate an earlier check", async () => {
    const user = userEvent.setup({ pointerEventsCheck: 0 });
    render(<RouterProvider router={createTestRouter(["/data/transactions/import"])} />);

    await user.upload(await screen.findByLabelText("File to import"), new File(["x"], "a.csv", { type: "text/csv" }));
    await user.click(await screen.findByRole("button", { name: "Check rows" }));
    expect(await screen.findByRole("heading", { name: "Review" })).toBeInTheDocument();

    await user.selectOptions(screen.getByLabelText("File column for purpose"), "");

    expect(screen.queryByRole("heading", { name: "Review" })).not.toBeInTheDocument();
    expect(screen.getByLabelText("purpose identifies a row")).toBeDisabled();
  });

  it("applies a saved profile to the file", async () => {
    api.listImportProfiles.mockResolvedValue([
      {
        id: 3,
        name: "DKB",
        tableName: "transactions",
        createdAt: "",
        updatedAt: "",
        config: {
          settings: { encoding: "windows-1252", delimiter: ";" },
          mapping: [
            { source: "Buchungstag", target: "booked_on" },
            { source: "Betrag (EUR)", target: "amount" },
          ],
          keyColumns: ["booked_on", "amount"],
        },
      },
    ]);
    const user = userEvent.setup({ pointerEventsCheck: 0 });
    render(<RouterProvider router={createTestRouter(["/data/transactions/import"])} />);

    await user.selectOptions(await screen.findByLabelText("Profile"), "3");
    await user.upload(screen.getByLabelText("File to import"), new File(["x"], "a.csv", { type: "text/csv" }));

    await waitFor(() => expect(api.analyzeImport).toHaveBeenCalled());
    expect(api.analyzeImport.mock.calls[0][1]).toEqual({ encoding: "windows-1252", delimiter: ";" });
    expect(await screen.findByLabelText("File column for Booked on")).toHaveValue("Buchungstag");
    expect(screen.getByLabelText("File column for purpose")).toHaveValue("");
    expect(screen.getByRole("button", { name: "Update profile" })).toBeInTheDocument();
  });
});
