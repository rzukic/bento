import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { EditorView } from "@codemirror/view";
import { createTestRouter } from "../router";
import { resetAppStoreForTest } from "../stores/appStore";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import { resetSchemaStoreForTest, useSchemaStore } from "../stores/schemaStore";

function seedAuthenticated(role = "admin") {
  useAuthStore.setState({
    status: "authenticated",
    user: { id: 1, username: role, role },
    accessToken: "test-access-token",
    expiresAt: Date.now() + 60_000,
    loginInFlight: false,
    error: null,
    refreshInFlight: null,
  });
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function readBlobText(blob: Blob): Promise<string> {
  if (typeof blob.text === "function") {
    return blob.text();
  }

  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => reject(reader.error ?? new Error("Unable to read blob contents."));
    reader.onload = () => resolve(typeof reader.result === "string" ? reader.result : "");
    reader.readAsText(blob);
  });
}

function getModelEditorElement(): HTMLElement {
  return screen.getByRole("textbox", { name: /model json draft/i });
}

function getModelEditorView(): EditorView {
  const element = getModelEditorElement();
  const view = EditorView.findFromDOM(element);

  if (!view) {
    throw new Error("Expected CodeMirror editor view to be mounted.");
  }

  return view;
}

function getModelEditorValue(): string {
  return getModelEditorView().state.doc.toString();
}

function replaceModelEditorValue(value: string): void {
  const view = getModelEditorView();
  act(() => {
    view.dispatch({
      changes: {
        from: 0,
        to: view.state.doc.length,
        insert: value,
      },
    });
  });
}

const populatedSchema = {
  version: 3,
  tables: [
    {
      name: "widgets",
      columns: [
        { name: "id", type: "integer", primaryKey: true, nullable: false, unique: true },
        { name: "name", type: "text", nullable: false },
      ],
      indexes: [],
    },
  ],
  relations: [],
};

const emptySchema = {
  version: 0,
  tables: [],
  relations: [],
};

const importedSchema = {
  version: 4,
  tables: [
    {
      name: "gadgets",
      columns: [
        { name: "id", type: "integer", primaryKey: true, nullable: false, unique: true },
      ],
      indexes: [],
    },
  ],
  relations: [],
};

const previewDiagnosticsResponse = {
  isValid: false,
  hasChanges: false,
  isDestructive: false,
  requiresConfirmation: false,
  currentVersion: 3,
  errors: [
    {
      code: "missing_column_type",
      message: "Column 'widgets.name' is missing a required type.",
      path: "tables[0].columns[1].type",
    },
  ],
  warnings: [
    {
      code: "reserved_table_name",
      message: "Table 'widgets' is a SQL reserved word and may require quoting.",
      path: "tables[0].name",
    },
  ],
  changes: [],
  normalizedModel: populatedSchema,
};

beforeEach(() => {
  resetAppStoreForTest();
  resetAuthStoreForTest();
  resetSchemaStoreForTest();
  vi.stubGlobal(
    "fetch",
    vi.fn(() => {
      throw new Error("fetch must be mocked in tests");
    }),
  );
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("Model workbench route (/model)", () => {
  it("loads the current schema into a local draft workbench shell for admins", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(populatedSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /model json draft/i });
    expect(getModelEditorValue()).toBe(
      JSON.stringify(populatedSchema, null, 2),
    );
    expect(screen.getByRole("toolbar", { name: "Schema workbench actions" })).toBeInTheDocument();
    expect(screen.queryByText("Schema workbench")).not.toBeInTheDocument();
    expect(screen.queryByText("Clean draft")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Preview" })).toBeEnabled();
    expect(screen.getByRole("button", { name: "Apply" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Format JSON" })).toBeEnabled();
    expect(screen.getByRole("heading", { name: "Diagnostics" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Preview" })).toBeInTheDocument();
    expect(screen.queryByTestId("model-json-highlight")).toBeNull();
    expect(useSchemaStore.getState().snapshot).toBeNull();

    replaceModelEditorValue(`${JSON.stringify(populatedSchema, null, 2)}\n`);

    expect(getModelEditorValue()).toBe(`${JSON.stringify(populatedSchema, null, 2)}\n`);
    expect(fetchMock).toHaveBeenCalledWith(
      "/api/v1/admin/schema",
      expect.objectContaining({
        method: "GET",
        credentials: "include",
      }),
    );
  });

  it("shows a clear access denied state for non-admin users without fetching", () => {
    seedAuthenticated("editor");
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    expect(screen.getByText("Admin access required")).toBeInTheDocument();
    expect(screen.getByText(/sign in with an admin account/i)).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("shows the loading shell while the current schema request is in flight", async () => {
    seedAuthenticated("admin");
    const never = new Promise<Response>(() => {});
    vi.stubGlobal("fetch", vi.fn().mockReturnValue(never));

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    expect(await screen.findByText(/loading current schema/i)).toBeInTheDocument();
    expect(screen.getByText(/fetching the current model json/i)).toBeInTheDocument();
  });

  it("shows an empty-schema notice when no schema is currently applied", async () => {
    seedAuthenticated("admin");
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse(emptySchema)));

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    expect(await screen.findByText("New workspace schema")).toBeInTheDocument();
    expect(getModelEditorValue()).toBe(JSON.stringify(emptySchema, null, 2));
  });

  it("formats valid JSON drafts into the canonical pretty-printed shape", async () => {
    seedAuthenticated("admin");
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse(populatedSchema)));

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /model json draft/i });
    replaceModelEditorValue('{"version":1,"tables":[],"relations":[]}');

    expect(getModelEditorValue()).toBe('{"version":1,"tables":[],"relations":[]}');
    expect(screen.getByText("JSON syntax valid")).toBeInTheDocument();

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Format JSON" }));

    expect(getModelEditorValue()).toBe('{\n  "version": 1,\n  "tables": [],\n  "relations": []\n}');
  });

  it("exports the current draft as canonical JSON", async () => {
    seedAuthenticated("admin");
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse(populatedSchema)));

    let capturedBlob: Blob | null = null;
    const createObjectUrl = vi.fn((blob: Blob) => {
      capturedBlob = blob;
      return "blob:model-export";
    });
    const revokeObjectUrl = vi.fn();
    const originalCreateObjectUrl = URL.createObjectURL;
    const originalRevokeObjectUrl = URL.revokeObjectURL;
    Object.defineProperty(URL, "createObjectURL", {
      configurable: true,
      writable: true,
      value: createObjectUrl,
    });
    Object.defineProperty(URL, "revokeObjectURL", {
      configurable: true,
      writable: true,
      value: revokeObjectUrl,
    });
    const anchorClick = vi
      .spyOn(HTMLAnchorElement.prototype, "click")
      .mockImplementation(() => {});

    try {
      const router = createTestRouter(["/model"]);
      render(<RouterProvider router={router} />);

      await screen.findByRole("textbox", { name: /model json draft/i });

      const user = userEvent.setup();
      await user.click(screen.getByRole("button", { name: "Export" }));

      await waitFor(() => expect(capturedBlob).not.toBeNull());

      expect(anchorClick).toHaveBeenCalledTimes(1);
      expect(createObjectUrl).toHaveBeenCalledTimes(1);
      expect(revokeObjectUrl).toHaveBeenCalledTimes(1);
      expect(capturedBlob).not.toBeNull();
      expect(await readBlobText(capturedBlob as unknown as Blob)).toBe(
        JSON.stringify(populatedSchema, null, 2),
      );
    } finally {
      Object.defineProperty(URL, "createObjectURL", {
        configurable: true,
        writable: true,
        value: originalCreateObjectUrl,
      });
      Object.defineProperty(URL, "revokeObjectURL", {
        configurable: true,
        writable: true,
        value: originalRevokeObjectUrl,
      });
    }
  });

  it("confirms before replacing a dirty draft with imported JSON", async () => {
    seedAuthenticated("admin");
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse(populatedSchema)));

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /model json draft/i });
    replaceModelEditorValue(`${JSON.stringify(populatedSchema, null, 2)}\n`);

    const file = new File([JSON.stringify(importedSchema)], "imported-model.json", {
      type: "application/json",
    });
    fireEvent.change(screen.getByTestId("model-json-import-input"), {
      target: {
        files: [file],
      },
    });

    expect(
      await screen.findByRole("dialog", { name: "Replace the current draft?" }),
    ).toBeInTheDocument();
    expect(getModelEditorValue()).toBe(`${JSON.stringify(populatedSchema, null, 2)}\n`);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Replace draft" }));

    expect(getModelEditorValue()).toBe(JSON.stringify(importedSchema, null, 2));
  });

  it("keeps invalid JSON editable and shows a local parse error without breaking the page", async () => {
    seedAuthenticated("admin");
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse(populatedSchema)));

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /model json draft/i });
    replaceModelEditorValue('{\n  "version": 1,\n}');

    expect(getModelEditorValue()).toBe('{\n  "version": 1,\n}');
    expect(screen.getByText("Syntax error")).toBeInTheDocument();
    expect(screen.getByText("Local JSON syntax")).toBeInTheDocument();
    expect(screen.getByText("json_parse_error")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Format JSON" })).toBeDisabled();
    expect(screen.getByText(/backend schema validation is paused/i)).toBeInTheDocument();

    replaceModelEditorValue('{\n  "version": 1,\n  "tables": [],\n  "relations": []\n}');

    expect(screen.queryByText("Syntax error")).not.toBeInTheDocument();
    expect(screen.getByText("JSON syntax valid")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Format JSON" })).toBeEnabled();
  });

  it("renders backend validation diagnostics and jumps to mapped lines after preview", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse(populatedSchema))
      .mockResolvedValueOnce(jsonResponse(previewDiagnosticsResponse));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /model json draft/i });
    const draftText = JSON.stringify(populatedSchema, null, 2);
    const expectedIndex = draftText.indexOf('"type": "text"');
    const expectedLine = draftText.slice(0, expectedIndex).split("\n").length;

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Preview" }));

    expect(await screen.findByText("Schema errors")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Warnings" })).toBeInTheDocument();
    expect(screen.getByText("missing_column_type")).toBeInTheDocument();
    expect(screen.getByText("reserved_table_name")).toBeInTheDocument();
    expect(screen.getByText("tables[0].columns[1].type")).toBeInTheDocument();
    expect(screen.getByText("tables[0].name")).toBeInTheDocument();

    await user.click(
      screen.getByRole("button", { name: /missing_column_type/i }),
    );

    await waitFor(() => {
      expect(getModelEditorView().state.selection.main.head).toBe(expectedIndex);
      expect(
        document.querySelector(
          `.schema-json-editor__gutter-line--active[data-line="${expectedLine}"]`,
        ),
      ).not.toBeNull();
    });

    expect(fetchMock).toHaveBeenNthCalledWith(
      2,
      "/api/v1/admin/schema/preview",
      expect.objectContaining({
        method: "POST",
        body: JSON.stringify(populatedSchema),
      }),
    );
  });

  it("rejects invalid imported JSON without replacing the current draft", async () => {
    seedAuthenticated("admin");
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse(populatedSchema)));

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /model json draft/i });
    const originalText = JSON.stringify(populatedSchema, null, 2);

    const file = new File(['{\n  "version": 1,\n}'], "broken-model.json", {
      type: "application/json",
    });
    fireEvent.change(screen.getByTestId("model-json-import-input"), {
      target: {
        files: [file],
      },
    });

    expect(await screen.findByText("Import failed")).toBeInTheDocument();
    expect(screen.getByText(/broken-model\.json/i)).toBeInTheDocument();
    expect(getModelEditorValue()).toBe(originalText);
  });

  it("shows a load error and retries back into the workbench shell", async () => {
    seedAuthenticated("admin");

    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(
        jsonResponse(
          { error: { code: "INTERNAL_ERROR", message: "Schema engine offline." } },
          500,
        ),
      )
      .mockResolvedValueOnce(jsonResponse(populatedSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    expect(await screen.findByRole("alert")).toHaveTextContent("Schema engine offline.");

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Retry" }));

    await waitFor(() => {
      expect(screen.getByRole("toolbar", { name: "Schema workbench actions" })).toBeInTheDocument();
    });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});
