import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ImportAnalysis, ImportRunResult } from "../api/import";
import { seedSetupInitializedForTest } from "../features/setup";
import { createTestRouter } from "../router";
import { resetAppStoreForTest } from "../stores/appStore";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import { resetSchemaStoreForTest, useSchemaStore } from "../stores/schemaStore";
import type { ModelSnapshot, SchemaModelDocument } from "../types/api";

const importApi = vi.hoisted(() => ({
  analyzeImport: vi.fn(),
  previewImport: vi.fn(),
  runImport: vi.fn(),
  listImportProfiles: vi.fn(),
}));
const schemaApi = vi.hoisted(() => ({
  getSchemaSnapshot: vi.fn(),
  getCanonicalSchemaSnapshot: vi.fn(),
  previewSchemaDraft: vi.fn(),
  applySchemaDraft: vi.fn(),
}));

vi.mock("../api/import", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../api/import")>()),
  ...importApi,
}));
vi.mock("../api/schema", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../api/schema")>()),
  ...schemaApi,
}));

const before = {
  version: 3,
  tables: [{ name: "customers", columns: [{ name: "id", type: "integer", primaryKey: true }, { name: "name", type: "text" }] }],
  relations: [],
} as unknown as SchemaModelDocument;

const analysis: ImportAnalysis = {
  settings: { encoding: "windows-1252", delimiter: ";", headerLine: 5, decimalSeparator: ",", dateFormat: "dd.MM.yyyy" },
  columns: [
    { index: 0, name: "Buchungstag", samples: ["01.09.2026"], inferredType: "date", blank: 0, distinctCount: 8, distinct: [] },
    { index: 1, name: "Betrag (EUR)", samples: ["-45,90"], inferredType: "float", blank: 0, distinctCount: 9, distinct: [] },
    { index: 2, name: "Kategorie", samples: ["Miete", "Lebensmittel"], inferredType: "text", blank: 0, distinctCount: 4, distinct: ["Miete", "Lebensmittel", "Gehalt", "Freizeit"] },
  ],
  preamble: [["Kontonummer:", "DE00"]],
  previewRows: [],
  rowCount: 10,
  options: { encodings: ["utf-8", "windows-1252"], delimiters: [";", ","], decimalSeparators: [",", "."], dateFormats: ["dd.MM.yyyy"] },
};

const preview: ImportRunResult = { dryRun: true, total: 10, new: 10, duplicates: 0, invalid: 0, inserted: 0, rows: [], rowsTruncated: false };

beforeEach(() => {
  resetAppStoreForTest();
  resetAuthStoreForTest();
  seedSetupInitializedForTest();
  resetSchemaStoreForTest();
  useAuthStore.setState({
    status: "authenticated",
    user: { id: 1, username: "admin", role: "admin" },
    accessToken: "token",
    expiresAt: Date.now() + 60_000,
    loginInFlight: false,
    error: null,
  });
  useSchemaStore.setState({ status: "ready", snapshot: before as unknown as ModelSnapshot, error: null, lastLoadedAt: Date.now(), inFlight: null });
  importApi.analyzeImport.mockResolvedValue(analysis);
  importApi.listImportProfiles.mockResolvedValue([]);
  importApi.previewImport.mockResolvedValue(preview);
  importApi.runImport.mockResolvedValue({ ...preview, dryRun: false, new: 4, inserted: 4, duplicates: 6 });
  schemaApi.getCanonicalSchemaSnapshot.mockResolvedValue(before);
  schemaApi.previewSchemaDraft.mockImplementation(async (draft: SchemaModelDocument) => ({
    isValid: true,
    hasChanges: true,
    isDestructive: false,
    requiresConfirmation: false,
    currentVersion: 3,
    errors: [],
    warnings: [],
    changes: [],
    normalizedModel: draft,
  }));
  schemaApi.applySchemaDraft.mockImplementation(async (draft: SchemaModelDocument) => {
    schemaApi.getSchemaSnapshot.mockResolvedValue({ ...draft, version: 4 });
    return { applied: true, version: 4, changes: [], warnings: [] };
  });
  vi.stubGlobal("fetch", vi.fn(() => Promise.reject(new Error("fetch must be mocked in tests"))));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe("New table from CSV", () => {
  it("proposes a table, creates it with a lookup table and continues into the import", async () => {
    const user = userEvent.setup({ pointerEventsCheck: 0 });
    render(<RouterProvider router={createTestRouter(["/data/new/csv"])} />);

    await user.upload(
      await screen.findByLabelText("CSV file"),
      new File(["x"], "girokonto-2026-09.csv", { type: "text/csv" }),
    );

    expect(await screen.findByLabelText("Column name for Betrag (EUR)")).toHaveValue("betrag_eur");
    expect(screen.getByLabelText("Stored as for Betrag (EUR)")).toHaveValue("currency");
    expect(screen.getByLabelText("Stored as for Kategorie")).toHaveValue("new-table");
    expect(screen.getByLabelText("New table name for Kategorie")).toHaveValue("kategorie");
    expect(screen.getByLabelText(/^Name/)).toHaveValue("girokonto");

    await user.clear(screen.getByLabelText("Display name"));
    await user.type(screen.getByLabelText("Display name"), "Girokonto DKB");
    await user.click(screen.getByRole("button", { name: "Create table and continue" }));

    await waitFor(() => expect(schemaApi.applySchemaDraft).toHaveBeenCalledTimes(1));
    const applied = schemaApi.applySchemaDraft.mock.calls[0][0] as SchemaModelDocument;
    expect(applied.tables.map((t) => t.name)).toEqual(["customers", "kategorie", "girokonto"]);
    expect(applied.tables[2].displayName).toBe("Girokonto DKB");
    expect(applied.relations).toEqual([
      { name: "kategorie_girokonto", type: "1:N", from: "kategorie", to: "girokonto", column: "kategorie_id" },
    ]);

    await waitFor(() => expect(importApi.runImport).toHaveBeenCalledTimes(1));
    expect(importApi.runImport.mock.calls[0][0]).toBe("kategorie");
    expect(importApi.runImport.mock.calls[0][1]).toMatchObject({
      mapping: [{ source: "Kategorie", target: "name" }],
      keyColumns: ["name"],
      distinctKeys: true,
    });

    expect(await screen.findByRole("heading", { name: "Import into Girokonto DKB" })).toBeInTheDocument();
    // The note arrives with the hand-off, a moment after the heading.
    expect(await screen.findByText(/Created Girokonto DKB\. 4 values added to Kategorie\./)).toBeInTheDocument();
    await waitFor(() => expect(importApi.previewImport).toHaveBeenCalledTimes(1));
    expect(importApi.previewImport.mock.calls[0][0]).toBe("girokonto");
    expect(importApi.previewImport.mock.calls[0][1].mapping).toEqual([
      { source: "Buchungstag", target: "buchungstag" },
      { source: "Betrag (EUR)", target: "betrag_eur" },
      { source: "Kategorie", target: "kategorie_id" },
    ]);
    expect(importApi.previewImport.mock.calls[0][1].keyColumns).toEqual(["buchungstag", "betrag_eur"]);
    expect(await screen.findByRole("button", { name: "Import 10 rows" })).toBeInTheDocument();
  });

  it("blocks a name that is already taken", async () => {
    const user = userEvent.setup({ pointerEventsCheck: 0 });
    render(<RouterProvider router={createTestRouter(["/data/new/csv"])} />);
    await user.upload(await screen.findByLabelText("CSV file"), new File(["x"], "a.csv", { type: "text/csv" }));

    const name = await screen.findByLabelText(/^Name/);
    await user.clear(name);
    await user.type(name, "customers");

    expect(screen.getByText('A table named "customers" already exists.')).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Create table and continue" })).toBeDisabled();
  });

  it("explains that creating tables needs the admin role", async () => {
    const { ApiClientError } = await import("../api/client");
    schemaApi.applySchemaDraft.mockRejectedValue(new ApiClientError("Forbidden", 403, "FORBIDDEN"));
    const user = userEvent.setup({ pointerEventsCheck: 0 });
    render(<RouterProvider router={createTestRouter(["/data/new/csv"])} />);
    await user.upload(await screen.findByLabelText("CSV file"), new File(["x"], "a.csv", { type: "text/csv" }));

    await user.click(await screen.findByRole("button", { name: "Create table and continue" }));

    expect(await screen.findByText("Creating tables needs the admin role.")).toBeInTheDocument();
    expect(importApi.runImport).not.toHaveBeenCalled();
  });
});
