import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
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
    reader.onerror = () =>
      reject(reader.error ?? new Error("Unable to read blob contents."));
    reader.onload = () =>
      resolve(typeof reader.result === "string" ? reader.result : "");
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

function setScrollMetrics(
  element: HTMLElement,
  metrics: { scrollHeight: number; clientHeight: number; scrollTop?: number },
): void {
  let currentScrollTop = metrics.scrollTop ?? 0;

  Object.defineProperty(element, "scrollHeight", {
    configurable: true,
    get: () => metrics.scrollHeight,
  });
  Object.defineProperty(element, "clientHeight", {
    configurable: true,
    get: () => metrics.clientHeight,
  });
  Object.defineProperty(element, "scrollTop", {
    configurable: true,
    get: () => currentScrollTop,
    set: (value: number) => {
      currentScrollTop = value;
    },
  });
}

const populatedSchema = {
  version: 3,
  tables: [
    {
      name: "widgets",
      columns: [
        {
          name: "id",
          type: "integer",
          primaryKey: true,
          nullable: false,
          unique: true,
        },
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
        {
          name: "id",
          type: "integer",
          primaryKey: true,
          nullable: false,
          unique: true,
        },
      ],
      indexes: [],
    },
  ],
  relations: [],
};

const appliedSchema = {
  version: 4,
  tables: [
    {
      name: "widgets",
      columns: [
        {
          name: "id",
          type: "integer",
          primaryKey: true,
          nullable: false,
          unique: true,
        },
        { name: "name", type: "text", nullable: false },
        { name: "color", type: "text", nullable: true },
      ],
      indexes: [
        {
          name: "idx_widgets_name",
          columns: ["name"],
          unique: false,
        },
      ],
    },
    {
      name: "gadgets",
      columns: [
        {
          name: "id",
          type: "integer",
          primaryKey: true,
          nullable: false,
          unique: true,
        },
      ],
      indexes: [],
    },
  ],
  relations: [
    {
      name: "widget_notes",
      type: "1:N",
      from: "widgets",
      to: "gadgets",
    },
  ],
};

const destructiveAppliedSchema = {
  version: 4,
  tables: [
    {
      name: "widgets",
      columns: [
        {
          name: "id",
          type: "integer",
          primaryKey: true,
          nullable: false,
          unique: true,
        },
        { name: "name", type: "text", nullable: false },
        { name: "score", type: "integer", nullable: true },
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
      message:
        "Table 'widgets' is a SQL reserved word and may require quoting.",
      path: "tables[0].name",
    },
  ],
  changes: [],
  normalizedModel: populatedSchema,
};

const previewGroupedResponse = {
  isValid: true,
  hasChanges: true,
  isDestructive: false,
  requiresConfirmation: false,
  currentVersion: 3,
  errors: [],
  warnings: [],
  changes: [
    {
      changeType: "AddTable",
      table: "gadgets",
      isDestructive: false,
    },
    {
      changeType: "AddColumn",
      table: "widgets",
      column: "color",
      isDestructive: false,
    },
    {
      changeType: "AddRelation",
      table: "widgets",
      relation: "widget_notes",
      isDestructive: false,
    },
    {
      changeType: "AddIndex",
      table: "widgets",
      index: "idx_widgets_name",
      isDestructive: false,
    },
  ],
  normalizedModel: populatedSchema,
  plan: {
    sqlUp: ['ALTER TABLE "widgets" ADD COLUMN "color" TEXT;'],
    sqlDown: ['ALTER TABLE "widgets" DROP COLUMN "color";'],
    isDestructive: false,
    requiresConfirmation: false,
  },
};

const previewNoOpResponse = {
  isValid: true,
  hasChanges: false,
  isDestructive: false,
  requiresConfirmation: false,
  currentVersion: 3,
  errors: [],
  warnings: [],
  changes: [],
  normalizedModel: populatedSchema,
  plan: {
    sqlUp: [],
    sqlDown: [],
    isDestructive: false,
    requiresConfirmation: false,
  },
};

const previewLossyResponse = {
  isValid: true,
  hasChanges: true,
  isDestructive: true,
  requiresConfirmation: true,
  currentVersion: 3,
  errors: [],
  warnings: [],
  changes: [
    {
      changeType: "AlterColumnType",
      table: "widgets",
      column: "score",
      oldValue: "text",
      newValue: "integer",
      coercionKind: "Lossy",
      isDestructive: true,
      castExamples: [
        { sourceValue: "1,0", castValue: 1, isValid: true },
        { sourceValue: "2,0", castValue: 2, isValid: true },
        { sourceValue: "2,1", castValue: 2, isValid: true },
        { sourceValue: "two", castValue: 0, isValid: true },
        { sourceValue: "3,9", castValue: 3, isValid: true },
      ],
    },
  ],
  normalizedModel: populatedSchema,
  plan: {
    sqlUp: [
      'ALTER TABLE "widgets" ALTER COLUMN "score" TYPE BIGINT USING CAST("score" AS BIGINT);',
    ],
    sqlDown: [
      'ALTER TABLE "widgets" ALTER COLUMN "score" TYPE TEXT USING CAST("score" AS TEXT);',
    ],
    isDestructive: true,
    requiresConfirmation: true,
  },
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
    expect(
      screen.getByRole("toolbar", { name: "Schema workbench actions" }),
    ).toBeInTheDocument();
    expect(screen.queryByText("Schema workbench")).not.toBeInTheDocument();
    expect(screen.queryByText("Clean draft")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Preview" })).toBeEnabled();
    expect(screen.getByRole("button", { name: "Apply" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Format JSON" })).toBeEnabled();
    expect(
      screen.getByRole("heading", { name: "Diagnostics" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("heading", { name: "Preview" }),
    ).toBeInTheDocument();
    expect(screen.queryByTestId("model-json-highlight")).toBeNull();
    expect(useSchemaStore.getState().snapshot).toBeNull();

    replaceModelEditorValue(`${JSON.stringify(populatedSchema, null, 2)}\n`);

    expect(getModelEditorValue()).toBe(
      `${JSON.stringify(populatedSchema, null, 2)}\n`,
    );
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
    expect(
      screen.getByText(/sign in with an admin account/i),
    ).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("shows the loading shell while the current schema request is in flight", async () => {
    seedAuthenticated("admin");
    const never = new Promise<Response>(() => {});
    vi.stubGlobal("fetch", vi.fn().mockReturnValue(never));

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    expect(
      await screen.findByText(/loading current schema/i),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/fetching the current model json/i),
    ).toBeInTheDocument();
  });

  it("shows an empty-schema notice when no schema is currently applied", async () => {
    seedAuthenticated("admin");
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(jsonResponse(emptySchema)),
    );

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    expect(await screen.findByText("New workspace schema")).toBeInTheDocument();
    expect(getModelEditorValue()).toBe(JSON.stringify(emptySchema, null, 2));
  });

  it("formats valid JSON drafts into the canonical pretty-printed shape", async () => {
    seedAuthenticated("admin");
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(jsonResponse(populatedSchema)),
    );

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /model json draft/i });
    replaceModelEditorValue('{"version":1,"tables":[],"relations":[]}');

    expect(getModelEditorValue()).toBe(
      '{"version":1,"tables":[],"relations":[]}',
    );
    expect(screen.getByText("JSON syntax valid")).toBeInTheDocument();

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Format JSON" }));

    expect(getModelEditorValue()).toBe(
      '{\n  "version": 1,\n  "tables": [],\n  "relations": []\n}',
    );
  });

  it("exports the current draft as canonical JSON", async () => {
    seedAuthenticated("admin");
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(jsonResponse(populatedSchema)),
    );

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
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(jsonResponse(populatedSchema)),
    );

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /model json draft/i });
    replaceModelEditorValue(`${JSON.stringify(populatedSchema, null, 2)}\n`);

    const file = new File(
      [JSON.stringify(importedSchema)],
      "imported-model.json",
      {
        type: "application/json",
      },
    );
    fireEvent.change(screen.getByTestId("model-json-import-input"), {
      target: {
        files: [file],
      },
    });

    expect(
      await screen.findByRole("dialog", { name: "Replace the current draft?" }),
    ).toBeInTheDocument();
    expect(getModelEditorValue()).toBe(
      `${JSON.stringify(populatedSchema, null, 2)}\n`,
    );

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Replace draft" }));

    expect(getModelEditorValue()).toBe(JSON.stringify(importedSchema, null, 2));
  });

  it("keeps invalid JSON editable and shows a local parse error without breaking the page", async () => {
    seedAuthenticated("admin");
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(jsonResponse(populatedSchema)),
    );

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /model json draft/i });
    replaceModelEditorValue('{\n  "version": 1,\n}');

    expect(getModelEditorValue()).toBe('{\n  "version": 1,\n}');
    expect(screen.getByText("Syntax error")).toBeInTheDocument();
    expect(screen.getByText("Local JSON syntax")).toBeInTheDocument();
    expect(screen.getByText("json_parse_error")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Format JSON" })).toBeDisabled();
    expect(
      screen.getByText(/backend schema validation is paused/i),
    ).toBeInTheDocument();

    replaceModelEditorValue(
      '{\n  "version": 1,\n  "tables": [],\n  "relations": []\n}',
    );

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
    expect(
      screen.getByRole("heading", { name: "Warnings" }),
    ).toBeInTheDocument();
    expect(screen.getByText("missing_column_type")).toBeInTheDocument();
    expect(screen.getByText("reserved_table_name")).toBeInTheDocument();
    expect(screen.getByText("tables[0].columns[1].type")).toBeInTheDocument();
    expect(screen.getByText("tables[0].name")).toBeInTheDocument();

    await user.click(
      screen.getByRole("button", { name: /missing_column_type/i }),
    );

    await waitFor(() => {
      expect(getModelEditorView().state.selection.main.head).toBe(
        expectedIndex,
      );
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

  it("renders grouped additive preview sections after a successful preview", async () => {
    seedAuthenticated("admin");
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(jsonResponse(populatedSchema))
        .mockResolvedValueOnce(jsonResponse(previewGroupedResponse)),
    );

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /model json draft/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Preview" }));

    expect(
      screen.getByText("No destructive changes detected"),
    ).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Tables" })).toBeInTheDocument();
    expect(
      screen.getByRole("heading", { name: "Columns" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("heading", { name: "Relations" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("heading", { name: "Indexes" }),
    ).toBeInTheDocument();
    expect(screen.getByText("Add table gadgets")).toBeInTheDocument();
    expect(screen.getByText("Add column widgets.color")).toBeInTheDocument();
    expect(screen.getByText("Add relation widget_notes")).toBeInTheDocument();
    expect(screen.getByText("Add index idx_widgets_name")).toBeInTheDocument();
  });

  it("shows a clear no-op preview state when the draft matches the current schema", async () => {
    seedAuthenticated("admin");
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(jsonResponse(populatedSchema))
        .mockResolvedValueOnce(jsonResponse(previewNoOpResponse)),
    );

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /model json draft/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Preview" }));

    expect(await screen.findByText("No-op preview")).toBeInTheDocument();
    expect(screen.getByText(/matches the live schema/i)).toBeInTheDocument();
  });

  it("shows destructive lossy type changes with example casts", async () => {
    seedAuthenticated("admin");
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(jsonResponse(populatedSchema))
        .mockResolvedValueOnce(jsonResponse(previewLossyResponse)),
    );

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /model json draft/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Preview" }));

    expect(
      await screen.findByText("Destructive changes require confirmation"),
    ).toBeInTheDocument();
    expect(
      screen.getByText("Change type of widgets.score from text to integer"),
    ).toBeInTheDocument();
    expect(
      screen.getByText("Example Casts (5 first rows)"),
    ).toBeInTheDocument();
    expect(screen.getByText('"1,0" -> 1')).toBeInTheDocument();
    expect(screen.getByText('"2,0" -> 2')).toBeInTheDocument();
    expect(screen.getByText('"2,1" -> 2')).toBeInTheDocument();
    expect(screen.getByText('"two" -> 0')).toBeInTheDocument();
    expect(screen.getByText('"3,9" -> 3')).toBeInTheDocument();
  });

  it("applies a previewed draft through the review modal and refreshes the live schema snapshot", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse(populatedSchema))
      .mockResolvedValueOnce(jsonResponse(previewGroupedResponse))
      .mockResolvedValueOnce(
        jsonResponse({
          applied: true,
          version: 4,
          changes: previewGroupedResponse.changes,
          warnings: [],
        }),
      )
      .mockResolvedValueOnce(jsonResponse(appliedSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /model json draft/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Preview" }));
    await user.click(screen.getByRole("button", { name: "Apply" }));

    const dialog = await screen.findByRole("dialog", {
      name: "Review and apply schema changes",
    });
    expect(
      within(dialog).getByText("Add column widgets.color"),
    ).toBeInTheDocument();

    const review = within(dialog).getByTestId("schema-apply-review-scroll");
    setScrollMetrics(review, {
      scrollHeight: 1200,
      clientHeight: 400,
      scrollTop: 0,
    });
    fireEvent.scroll(review);

    const confirmButton = within(dialog).getByRole("button", { name: "Apply" });
    await waitFor(() => expect(confirmButton).toBeDisabled());

    review.scrollTop = 800;
    fireEvent.scroll(review);
    await waitFor(() => expect(confirmButton).toBeEnabled());

    await user.click(confirmButton);

    await waitFor(() => {
      expect(
        screen.queryByRole("dialog", { name: "Review and apply schema changes" }),
      ).not.toBeInTheDocument();
    });

    expect(
      await screen.findByText("Applied schema version 4"),
    ).toBeInTheDocument();
    expect(getModelEditorValue()).toBe(JSON.stringify(appliedSchema, null, 2));
    expect(useSchemaStore.getState().snapshot).toEqual(appliedSchema);
    expect(fetchMock).toHaveBeenNthCalledWith(
      3,
      "/api/v1/admin/schema",
      expect.objectContaining({
        method: "PUT",
        body: JSON.stringify(populatedSchema),
      }),
    );
    expect(fetchMock).toHaveBeenNthCalledWith(
      4,
      "/api/v1/admin/schema",
      expect.objectContaining({
        method: "GET",
      }),
    );
  });

  it("keeps the draft and shows diagnostics when apply fails validation", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse(populatedSchema))
      .mockResolvedValueOnce(jsonResponse(previewGroupedResponse))
      .mockResolvedValueOnce(
        jsonResponse(
          {
            error: {
              code: "VALIDATION_FAILED",
              message: "Schema validation failed.",
              details: {
                errors: [
                  {
                    code: "duplicate_table_name",
                    message: "Table 'widgets' already exists.",
                    path: "tables[1].name",
                  },
                ],
              },
            },
          },
          400,
        ),
      );
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /model json draft/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Preview" }));
    await user.click(screen.getByRole("button", { name: "Apply" }));

    const dialog = await screen.findByRole("dialog", {
      name: "Review and apply schema changes",
    });
    const review = within(dialog).getByTestId("schema-apply-review-scroll");
    setScrollMetrics(review, {
      scrollHeight: 1200,
      clientHeight: 400,
      scrollTop: 800,
    });
    fireEvent.scroll(review);

    await user.click(within(dialog).getByRole("button", { name: "Apply" }));

    await waitFor(() => {
      expect(
        screen.queryByRole("dialog", { name: "Review and apply schema changes" }),
      ).not.toBeInTheDocument();
    });

    expect(
      await screen.findByText("Apply blocked by validation errors"),
    ).toBeInTheDocument();
    expect(screen.getByText("Schema errors")).toBeInTheDocument();
    expect(screen.getByText("duplicate_table_name")).toBeInTheDocument();
    expect(screen.getByText("tables[1].name")).toBeInTheDocument();
    expect(getModelEditorValue()).toBe(JSON.stringify(populatedSchema, null, 2));
    expect(useSchemaStore.getState().snapshot).toBeNull();
    expect(fetchMock).toHaveBeenCalledTimes(3);
  });

  it("requires explicit destructive confirmation before resending apply with confirm_destructive", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse(populatedSchema))
      .mockResolvedValueOnce(jsonResponse(previewLossyResponse))
      .mockResolvedValueOnce(
        jsonResponse(
          {
            error: {
              code: "CONFIRMATION_REQUIRED",
              message:
                "Schema change includes destructive operations. Resend with ?confirm_destructive=true to apply.",
              details: {
                changes: [
                  {
                    changeType: "AlterColumnType",
                    table: "widgets",
                    column: "score",
                    isDestructive: true,
                  },
                ],
              },
            },
          },
          409,
        ),
      )
      .mockResolvedValueOnce(
        jsonResponse({
          applied: true,
          version: 4,
          changes: previewLossyResponse.changes,
          warnings: [],
        }),
      )
      .mockResolvedValueOnce(jsonResponse(destructiveAppliedSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /model json draft/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Preview" }));
    await user.click(screen.getByRole("button", { name: "Apply" }));

    const dialog = await screen.findByRole("dialog", {
      name: "Review and apply schema changes",
    });
    const review = within(dialog).getByTestId("schema-apply-review-scroll");
    setScrollMetrics(review, {
      scrollHeight: 1200,
      clientHeight: 400,
      scrollTop: 800,
    });
    fireEvent.scroll(review);

    await user.click(within(dialog).getByRole("button", { name: "Apply" }));

    expect(
      await within(dialog).findByText("Explicit destructive confirmation required"),
    ).toBeInTheDocument();

    const destructiveButton = within(dialog).getByRole("button", {
      name: "Apply destructive changes",
    });
    expect(destructiveButton).toBeEnabled();

    await user.click(destructiveButton);

    await waitFor(() => {
      expect(
        screen.queryByRole("dialog", { name: "Review and apply schema changes" }),
      ).not.toBeInTheDocument();
    });

    expect(
      await screen.findByText("Applied schema version 4"),
    ).toBeInTheDocument();
    expect(fetchMock).toHaveBeenNthCalledWith(
      3,
      "/api/v1/admin/schema",
      expect.objectContaining({
        method: "PUT",
      }),
    );
    expect(fetchMock).toHaveBeenNthCalledWith(
      4,
      "/api/v1/admin/schema?confirm_destructive=true",
      expect.objectContaining({
        method: "PUT",
      }),
    );
    expect(useSchemaStore.getState().snapshot).toEqual(destructiveAppliedSchema);
  });

  it("rejects invalid imported JSON without replacing the current draft", async () => {
    seedAuthenticated("admin");
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(jsonResponse(populatedSchema)),
    );

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
          {
            error: {
              code: "INTERNAL_ERROR",
              message: "Schema engine offline.",
            },
          },
          500,
        ),
      )
      .mockResolvedValueOnce(jsonResponse(populatedSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Schema engine offline.",
    );

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Retry" }));

    await waitFor(() => {
      expect(
        screen.getByRole("toolbar", { name: "Schema workbench actions" }),
      ).toBeInTheDocument();
    });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});
