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
import { resetAppStoreForTest, useAppStore } from "../stores/appStore";
import { resetRememberedCanvasViewport } from "../features/schema-workbench/visual/VisualSchemaMode";
import { seedSetupInitializedForTest } from "../features/setup";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import { resetSchemaStoreForTest, useSchemaStore } from "../stores/schemaStore";
import { useWorkspacesStore } from "../stores/workspacesStore";
import { setCurrentWorkspaceForTests } from "../lib/workspace";
import type { SchemaModelDocument } from "../types/api";
import { addVisibleRenameHintFields } from "../features/schema-workbench/schemaDraftPresentation";

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
  return screen.getByRole("textbox", { name: /schema json editor/i });
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

function getVisibleSchemaDraft<T>(document: T): T {
  return addVisibleRenameHintFields(document as SchemaModelDocument) as T;
}

function getVisibleSchemaDraftText(document: unknown): string {
  return JSON.stringify(getVisibleSchemaDraft(document), null, 2);
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
          type: 1,
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

const visualCreatedSchema = {
  version: 0,
  tables: [
    {
      name: "customers",
      columns: [
        {
          name: "id",
          type: "integer",
          primaryKey: true,
          nullable: false,
        },
        { name: "name", type: "text", nullable: true },
      ],
      indexes: [],
      layout: { x: 80, y: 80 },
    },
    {
      name: "orders",
      columns: [
        {
          name: "id",
          type: "integer",
          primaryKey: true,
          nullable: false,
        },
        { name: "total", type: "integer", nullable: true },
        {
          name: "customer_id",
          type: "reference",
          references: "customers",
          nullable: false,
          relationName: "customer_orders",
        },
      ],
      indexes: [
        {
          name: "idx_orders_customer_id",
          columns: ["customer_id"],
        },
      ],
      layout: { x: 400, y: 80 },
    },
  ],
  relations: [
    {
      name: "customer_orders",
      type: "1:N",
      from: "customers",
      to: "orders",
      column: "customer_id",
      fromAccessor: "orders",
      toAccessor: "customer",
    },
  ],
};

const visualCreatedAppliedSchema = {
  ...visualCreatedSchema,
  version: 1,
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

const appliedSchemaWithResolvedRelationMetadata = {
  ...appliedSchema,
  relations: appliedSchema.relations.map((relation) => ({
    ...relation,
    resolvedFromAccessor: "notes",
    resolvedToAccessor: "widget",
    resolvedJunctionTableName: "_jt_widgets_gadgets",
  })),
};

const visualRelationSchema = {
  version: 5,
  tables: [
    {
      name: "users",
      layout: { x: 80, y: 80, color: "#2563eb" },
      columns: [
        { name: "id", type: "integer", primaryKey: true, nullable: false },
        { name: "manager_id", type: "reference", references: "users" },
        { name: "account_id", type: "reference", references: "accounts" },
      ],
      indexes: [],
    },
    {
      name: "accounts",
      layout: { x: 500, y: 100 },
      columns: [
        { name: "id", type: "integer", primaryKey: true, nullable: false },
        { name: "name", type: "text" },
      ],
      indexes: [],
    },
    {
      name: "groups",
      layout: { x: 500, y: 410 },
      columns: [
        { name: "id", type: "integer", primaryKey: true, nullable: false },
        { name: "name", type: "text" },
      ],
      indexes: [],
    },
  ],
  relations: [
    {
      name: "account_users",
      type: "1:N",
      from: "accounts",
      to: "users",
      column: "account_id",
    },
    {
      name: "user_groups",
      type: "N:M",
      from: "users",
      to: "groups",
    },
    {
      name: "user_manager",
      type: "1:N",
      from: "users",
      to: "users",
      column: "manager_id",
    },
  ],
};

const visualRelationCreationSchema = {
  version: 5,
  tables: [
    {
      name: "customers",
      layout: { x: 80, y: 80 },
      columns: [
        { name: "id", type: "integer", primaryKey: true, nullable: false },
        { name: "name", type: "text" },
      ],
      indexes: [],
    },
    {
      name: "orders",
      layout: { x: 460, y: 100 },
      columns: [
        { name: "id", type: "integer", primaryKey: true, nullable: false },
      ],
      indexes: [],
    },
  ],
  relations: [],
};

const renamedColumnDraftSchema = {
  ...populatedSchema,
  tables: [
    {
      ...populatedSchema.tables[0],
      columns: [
        populatedSchema.tables[0].columns[0],
        {
          ...populatedSchema.tables[0].columns[1],
          name: "display_name",
        },
      ],
    },
  ],
};

const renamedColumnRequestSchema = {
  ...renamedColumnDraftSchema,
  tables: [
    {
      ...renamedColumnDraftSchema.tables[0],
      columns: [
        renamedColumnDraftSchema.tables[0].columns[0],
        {
          ...renamedColumnDraftSchema.tables[0].columns[1],
          renamedFrom: "name",
        },
      ],
    },
  ],
};

const renamedColumnAppliedSchema = {
  ...renamedColumnDraftSchema,
  version: 4,
};

const renamedPrimaryKeyDraftSchema = {
  ...populatedSchema,
  tables: [
    {
      ...populatedSchema.tables[0],
      columns: [
        {
          ...populatedSchema.tables[0].columns[0],
          name: "account_id",
        },
        populatedSchema.tables[0].columns[1],
      ],
    },
  ],
};

const renamedPrimaryKeyRequestSchema = {
  ...renamedPrimaryKeyDraftSchema,
  tables: [
    {
      ...renamedPrimaryKeyDraftSchema.tables[0],
      columns: [
        {
          ...renamedPrimaryKeyDraftSchema.tables[0].columns[0],
          renamedFrom: "id",
        },
        renamedPrimaryKeyDraftSchema.tables[0].columns[1],
      ],
    },
  ],
};

const movedPrimaryKeyDraftSchema = {
  ...populatedSchema,
  tables: [
    {
      ...populatedSchema.tables[0],
      columns: [
        {
          ...populatedSchema.tables[0].columns[0],
          primaryKey: false,
          unique: false,
        },
        {
          ...populatedSchema.tables[0].columns[1],
          primaryKey: true,
        },
      ],
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

const lossySourceSchema = {
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
        { name: "score", type: "text", nullable: true },
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
  normalizedModel: appliedSchema,
  plan: {
    sqlUp: ['ALTER TABLE "widgets" ADD COLUMN "color" TEXT;'],
    sqlDown: ['ALTER TABLE "widgets" DROP COLUMN "color";'],
    isDestructive: false,
    requiresConfirmation: false,
  },
};

const previewVisualCreateResponse = {
  isValid: true,
  hasChanges: true,
  isDestructive: false,
  requiresConfirmation: false,
  currentVersion: 0,
  errors: [],
  warnings: [],
  changes: [
    {
      changeType: "AddTable",
      table: "customers",
      isDestructive: false,
    },
    {
      changeType: "AddTable",
      table: "orders",
      isDestructive: false,
    },
    {
      changeType: "AddRelation",
      table: "customers",
      relation: "customer_orders",
      isDestructive: false,
    },
  ],
  normalizedModel: visualCreatedSchema,
  plan: {
    sqlUp: [
      'CREATE TABLE "customers" ("id" INTEGER PRIMARY KEY, "name" TEXT);',
      'CREATE TABLE "orders" ("id" INTEGER PRIMARY KEY, "total" INTEGER, "customer_id" INTEGER NOT NULL);',
    ],
    sqlDown: ['DROP TABLE "orders";', 'DROP TABLE "customers";'],
    isDestructive: false,
    requiresConfirmation: false,
  },
};

const relationDropDraftSchema = {
  ...appliedSchema,
  relations: [],
};

const previewDestructiveRelationDropResponse = {
  isValid: true,
  hasChanges: true,
  isDestructive: true,
  requiresConfirmation: true,
  currentVersion: 4,
  errors: [],
  warnings: [],
  changes: [
    {
      changeType: "DropRelation",
      table: "gadgets",
      relation: "widget_notes",
      impact: {
        kind: "many_to_many_junction_drop",
        objectType: "table",
        objectName: "_jt_gadgets_widgets",
        rowCount: 2,
        sampleRows: [
          { gadget_id: 1, widget_id: 1 },
          { gadget_id: 2, widget_id: 1 },
        ],
        message:
          'Applying this change drops the junction rows stored for relation "widget_notes".',
      },
      isDestructive: true,
    },
  ],
  normalizedModel: relationDropDraftSchema,
  plan: {
    sqlUp: ['DROP TABLE "_jt_gadgets_widgets";'],
    sqlDown: [
      'CREATE TABLE "_jt_gadgets_widgets" ("gadget_id" INTEGER NOT NULL, "widget_id" INTEGER NOT NULL);',
    ],
    isDestructive: true,
    requiresConfirmation: true,
  },
};

const previewRenameResponse = {
  isValid: true,
  hasChanges: true,
  isDestructive: false,
  requiresConfirmation: false,
  currentVersion: 3,
  errors: [],
  warnings: [],
  changes: [
    {
      changeType: "RenameColumn",
      table: "widgets",
      column: "display_name",
      previousColumn: "name",
      isDestructive: false,
    },
  ],
  normalizedModel: renamedColumnAppliedSchema,
  plan: {
    sqlUp: ['ALTER TABLE "widgets" RENAME COLUMN "name" TO "display_name";'],
    sqlDown: ['ALTER TABLE "widgets" RENAME COLUMN "display_name" TO "name";'],
    isDestructive: false,
    requiresConfirmation: false,
  },
};

const previewUnhintedSameSlotRenameResponse = {
  isValid: true,
  hasChanges: true,
  isDestructive: true,
  requiresConfirmation: true,
  currentVersion: 3,
  errors: [],
  warnings: [],
  changes: [
    {
      changeType: "DropColumn",
      table: "widgets",
      column: "name",
      isDestructive: true,
    },
    {
      changeType: "AddColumn",
      table: "widgets",
      column: "display_name",
      isDestructive: false,
    },
  ],
  normalizedModel: renamedColumnDraftSchema,
  plan: {
    sqlUp: [
      'ALTER TABLE "widgets" DROP COLUMN "name";',
      'ALTER TABLE "widgets" ADD COLUMN "display_name" TEXT NOT NULL;',
    ],
    sqlDown: [
      'ALTER TABLE "widgets" DROP COLUMN "display_name";',
      'ALTER TABLE "widgets" ADD COLUMN "name" TEXT NOT NULL;',
    ],
    isDestructive: true,
    requiresConfirmation: true,
  },
};

const previewPrimaryKeyRenameBlockedResponse = {
  isValid: false,
  hasChanges: true,
  isDestructive: false,
  requiresConfirmation: false,
  currentVersion: 3,
  errors: [
    {
      code: "primary_key_rename_unsupported",
      message:
        "Primary key column rename is not supported for table 'widgets'. Keep the primary key column named 'id'.",
      path: "tables[0].columns[0].name",
    },
  ],
  warnings: [],
  changes: [
    {
      changeType: "RenameColumn",
      table: "widgets",
      column: "account_id",
      previousColumn: "id",
      isDestructive: false,
    },
  ],
  normalizedModel: renamedPrimaryKeyDraftSchema,
};

const previewMovedPrimaryKeyBlockedResponse = {
  isValid: false,
  hasChanges: false,
  isDestructive: false,
  requiresConfirmation: false,
  currentVersion: 3,
  errors: [
    {
      code: "primary_key_must_be_id",
      message:
        "Table 'widgets' uses 'name' as the primary key. User tables support only the 'id' column as primaryKey.",
      path: "tables[0].columns[1].primaryKey",
    },
  ],
  warnings: [],
  changes: [],
  normalizedModel: movedPrimaryKeyDraftSchema,
};

const previewNoOpResponse = {
  isValid: true,
  hasChanges: false,
  metadataOnly: false,
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

const previewNoOpWarningResponse = {
  ...previewNoOpResponse,
  warnings: [
    {
      code: "reserved_table_name",
      message:
        "Table 'widgets' is a SQL reserved word and may require quoting.",
      path: "tables[0].name",
    },
  ],
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
  normalizedModel: destructiveAppliedSchema,
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
  seedSetupInitializedForTest();
  resetSchemaStoreForTest();
  resetRememberedCanvasViewport();
  // The page opens in Visual mode by default and remembers the last mode;
  // these tests start from the JSON editor.
  useAppStore.setState({ schemaMode: "json" });
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
  it("says which workspace Apply changes when there are several", async () => {
    seedAuthenticated("admin");
    vi.stubGlobal("fetch", vi.fn().mockImplementation(() => Promise.resolve(jsonResponse(populatedSchema))));
    useWorkspacesStore.setState({
      workspaces: [
        { id: 1, slug: "default", name: "Default", isDefault: true },
        { id: 2, slug: "finances", name: "Finances", isDefault: false },
      ],
    });
    setCurrentWorkspaceForTests("finances");
    try {
      render(<RouterProvider router={createTestRouter(["/model"])} />);
      await screen.findByRole("textbox", { name: /schema json editor/i });
      expect(screen.getByRole("button", { name: "Apply to Finances" })).toBeDisabled();
      // The sidebar and the top bar name it, and the tab.
      expect(screen.getAllByRole("button", { name: /Workspace: Finances/ })).toHaveLength(2);
      await waitFor(() => expect(document.title).toBe("Schema · Finances · Bento"));
    } finally {
      setCurrentWorkspaceForTests(null);
      useWorkspacesStore.getState().reset();
    }
  });

  it("loads the current schema into a local draft workbench shell for admins", async () => {
    seedAuthenticated("admin");
    // A fresh answer per request: the sidebar asks for the workspaces too.
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(jsonResponse(populatedSchema)));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });
    expect(getModelEditorValue()).toBe(
      getVisibleSchemaDraftText(populatedSchema),
    );
    expect(getModelEditorValue()).toContain('"renamedFrom": "widgets"');
    expect(getModelEditorValue()).toContain('"renamedFrom": "name"');
    expect(
      screen.getByRole("toolbar", { name: "Schema workbench actions" }),
    ).toBeInTheDocument();
    expect(screen.queryByText("Schema workbench")).not.toBeInTheDocument();
    expect(screen.queryByText("Clean draft")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Preview" })).toBeEnabled();
    expect(screen.getByRole("button", { name: "Apply" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Format JSON" })).toBeEnabled();
    expect(
      screen.queryByRole("heading", { name: "Diagnostics" }),
    ).not.toBeInTheDocument();
    // The preview panel opens on request only.
    expect(
      screen.queryByRole("heading", { name: "Preview" }),
    ).not.toBeInTheDocument();
    expect(screen.queryByTestId("model-json-highlight")).toBeNull();
    expect(useSchemaStore.getState().snapshot).toBeNull();

    replaceModelEditorValue(`${JSON.stringify(populatedSchema, null, 2)}\n`);

    expect(getModelEditorValue()).toBe(
      `${JSON.stringify(populatedSchema, null, 2)}\n`,
    );
    expect(fetchMock).toHaveBeenCalledWith(
      "/api/v1/admin/schema/model",
      expect.objectContaining({
        method: "GET",
        credentials: "include",
      }),
    );
  });

  it("opens on the diagram with no side panel until something is selected or Preview is pressed", async () => {
    seedAuthenticated("admin");
    useAppStore.setState({ schemaMode: "visual" });
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(populatedSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    const visualMode = await screen.findByTestId("schema-visual-mode");
    expect(screen.getByRole("heading", { level: 1, name: "Schema" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Visual" })).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByText("No unapplied changes")).toBeInTheDocument();
    expect(screen.queryByRole("complementary", { name: /Edit selection|Changes/ })).not.toBeInTheDocument();

    const user = userEvent.setup();
    await user.click(within(visualMode).getByRole("button", { name: /widgets/i }));
    expect(screen.getByRole("complementary", { name: "Edit selection" })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Close inspector" }));
    expect(screen.queryByRole("complementary", { name: /Edit selection|Changes/ })).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Preview" }));
    expect(screen.getByRole("complementary", { name: "Changes" })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Close preview" }));
    expect(screen.queryByRole("complementary", { name: /Edit selection|Changes/ })).not.toBeInTheDocument();
  });

  it("asks before leaving the page with an unapplied draft", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(populatedSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });
    replaceModelEditorValue(
      getVisibleSchemaDraftText({
        ...populatedSchema,
        tables: [
          ...populatedSchema.tables,
          { name: "gadgets", columns: [{ name: "id", type: "integer", primaryKey: true }], indexes: [] },
        ],
      }),
    );

    const user = userEvent.setup();
    await act(async () => {
      await router.navigate("/data");
    });
    const dialog = await screen.findByRole("dialog", { name: "Leave without applying?" });
    await user.click(within(dialog).getByRole("button", { name: "Keep editing" }));
    expect(router.state.location.pathname).toBe("/model");
    expect(screen.getByRole("textbox", { name: /schema json editor/i })).toBeInTheDocument();

    await act(async () => {
      await router.navigate("/data");
    });
    await user.click(
      within(await screen.findByRole("dialog", { name: "Leave without applying?" })).getByRole(
        "button",
        { name: "Discard changes" },
      ),
    );
    await waitFor(() => expect(router.state.location.pathname).toBe("/data"));
  });

  it("switches between JSON and Visual modes against the same draft", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(populatedSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });
    expect(screen.getByRole("button", { name: "JSON" })).toHaveAttribute(
      "aria-pressed",
      "true",
    );
    expect(screen.getByRole("button", { name: "Visual" })).toHaveAttribute(
      "aria-pressed",
      "false",
    );

    const editedSchema = {
      ...populatedSchema,
      tables: [
        ...populatedSchema.tables,
        {
          name: "gadgets",
          layout: { x: 120, y: 80, color: "#2563eb" },
          columns: [{ name: "id", type: "integer", primaryKey: true }],
          indexes: [],
        },
      ],
    };
    replaceModelEditorValue(getVisibleSchemaDraftText(editedSchema));

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));

    const visualMode = await screen.findByTestId("schema-visual-mode");
    expect(visualMode).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Visual" })).toHaveAttribute(
      "aria-pressed",
      "true",
    );
    expect(
      screen.queryByRole("textbox", { name: /schema json editor/i }),
    ).not.toBeInTheDocument();
    expect(within(visualMode).getAllByText("gadgets").length).toBeGreaterThan(
      0,
    );
    expect(
      within(visualMode).getByTestId("schema-erd-canvas"),
    ).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Preview" })).toBeEnabled();
    expect(screen.getByRole("button", { name: "Export" })).toBeEnabled();

    await user.click(screen.getByRole("button", { name: "JSON" }));

    await screen.findByRole("textbox", { name: /schema json editor/i });
    expect(getModelEditorValue()).toBe(getVisibleSchemaDraftText(editedSchema));
  });

  it("blocks Visual mode on invalid JSON without discarding the draft", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(populatedSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });
    const invalidDraft = `${getVisibleSchemaDraftText(populatedSchema)}\n{`;
    replaceModelEditorValue(invalidDraft);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));

    expect(
      await screen.findByText("Fix JSON syntax to use Visual mode"),
    ).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Preview" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Export" })).toBeDisabled();
    expect(
      screen.queryByRole("textbox", { name: /schema json editor/i }),
    ).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Return to JSON" }));

    await screen.findByRole("textbox", { name: /schema json editor/i });
    expect(getModelEditorValue()).toBe(invalidDraft);
    expect(screen.getByRole("button", { name: "JSON" })).toHaveAttribute(
      "aria-pressed",
      "true",
    );
  });

  it("renders the visual ERD empty state for an empty schema", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(emptySchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));

    const visualMode = await screen.findByTestId("schema-visual-mode");
    expect(
      within(visualMode).getByText("No tables in this draft"),
    ).toBeInTheDocument();
    expect(
      within(visualMode).getByTestId("schema-erd-canvas"),
    ).toBeInTheDocument();
    expect(
      within(visualMode).getByRole("button", { name: /add table/i }),
    ).toBeInTheDocument();
    expect(
      within(visualMode).getByRole("button", { name: "Zoom in" }),
    ).toBeDisabled();
  });

  it("renders selectable ERD cards and navigation controls for a simple schema", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(populatedSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));

    const visualMode = await screen.findByTestId("schema-visual-mode");
    expect(
      within(visualMode).getByTestId("schema-erd-canvas"),
    ).toBeInTheDocument();
    expect(
      within(visualMode).getByRole("toolbar", { name: "ERD canvas controls" }),
    ).toBeInTheDocument();
    expect(
      within(visualMode).getByLabelText("Canvas zoom 100%"),
    ).toHaveTextContent("100%");

    await user.click(
      within(visualMode).getByRole("button", { name: "Zoom out" }),
    );
    expect(
      within(visualMode).getByLabelText("Canvas zoom 90%"),
    ).toHaveTextContent("90%");

    await user.click(
      within(visualMode).getByRole("button", { name: "Zoom in" }),
    );
    await user.click(
      within(visualMode).getByRole("button", { name: "Fit canvas" }),
    );
    expect(
      within(visualMode).getByLabelText("Canvas zoom 86%"),
    ).toHaveTextContent("86%");

    await user.click(
      within(visualMode).getByRole("button", { name: /widgets/i }),
    );
    expect(
      screen.getByLabelText("Visual schema inspector"),
    ).toBeInTheDocument();
  });

  it("renders 1:N, N:M, and self-referential relation edges with selection", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi
      .fn()
      .mockResolvedValue(jsonResponse(visualRelationSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));

    const visualMode = await screen.findByTestId("schema-visual-mode");
    expect(
      within(visualMode).getByRole("button", {
        name: /account_users 1:N relation from accounts to users/i,
      }),
    ).toBeInTheDocument();
    expect(
      within(visualMode).getByRole("button", {
        name: /user_groups N:M relation from users to groups/i,
      }),
    ).toBeInTheDocument();
    expect(
      within(visualMode).getByRole("button", {
        name: /user_manager 1:N relation from users to users/i,
      }),
    ).toHaveTextContent("self");

    await user.click(
      within(visualMode).getByRole("button", {
        name: /user_groups N:M relation from users to groups/i,
      }),
    );

    await user.click(
      within(visualMode).getByRole("button", { name: /manager_id/i }),
    );
  });

  it("edits an existing relation from Visual mode and syncs the JSON draft", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(visualRelationSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));

    const visualMode = await screen.findByTestId("schema-visual-mode");
    await user.click(
      within(visualMode).getByRole("button", {
        name: /account_users 1:N relation from accounts to users/i,
      }),
    );

    const inspector = screen.getByLabelText("Visual schema inspector");
    expect(within(inspector).getByLabelText("From")).toHaveValue("accounts");
    expect(within(inspector).getByLabelText("To")).toHaveValue("users");
    expect(within(inspector).getByLabelText("Reference column")).toHaveValue(
      "account_id",
    );
    expect(
      within(inspector).getByRole("button", { name: "Switch direction" }),
    ).toBeInTheDocument();

    await user.selectOptions(within(inspector).getByLabelText("From"), "users");
    fireEvent.change(within(inspector).getByLabelText("Name on parent records"), {
      target: { value: "accounts" },
    });
    fireEvent.change(within(inspector).getByLabelText("Name on child records"), {
      target: { value: "user" },
    });

    await user.click(screen.getByRole("button", { name: "JSON" }));
    await screen.findByRole("textbox", { name: /schema json editor/i });

    const draft = JSON.parse(getModelEditorValue()) as SchemaModelDocument;
    expect(draft.relations).toContainEqual(
      expect.objectContaining({
        name: "account_users",
        type: "1:N",
        from: "users",
        to: "users",
        column: "parent_user_id",
        fromAccessor: "accounts",
        toAccessor: "user",
      }),
    );
  });

  it("converts an existing 1:N relation to N:M from the relation inspector", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(visualRelationSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));

    const visualMode = await screen.findByTestId("schema-visual-mode");
    await user.click(
      within(visualMode).getByRole("button", {
        name: /account_users 1:N relation from accounts to users/i,
      }),
    );

    const inspector = screen.getByLabelText("Visual schema inspector");
    await user.selectOptions(
      within(inspector).getByLabelText("Relation type"),
      "N:M",
    );

    expect(within(inspector).getByLabelText("Junction table")).toHaveValue(
      "_jt_accounts_users",
    );
    expect(within(inspector).getByLabelText("Source column")).toHaveValue(
      "account_id",
    );
    expect(within(inspector).getByLabelText("Target column")).toHaveValue(
      "user_id",
    );

    await user.click(screen.getByRole("button", { name: "JSON" }));
    await screen.findByRole("textbox", { name: /schema json editor/i });

    const draft = JSON.parse(getModelEditorValue()) as SchemaModelDocument;
    expect(draft.tables).toContainEqual(
      expect.objectContaining({
        name: "_jt_accounts_users",
      }),
    );
    expect(draft.tables.find((table) => table.name === "users")?.columns).not.toContainEqual(
      expect.objectContaining({ name: "account_id" }),
    );
    expect(draft.relations).toContainEqual(
      expect.objectContaining({
        name: "account_users",
        type: "N:M",
        junctionTable: "_jt_accounts_users",
        fromColumn: "account_id",
        toColumn: "user_id",
      }),
    );
  });

  it("switches a new relation direction in Visual mode without edit-pane warnings", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi
      .fn()
      .mockResolvedValue(jsonResponse(visualRelationCreationSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));

    const visualMode = await screen.findByTestId("schema-visual-mode");
    await user.click(
      within(visualMode).getByRole("button", { name: /customers/i }),
    );

    expect(
      screen.getByRole("heading", { name: "Edit selection" }),
    ).toBeInTheDocument();
    let inspector = screen.getByLabelText("Visual schema inspector");
    await user.click(
      within(inspector).getByRole("button", { name: /add relation/i }),
    );
    await user.click(
      within(inspector).getByRole("button", { name: "Create relation" }),
    );

    inspector = screen.getByLabelText("Visual schema inspector");
    await user.click(
      within(inspector).getByRole("button", { name: "Switch direction" }),
    );

    expect(
      within(inspector).queryByText(/destructive|warning|confirm/i),
    ).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "JSON" }));
    await screen.findByRole("textbox", { name: /schema json editor/i });

    const draft = JSON.parse(getModelEditorValue()) as SchemaModelDocument;
    expect(draft.relations).toContainEqual(
      expect.objectContaining({
        name: "customer_orders",
        type: "1:N",
        from: "orders",
        to: "customers",
        column: "order_id",
      }),
    );
  });

  it("creates a 1:N relation from Visual mode and syncs relation JSON", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi
      .fn()
      .mockResolvedValue(jsonResponse(visualRelationCreationSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));

    let visualMode = await screen.findByTestId("schema-visual-mode");
    await user.click(
      within(visualMode).getByRole("button", { name: /customers/i }),
    );

    const inspector = screen.getByLabelText("Visual schema inspector");
    await user.click(
      within(inspector).getByRole("button", { name: /add relation/i }),
    );

    expect(within(inspector).getByLabelText("Child table")).toHaveValue(
      "orders",
    );
    expect(within(inspector).getByLabelText("Reference column")).toHaveValue(
      "__new__",
    );
    expect(within(inspector).getByLabelText("Relation name")).toHaveValue(
      "customer_orders",
    );
    expect(within(inspector).getByLabelText("Column name")).toHaveValue(
      "customer_id",
    );
    expect(within(inspector).getByLabelText("Name on parent records")).toHaveValue(
      "orders",
    );
    expect(within(inspector).getByLabelText("Name on child records")).toHaveValue(
      "customer",
    );

    await user.click(
      within(inspector).getByRole("button", { name: "Create relation" }),
    );

    visualMode = await screen.findByTestId("schema-visual-mode");
    expect(
      within(visualMode).getByRole("button", {
        name: /customer_orders 1:N relation from customers to orders/i,
      }),
    ).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "JSON" }));
    await screen.findByRole("textbox", { name: /schema json editor/i });

    const draft = JSON.parse(getModelEditorValue()) as SchemaModelDocument;
    const orders = draft.tables.find((table) => table.name === "orders");
    expect(orders?.columns).toContainEqual(
      expect.objectContaining({
        name: "customer_id",
        type: "reference",
        references: "customers",
        relationName: "customer_orders",
      }),
    );
    expect(orders?.indexes).toContainEqual(
      expect.objectContaining({
        name: "idx_orders_customer_id",
        columns: ["customer_id"],
        unique: false,
      }),
    );
    expect(draft.relations).toContainEqual({
      name: "customer_orders",
      type: "1:N",
      from: "customers",
      to: "orders",
      column: "customer_id",
      fromAccessor: "orders",
      toAccessor: "customer",
    });
  });

  it("creates an N:M relation from Visual mode with a visible junction table", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi
      .fn()
      .mockResolvedValue(jsonResponse(visualRelationCreationSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));

    let visualMode = await screen.findByTestId("schema-visual-mode");
    await user.click(
      within(visualMode).getByRole("button", { name: /customers/i }),
    );

    const inspector = screen.getByLabelText("Visual schema inspector");
    await user.click(
      within(inspector).getByRole("button", { name: /add relation/i }),
    );
    await user.selectOptions(
      within(inspector).getByLabelText("Relation type"),
      "N:M",
    );

    expect(within(inspector).getByLabelText("Target table")).toHaveValue(
      "orders",
    );
    expect(within(inspector).getByLabelText("Junction table")).toHaveValue(
      "_jt_customers_orders",
    );
    expect(within(inspector).getByLabelText("Source column")).toHaveValue(
      "customer_id",
    );
    expect(within(inspector).getByLabelText("Target column")).toHaveValue(
      "order_id",
    );

    await user.click(
      within(inspector).getByRole("button", { name: "Create relation" }),
    );

    visualMode = await screen.findByTestId("schema-visual-mode");
    expect(
      within(visualMode).getAllByText("_jt_customers_orders").length,
    ).toBeGreaterThan(0);
    expect(
      within(visualMode).getByRole("button", {
        name: /customer_orders N:M relation from customers to _jt_customers_orders/i,
      }),
    ).toBeInTheDocument();
    expect(
      within(visualMode).getByRole("button", {
        name: /customer_orders N:M relation from _jt_customers_orders to orders/i,
      }),
    ).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "JSON" }));
    await screen.findByRole("textbox", { name: /schema json editor/i });

    const draft = JSON.parse(getModelEditorValue()) as SchemaModelDocument;
    expect(draft.tables).toContainEqual(
      expect.objectContaining({
        name: "_jt_customers_orders",
        columns: [
          expect.objectContaining({
            name: "customer_id",
            type: "reference",
            references: "customers",
            primaryKey: true,
          }),
          expect.objectContaining({
            name: "order_id",
            type: "reference",
            references: "orders",
            primaryKey: true,
          }),
          expect.objectContaining({ name: "created_at", nullable: true }),
        ],
      }),
    );
    expect(draft.relations).toContainEqual({
      name: "customer_orders",
      type: "N:M",
      from: "customers",
      to: "orders",
      fromColumn: "customer_id",
      toColumn: "order_id",
      junctionTable: "_jt_customers_orders",
      fromAccessor: "orders",
      toAccessor: "customers",
    });
  });

  it("keeps N:M inspector junction table and column edits synchronized with JSON", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi
      .fn()
      .mockResolvedValue(jsonResponse(visualRelationCreationSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));

    const visualMode = await screen.findByTestId("schema-visual-mode");
    await user.click(
      within(visualMode).getByRole("button", { name: /customers/i }),
    );

    const inspector = screen.getByLabelText("Visual schema inspector");
    await user.click(
      within(inspector).getByRole("button", { name: /add relation/i }),
    );
    await user.selectOptions(
      within(inspector).getByLabelText("Relation type"),
      "N:M",
    );
    await user.click(
      within(inspector).getByRole("button", { name: "Create relation" }),
    );

    fireEvent.change(within(inspector).getByLabelText("Junction table"), {
      target: { value: "customer_order_links" },
    });
    fireEvent.change(within(inspector).getByLabelText("Source column"), {
      target: { value: "source_customer_id" },
    });
    fireEvent.change(within(inspector).getByLabelText("Target column"), {
      target: { value: "target_order_id" },
    });

    await user.click(screen.getByRole("button", { name: "JSON" }));
    await screen.findByRole("textbox", { name: /schema json editor/i });

    const draft = JSON.parse(getModelEditorValue()) as SchemaModelDocument;
    expect(draft.tables.some((table) => table.name === "_jt_customers_orders")).toBe(
      false,
    );
    expect(draft.tables).toContainEqual(
      expect.objectContaining({
        name: "customer_order_links",
        columns: [
          expect.objectContaining({
            name: "source_customer_id",
            references: "customers",
            primaryKey: true,
          }),
          expect.objectContaining({
            name: "target_order_id",
            references: "orders",
            primaryKey: true,
          }),
          expect.objectContaining({ name: "created_at" }),
        ],
      }),
    );
    expect(draft.relations).toContainEqual(
      expect.objectContaining({
        name: "customer_orders",
        type: "N:M",
        junctionTable: "customer_order_links",
        fromColumn: "source_customer_id",
        toColumn: "target_order_id",
      }),
    );
  });

  it("switches N:M direction from the inspector without invalid junction references", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi
      .fn()
      .mockResolvedValue(jsonResponse(visualRelationCreationSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));

    const visualMode = await screen.findByTestId("schema-visual-mode");
    await user.click(
      within(visualMode).getByRole("button", { name: /customers/i }),
    );

    const inspector = screen.getByLabelText("Visual schema inspector");
    await user.click(
      within(inspector).getByRole("button", { name: /add relation/i }),
    );
    await user.selectOptions(
      within(inspector).getByLabelText("Relation type"),
      "N:M",
    );
    await user.click(
      within(inspector).getByRole("button", { name: "Create relation" }),
    );
    await user.click(
      within(inspector).getByRole("button", { name: "Switch direction" }),
    );

    await user.click(screen.getByRole("button", { name: "JSON" }));
    await screen.findByRole("textbox", { name: /schema json editor/i });

    const draft = JSON.parse(getModelEditorValue()) as SchemaModelDocument;
    expect(draft.relations).toContainEqual(
      expect.objectContaining({
        name: "customer_orders",
        type: "N:M",
        from: "orders",
        to: "customers",
        fromColumn: "order_id",
        toColumn: "customer_id",
        fromAccessor: "customers",
        toAccessor: "orders",
      }),
    );
    expect(draft.tables).toContainEqual(
      expect.objectContaining({
        name: "_jt_customers_orders",
        columns: [
          expect.objectContaining({
            name: "customer_id",
            references: "customers",
          }),
          expect.objectContaining({
            name: "order_id",
            references: "orders",
          }),
          expect.objectContaining({ name: "created_at" }),
        ],
      }),
    );
  });

  it("converts an existing visible N:M relation back to 1:N from the relation inspector", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi
      .fn()
      .mockResolvedValue(jsonResponse(visualRelationCreationSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));

    let visualMode = await screen.findByTestId("schema-visual-mode");
    await user.click(
      within(visualMode).getByRole("button", { name: /customers/i }),
    );

    expect(
      screen.getByRole("heading", { name: "Edit selection" }),
    ).toBeInTheDocument();
    let inspector = screen.getByLabelText("Visual schema inspector");
    await user.click(
      within(inspector).getByRole("button", { name: /add relation/i }),
    );
    await user.selectOptions(
      within(inspector).getByLabelText("Relation type"),
      "N:M",
    );
    await user.click(
      within(inspector).getByRole("button", { name: "Create relation" }),
    );

    visualMode = await screen.findByTestId("schema-visual-mode");
    await user.click(
      within(visualMode).getByRole("button", {
        name: /customer_orders N:M relation from customers to _jt_customers_orders/i,
      }),
    );

    inspector = screen.getByLabelText("Visual schema inspector");
    await user.selectOptions(
      within(inspector).getByLabelText("Relation type"),
      "1:N",
    );

    await user.click(screen.getByRole("button", { name: "JSON" }));
    await screen.findByRole("textbox", { name: /schema json editor/i });

    const draft = JSON.parse(getModelEditorValue()) as SchemaModelDocument;
    expect(draft.tables.some((table) => table.name === "_jt_customers_orders")).toBe(
      false,
    );
    expect(draft.relations).toContainEqual(
      expect.objectContaining({
        name: "customer_orders",
        type: "1:N",
        column: "customer_id",
      }),
    );
  });

  it("deletes an existing 1:N relation from the relation inspector without an edit-pane confirmation", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(visualRelationSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));

    const visualMode = await screen.findByTestId("schema-visual-mode");
    await user.click(
      within(visualMode).getByRole("button", {
        name: /account_users 1:N relation from accounts to users/i,
      }),
    );

    const inspector = screen.getByLabelText("Visual schema inspector");
    await user.click(
      within(inspector).getByRole("button", { name: "Delete relation" }),
    );

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "JSON" }));
    await screen.findByRole("textbox", { name: /schema json editor/i });

    const draft = JSON.parse(getModelEditorValue()) as SchemaModelDocument;
    expect(draft.relations).not.toContainEqual(
      expect.objectContaining({ name: "account_users" }),
    );
    expect(draft.tables.find((table) => table.name === "users")?.columns).not.toContainEqual(
      expect.objectContaining({ name: "account_id" }),
    );
  });

  it("deletes an N:M relation from the relation inspector and removes the visible junction table", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi
      .fn()
      .mockResolvedValue(jsonResponse(visualRelationCreationSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));

    let visualMode = await screen.findByTestId("schema-visual-mode");
    await user.click(
      within(visualMode).getByRole("button", { name: /customers/i }),
    );

    expect(
      screen.getByRole("heading", { name: "Edit selection" }),
    ).toBeInTheDocument();
    let inspector = screen.getByLabelText("Visual schema inspector");
    await user.click(
      within(inspector).getByRole("button", { name: /add relation/i }),
    );
    await user.selectOptions(
      within(inspector).getByLabelText("Relation type"),
      "N:M",
    );
    await user.click(
      within(inspector).getByRole("button", { name: "Create relation" }),
    );

    visualMode = await screen.findByTestId("schema-visual-mode");
    await user.click(
      within(visualMode).getByRole("button", {
        name: /customer_orders N:M relation from customers to _jt_customers_orders/i,
      }),
    );

    inspector = screen.getByLabelText("Visual schema inspector");
    await user.click(
      within(inspector).getByRole("button", { name: "Delete relation" }),
    );

    await user.click(screen.getByRole("button", { name: "JSON" }));
    await screen.findByRole("textbox", { name: /schema json editor/i });

    const draft = JSON.parse(getModelEditorValue()) as SchemaModelDocument;
    expect(draft.relations).toEqual([]);
    expect(draft.tables.some((table) => table.name === "_jt_customers_orders")).toBe(
      false,
    );
  });

  it("selects a relation by clicking its line in the canvas", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(visualRelationSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));

    const visualMode = await screen.findByTestId("schema-visual-mode");
    const line = visualMode.querySelector(
      '[data-testid="schema-relation-line-account_users"]',
    );
    expect(line).not.toBeNull();
    expect(line?.getAttribute("d")).toMatch(/^M [\d.-]+ [\d.-]+(?: L [\d.-]+ [\d.-]+)+$/);

    fireEvent.click(line!);

    const inspector = screen.getByLabelText("Visual schema inspector");
    expect(within(inspector).getByLabelText("Relation name")).toHaveValue(
      "account_users",
    );
  });

  it("renames a relation from the relation inspector and syncs the JSON draft", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(visualRelationSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));

    const visualMode = await screen.findByTestId("schema-visual-mode");
    await user.click(
      within(visualMode).getByRole("button", {
        name: /account_users 1:N relation from accounts to users/i,
      }),
    );

    const inspector = screen.getByLabelText("Visual schema inspector");
    const nameField = within(inspector).getByLabelText(
      "Relation name",
    ) as HTMLInputElement;

    fireEvent.change(nameField, { target: { value: "accounts_users" } });
    fireEvent.blur(nameField);

    await screen.findByRole("button", {
      name: /accounts_users 1:N relation from accounts to users/i,
    });

    await user.click(screen.getByRole("button", { name: "JSON" }));
    await screen.findByRole("textbox", { name: /schema json editor/i });

    const draft = JSON.parse(getModelEditorValue()) as SchemaModelDocument;
    expect(draft.relations.map((relation) => relation.name)).toContain(
      "accounts_users",
    );
    expect(draft.relations.map((relation) => relation.name)).not.toContain(
      "account_users",
    );
  });

  it("blocks renaming a relation when the new name collides with another relation", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(visualRelationSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));

    const visualMode = await screen.findByTestId("schema-visual-mode");
    await user.click(
      within(visualMode).getByRole("button", {
        name: /account_users 1:N relation from accounts to users/i,
      }),
    );

    const inspector = screen.getByLabelText("Visual schema inspector");
    const nameField = within(inspector).getByLabelText(
      "Relation name",
    ) as HTMLInputElement;

    fireEvent.change(nameField, { target: { value: "user_groups" } });
    fireEvent.blur(nameField);

    const errors = await screen.findByTestId("visual-relation-inspector-errors");
    expect(
      within(errors).getByText('Relation name "user_groups" is already in use.'),
    ).toBeInTheDocument();

    expect(nameField).toHaveValue("account_users");
  });

  it("shows unsupported relation properties as read-only in the inspector", async () => {
    seedAuthenticated("admin");
    const schemaWithCascade = {
      ...visualRelationSchema,
      relations: visualRelationSchema.relations.map((relation) =>
        relation.name === "account_users"
          ? { ...relation, onDelete: "cascade", nullable: false }
          : relation,
      ),
    };
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(schemaWithCascade));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));

    const visualMode = await screen.findByTestId("schema-visual-mode");
    await user.click(
      within(visualMode).getByRole("button", {
        name: /account_users 1:N relation from accounts to users/i,
      }),
    );

    const inspector = screen.getByLabelText("Visual schema inspector");
    const advanced = within(inspector).getByTestId("visual-relation-advanced");
    expect(advanced).toHaveTextContent("onDelete");
    expect(advanced).toHaveTextContent("cascade");
    expect(advanced).toHaveTextContent("nullable");
    expect(advanced).toHaveTextContent("false");
    expect(
      within(inspector).queryByLabelText(/cascade/i),
    ).not.toBeInTheDocument();
  });

  it("creates a relation by dragging from a selected table side handle onto another table", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi
      .fn()
      .mockResolvedValue(jsonResponse(visualRelationCreationSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));

    let visualMode = await screen.findByTestId("schema-visual-mode");
    await user.click(
      within(visualMode).getByRole("button", { name: /customers/i }),
    );

    const rightHandle = within(visualMode).getByRole("button", {
      name: /start relation from right side/i,
    });
    expect(
      within(visualMode).getByRole("button", {
        name: /start relation from left side/i,
      }),
    ).toBeInTheDocument();

    fireEvent.pointerDown(rightHandle, {
      pointerId: 1,
      clientX: 368,
      clientY: 176,
    });
    act(() => {
      window.dispatchEvent(
        new MouseEvent("pointermove", {
          bubbles: true,
          clientX: 500,
          clientY: 140,
        }),
      );
    });
    expect(
      visualMode.querySelector(
        ".schema-workbench__erd-relation-path--draft",
      ),
    ).toBeInTheDocument();
    act(() => {
      window.dispatchEvent(
        new MouseEvent("pointerup", {
          bubbles: true,
          clientX: 500,
          clientY: 140,
        }),
      );
    });

    visualMode = await screen.findByTestId("schema-visual-mode");
    expect(
      within(visualMode).getByRole("button", {
        name: /customer_orders 1:N relation from customers to orders/i,
      }),
    ).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "JSON" }));
    await screen.findByRole("textbox", { name: /schema json editor/i });

    const draft = JSON.parse(getModelEditorValue()) as SchemaModelDocument;
    expect(draft.relations).toContainEqual(
      expect.objectContaining({
        name: "customer_orders",
        type: "1:N",
        from: "customers",
        to: "orders",
        column: "customer_id",
      }),
    );
  });

  it("creates a multi-table schema visually, previews it, applies it, and refreshes the draft", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse(emptySchema))
      .mockResolvedValueOnce(jsonResponse(previewVisualCreateResponse))
      .mockResolvedValueOnce(
        jsonResponse({
          applied: true,
          version: 1,
          changes: previewVisualCreateResponse.changes,
          warnings: [],
        }),
      )
      .mockResolvedValueOnce(jsonResponse(visualCreatedAppliedSchema))
      .mockResolvedValueOnce(jsonResponse(visualCreatedAppliedSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));

    let visualMode = await screen.findByTestId("schema-visual-mode");
    const getTableHeader = (name: string) => {
      const button = within(visualMode)
        .getAllByRole("button", { name: new RegExp(name, "i") })
        .find((candidate) =>
          candidate.classList.contains("schema-workbench__erd-table-header"),
        );
      expect(button).toBeDefined();
      return button as HTMLElement;
    };

    await user.click(
      within(visualMode).getByRole("button", { name: /add table/i }),
    );
    expect(
      screen.getByRole("heading", { name: "Edit selection" }),
    ).toBeInTheDocument();
    let inspector = screen.getByLabelText("Visual schema inspector");
    fireEvent.change(within(inspector).getByLabelText("Table name"), {
      target: { value: "customers" },
    });
    await user.click(
      within(inspector).getByRole("button", { name: /add column/i }),
    );
    fireEvent.change(within(inspector).getByLabelText("Column name"), {
      target: { value: "name" },
    });
    fireEvent.change(within(inspector).getByLabelText(/^Previous name/), {
      target: { value: "" },
    });
    await user.click(
      within(inspector).getByRole("button", { name: "Table columns" }),
    );

    await user.click(
      within(visualMode).getByRole("button", { name: /add table/i }),
    );
    inspector = screen.getByLabelText("Visual schema inspector");
    fireEvent.change(within(inspector).getByLabelText("Table name"), {
      target: { value: "orders" },
    });
    await user.click(
      within(inspector).getByRole("button", { name: /add column/i }),
    );
    fireEvent.change(within(inspector).getByLabelText("Column name"), {
      target: { value: "total" },
    });
    await user.selectOptions(
      within(inspector).getByLabelText("Column type"),
      "integer",
    );
    fireEvent.change(within(inspector).getByLabelText(/^Previous name/), {
      target: { value: "" },
    });

    await user.click(getTableHeader("customers"));
    inspector = screen.getByLabelText("Visual schema inspector");
    await user.click(
      within(inspector).getByRole("button", { name: /add relation/i }),
    );
    await user.click(
      within(inspector).getByRole("button", { name: /create relation/i }),
    );

    visualMode = await screen.findByTestId("schema-visual-mode");
    expect(
      within(visualMode).getByRole("button", {
        name: /customer_orders 1:N relation from customers to orders/i,
      }),
    ).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Preview" }));
    expect(await screen.findByText("Add table customers")).toBeInTheDocument();
    expect(screen.getByText("Add table orders")).toBeInTheDocument();
    expect(screen.getByText("Add relation customer_orders")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Apply" }));
    const dialog = await screen.findByRole("dialog", {
      name: "Review and apply schema changes",
    });
    const review = within(dialog).getByTestId("schema-apply-review-scroll");
    setScrollMetrics(review, {
      scrollHeight: 480,
      clientHeight: 480,
      scrollTop: 0,
    });
    fireEvent.scroll(review);

    const confirmButton = within(dialog).getByRole("button", { name: "Apply" });
    await waitFor(() => expect(confirmButton).toBeEnabled());
    await user.click(confirmButton);

    expect(
      await screen.findByText("Schema changes applied"),
    ).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "JSON" }));
    await screen.findByRole("textbox", { name: /schema json editor/i });
    expect(getModelEditorValue()).toBe(
      getVisibleSchemaDraftText(visualCreatedAppliedSchema),
    );
    expect(useSchemaStore.getState().snapshot).toEqual(visualCreatedAppliedSchema);
    expect(fetchMock).toHaveBeenNthCalledWith(
      2,
      "/api/v1/admin/schema/preview",
      expect.objectContaining({
        method: "POST",
      }),
    );
    expect(fetchMock).toHaveBeenNthCalledWith(
      3,
      "/api/v1/admin/schema",
      expect.objectContaining({
        method: "PUT",
      }),
    );
  });

  it("edits table layout color from the Visual inspector and persists it in the draft JSON", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi
      .fn()
      .mockResolvedValue(jsonResponse(visualRelationCreationSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));

    const visualMode = await screen.findByTestId("schema-visual-mode");
    await user.click(
      within(visualMode).getByRole("button", { name: /customers/i }),
    );

    const inspector = screen.getByLabelText("Visual schema inspector");
    const colorInput = within(inspector).getByLabelText(
      "Table color",
    ) as HTMLInputElement;
    fireEvent.input(colorInput, { target: { value: "#ff8800" } });

    await user.click(screen.getByRole("button", { name: "JSON" }));
    await screen.findByRole("textbox", { name: /schema json editor/i });

    const draft = JSON.parse(getModelEditorValue()) as SchemaModelDocument;
    const customers = draft.tables.find((table) => table.name === "customers");
    expect(customers?.layout).toEqual(
      expect.objectContaining({ color: "#ff8800" }),
    );
  });

  it("clears table layout color from the Visual inspector and keeps x/y", async () => {
    seedAuthenticated("admin");
    const seededSchema = {
      ...visualRelationCreationSchema,
      tables: visualRelationCreationSchema.tables.map((table) =>
        table.name === "customers"
          ? {
              ...table,
              layout: { ...table.layout, color: "#2563eb" },
            }
          : table,
      ),
    };
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(seededSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));

    const visualMode = await screen.findByTestId("schema-visual-mode");
    await user.click(
      within(visualMode).getByRole("button", { name: /customers/i }),
    );

    const inspector = screen.getByLabelText("Visual schema inspector");
    expect(within(inspector).getByText("#2563eb")).toBeInTheDocument();

    await user.click(
      within(inspector).getByRole("button", { name: /clear table color/i }),
    );

    expect(within(inspector).queryByText("#2563eb")).not.toBeInTheDocument();
    expect(within(inspector).getByText(/no color/i)).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "JSON" }));
    await screen.findByRole("textbox", { name: /schema json editor/i });

    const draft = JSON.parse(getModelEditorValue()) as SchemaModelDocument;
    const customers = draft.tables.find((table) => table.name === "customers");
    expect((customers?.layout as { color?: unknown })?.color).toBeNull();
    expect((customers?.layout as { x: number }).x).toBe(80);
  });

  it("shows a layout-only preview state and enables Apply when only layout metadata changed", async () => {
    seedAuthenticated("admin");
    const layoutOnlyPreviewResponse = {
      isValid: true,
      hasChanges: false,
      metadataOnly: true,
      isDestructive: false,
      requiresConfirmation: false,
      currentVersion: 5,
      errors: [],
      warnings: [],
      changes: [],
      normalizedModel: visualRelationCreationSchema,
      plan: {
        sqlUp: [],
        sqlDown: [],
        isDestructive: false,
        requiresConfirmation: false,
      },
    };
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse(visualRelationCreationSchema))
      .mockResolvedValue(jsonResponse(layoutOnlyPreviewResponse));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));

    const visualMode = await screen.findByTestId("schema-visual-mode");
    await user.click(
      within(visualMode).getByRole("button", { name: /customers/i }),
    );

    const inspector = screen.getByLabelText("Visual schema inspector");
    const colorInput = within(inspector).getByLabelText(
      "Table color",
    ) as HTMLInputElement;
    fireEvent.input(colorInput, { target: { value: "#00bcd4" } });

    await user.click(screen.getByRole("button", { name: "Preview" }));

    await screen.findByTestId("schema-preview-layout-only");
    expect(
      screen.getByRole("button", { name: "Apply" }),
    ).not.toBeDisabled();
  });

  it("edits table and column metadata in Visual mode and syncs the JSON draft", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(populatedSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));

    const visualMode = await screen.findByTestId("schema-visual-mode");
    // The inspector only appears once something is selected.
    expect(
      screen.queryByRole("heading", { name: "Edit selection" }),
    ).not.toBeInTheDocument();
    expect(
      within(visualMode).queryByLabelText("Visual schema inspector"),
    ).not.toBeInTheDocument();
    await user.click(
      within(visualMode).getByRole("button", { name: /widgets/i }),
    );

    expect(
      screen.getByRole("heading", { name: "Edit selection" }),
    ).toBeInTheDocument();
    let inspector = screen.getByLabelText("Visual schema inspector");
    fireEvent.change(within(inspector).getByLabelText("Table name"), {
      target: { value: "inventory_items" },
    });
    fireEvent.change(within(inspector).getByLabelText("Table display name"), {
      target: { value: "Inventory Items" },
    });

    await user.click(
      within(inspector).getByRole("button", { name: /add column/i }),
    );
    fireEvent.change(within(inspector).getByLabelText("Column name"), {
      target: { value: "status" },
    });
    expect(
      within(inspector).queryByLabelText("Table name"),
    ).not.toBeInTheDocument();
    expect(
      within(inspector).queryByLabelText("Table display name"),
    ).not.toBeInTheDocument();
    expect(
      within(inspector).queryByRole("button", { name: /add column/i }),
    ).not.toBeInTheDocument();
    fireEvent.change(within(inspector).getByLabelText("Column display name"), {
      target: { value: "Status" },
    });
    await user.selectOptions(
      within(inspector).getByLabelText("Column type"),
      "enum",
    );

    expect(
      within(visualMode).getByText(/must define at least one value/),
    ).toBeInTheDocument();

    fireEvent.change(within(inspector).getByLabelText(/enum values/i), {
      target: { value: "draft, active" },
    });
    fireEvent.change(within(inspector).getByLabelText("Default value"), {
      target: { value: "active" },
    });
    fireEvent.change(within(inspector).getByLabelText(/^Previous name/), {
      target: { value: "legacy_status" },
    });
    await user.click(within(inspector).getByLabelText("Required"));
    await user.click(within(inspector).getByLabelText("Unique"));

    await user.click(
      within(inspector).getByRole("button", { name: "Table columns" }),
    );
    inspector = screen.getByLabelText("Visual schema inspector");
    await user.click(
      within(inspector).getByRole("button", { name: /add column/i }),
    );
    fireEvent.change(within(inspector).getByLabelText("Column name"), {
      target: { value: "owner_id" },
    });
    await user.selectOptions(
      within(inspector).getByLabelText("Column type"),
      "reference",
    );
    await user.selectOptions(
      within(inspector).getByLabelText("Reference target"),
      "inventory_items",
    );

    await user.click(screen.getByRole("button", { name: "JSON" }));
    await screen.findByRole("textbox", { name: /schema json editor/i });

    const draft = JSON.parse(getModelEditorValue()) as SchemaModelDocument;
    const table = draft.tables.find(
      (candidate) => candidate.name === "inventory_items",
    );
    expect(table?.displayName).toBe("Inventory Items");
    expect(table?.renamedFrom).toBe("widgets");

    const statusColumn = table?.columns.find(
      (column) => column.name === "status",
    );
    expect(statusColumn).toEqual(
      expect.objectContaining({
        displayName: "Status",
        type: "enum",
        values: ["draft", "active"],
        default: "active",
        renamedFrom: "legacy_status",
        nullable: false,
        unique: true,
      }),
    );
    expect(table?.columns.find((column) => column.name === "owner_id")).toEqual(
      expect.objectContaining({
        type: "reference",
        references: "inventory_items",
      }),
    );
  });

  it("adds, duplicates, renames, and deletes tables from Visual mode", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi
      .fn()
      .mockResolvedValue(jsonResponse(visualRelationSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));

    const visualMode = await screen.findByTestId("schema-visual-mode");
    const getTableButton = (name: string) => {
      const matcher = new RegExp(name, "i");
      const button = within(visualMode)
        .getAllByRole("button", { name: matcher })
        .find((candidate) =>
          candidate.classList.contains("schema-workbench__erd-table-header"),
        );
      expect(button).toBeDefined();
      return button as HTMLElement;
    };

    await user.click(
      within(visualMode).getByRole("button", { name: /add table/i }),
    );
    expect(
      within(visualMode).getByRole("button", { name: /new_table/i }),
    ).toBeInTheDocument();

    await user.click(getTableButton("accounts"));
    expect(
      screen.getByRole("heading", { name: "Edit selection" }),
    ).toBeInTheDocument();
    let inspector = screen.getByLabelText("Visual schema inspector");
    fireEvent.change(within(inspector).getByLabelText("Table name"), {
      target: { value: "organizations" },
    });
    expect(within(inspector).getByLabelText(/^Previous name/)).toHaveValue(
      "accounts",
    );

    await user.click(getTableButton("users"));
    inspector = screen.getByLabelText("Visual schema inspector");
    await user.click(
      within(inspector).getByRole("button", { name: /duplicate table/i }),
    );
    expect(getTableButton("users_copy")).toBeInTheDocument();
    expect(
      within(visualMode).getByRole("button", { name: /user_manager_copy/i }),
    ).toBeInTheDocument();

    await user.click(getTableButton("organizations"));
    inspector = screen.getByLabelText("Visual schema inspector");
    await user.click(
      within(inspector).getByRole("button", { name: /delete table/i }),
    );
    const deleteTableDialog = await screen.findByRole("dialog", {
      name: 'Delete table "organizations"?',
    });
    expect(deleteTableDialog).toHaveTextContent(
      "1 relation will be removed. 2 reference columns will be removed.",
    );
    await user.click(
      within(deleteTableDialog).getByRole("button", { name: "Delete table" }),
    );

    await user.click(screen.getByRole("button", { name: "JSON" }));
    await screen.findByRole("textbox", { name: /schema json editor/i });

    const draft = JSON.parse(getModelEditorValue()) as SchemaModelDocument;
    expect(draft.tables.map((table) => table.name)).toEqual([
      "users",
      "groups",
      "new_table",
      "users_copy",
    ]);
    expect(
      draft.tables
        .find((table) => table.name === "users")
        ?.columns.map((column) => column.name),
    ).toEqual(["id", "manager_id"]);
    expect(
      draft.tables.find((table) => table.name === "new_table")?.columns[0],
    ).toEqual(
      expect.objectContaining({
        name: "id",
        type: "integer",
        primaryKey: true,
        nullable: false,
      }),
    );
    expect(draft.relations.map((relation) => relation.name)).toEqual([
      "user_groups",
      "user_manager",
      "user_manager_copy",
    ]);
  });

  it("adds, reorders, and deletes columns from the Visual inspector", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(populatedSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));

    let visualMode = await screen.findByTestId("schema-visual-mode");
    // The inspector only appears once something is selected.
    expect(
      screen.queryByRole("heading", { name: "Edit selection" }),
    ).not.toBeInTheDocument();
    await user.click(
      within(visualMode).getByRole("button", { name: /widgets/i }),
    );

    expect(
      screen.getByRole("heading", { name: "Edit selection" }),
    ).toBeInTheDocument();
    let inspector = screen.getByLabelText("Visual schema inspector");
    await user.click(
      within(inspector).getByRole("button", { name: /add column/i }),
    );
    fireEvent.change(within(inspector).getByLabelText("Column name"), {
      target: { value: "color" },
    });
    await user.click(
      within(inspector).getByRole("button", { name: "Move column up" }),
    );

    await user.click(
      within(inspector).getByRole("button", { name: "Table columns" }),
    );
    inspector = screen.getByLabelText("Visual schema inspector");
    expect(within(inspector).getByText("Columns")).toBeInTheDocument();
    expect(
      within(inspector).getByRole("button", { name: /color/i }),
    ).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "JSON" }));
    await screen.findByRole("textbox", { name: /schema json editor/i });
    let draft = JSON.parse(getModelEditorValue()) as SchemaModelDocument;
    expect(draft.tables[0].columns.map((column) => column.name)).toEqual([
      "id",
      "color",
      "name",
    ]);

    await user.click(screen.getByRole("button", { name: "Visual" }));
    visualMode = await screen.findByTestId("schema-visual-mode");
    await user.click(
      within(visualMode).getByRole("button", { name: /^color/i }),
    );
    inspector = screen.getByLabelText("Visual schema inspector");

    await user.click(
      within(inspector).getByRole("button", { name: "Delete column" }),
    );
    const deleteColumnDialog = await screen.findByRole("dialog", {
      name: 'Delete column "widgets.color"?',
    });
    await user.click(
      within(deleteColumnDialog).getByRole("button", { name: "Delete column" }),
    );

    await user.click(screen.getByRole("button", { name: "JSON" }));
    await screen.findByRole("textbox", { name: /schema json editor/i });
    draft = JSON.parse(getModelEditorValue()) as SchemaModelDocument;
    expect(draft.tables[0].columns.map((column) => column.name)).toEqual([
      "id",
      "name",
    ]);
  });

  it("refreshes a clean draft without asking for confirmation", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse(populatedSchema))
      .mockResolvedValueOnce(jsonResponse(appliedSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Refresh" }));

    await waitFor(() =>
      expect(getModelEditorValue()).toBe(
        getVisibleSchemaDraftText(appliedSchema),
      ),
    );
    expect(
      screen.queryByRole("dialog", { name: "Discard local draft edits?" }),
    ).not.toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(fetchMock).toHaveBeenNthCalledWith(
      2,
      "/api/v1/admin/schema/model",
      expect.objectContaining({
        method: "GET",
        credentials: "include",
      }),
    );
  });

  it("confirms before refreshing over a dirty draft", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse(populatedSchema))
      .mockResolvedValueOnce(jsonResponse(appliedSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });
    const dirtyDraft = `${getVisibleSchemaDraftText(populatedSchema)}\n{`;
    replaceModelEditorValue(dirtyDraft);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Refresh" }));

    expect(
      await screen.findByRole("dialog", {
        name: "Discard local draft edits?",
      }),
    ).toBeInTheDocument();
    expect(getModelEditorValue()).toBe(dirtyDraft);
    expect(fetchMock).toHaveBeenCalledTimes(1);

    await user.click(screen.getByRole("button", { name: "Keep editing" }));

    await waitFor(() =>
      expect(
        screen.queryByRole("dialog", {
          name: "Discard local draft edits?",
        }),
      ).not.toBeInTheDocument(),
    );
    expect(getModelEditorValue()).toBe(dirtyDraft);
    expect(fetchMock).toHaveBeenCalledTimes(1);

    await user.click(screen.getByRole("button", { name: "Refresh" }));
    await user.click(
      await screen.findByRole("button", {
        name: "Discard edits and refresh",
      }),
    );

    await waitFor(() =>
      expect(getModelEditorValue()).toBe(
        getVisibleSchemaDraftText(appliedSchema),
      ),
    );
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(fetchMock).toHaveBeenNthCalledWith(
      2,
      "/api/v1/admin/schema/model",
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
      screen.getByText("Only administrators can change the schema."),
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
    await screen.findByRole("textbox", { name: /schema json editor/i });
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

    await screen.findByRole("textbox", { name: /schema json editor/i });
    replaceModelEditorValue('{"version":1,"tables":[],"relations":[]}');

    expect(getModelEditorValue()).toBe(
      '{"version":1,"tables":[],"relations":[]}',
    );

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Format JSON" }));

    expect(getModelEditorValue()).toBe(
      '{\n  "version": 1,\n  "tables": [],\n  "relations": []\n}',
    );
  });

  it("automatically previews a valid dirty draft after editing pauses", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse(populatedSchema))
      .mockResolvedValueOnce(jsonResponse(previewGroupedResponse));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    vi.useFakeTimers();
    try {
      replaceModelEditorValue(JSON.stringify(appliedSchema, null, 2));

      expect(fetchMock).toHaveBeenCalledTimes(1);

      act(() => {
        vi.advanceTimersByTime(799);
      });
      expect(fetchMock).toHaveBeenCalledTimes(1);

      await act(async () => {
        vi.advanceTimersByTime(1);
        await Promise.resolve();
      });

      expect(fetchMock).toHaveBeenNthCalledWith(
        2,
        "/api/v1/admin/schema/preview",
        expect.objectContaining({
          method: "POST",
          body: JSON.stringify(appliedSchema),
        }),
      );
    } finally {
      vi.useRealTimers();
    }
  });

  it("does not infer rename hints for same-slot edits without explicit renamedFrom", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse(populatedSchema))
      .mockResolvedValueOnce(
        jsonResponse(previewUnhintedSameSlotRenameResponse),
      );
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });
    const visibleDraftText = JSON.stringify(renamedColumnDraftSchema, null, 2);
    replaceModelEditorValue(visibleDraftText);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Preview" }));

    expect(
      await screen.findByText("Drop column widgets.name"),
    ).toBeInTheDocument();
    expect(fetchMock).toHaveBeenNthCalledWith(
      2,
      "/api/v1/admin/schema/preview",
      expect.objectContaining({
        method: "POST",
        body: JSON.stringify(renamedColumnDraftSchema),
      }),
    );
    expect(fetchMock.mock.calls[1]?.[1]?.body).not.toContain("renamedFrom");
  });

  it("sends explicit rename hints from the visible draft for preview and apply", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse(populatedSchema))
      .mockResolvedValueOnce(jsonResponse(previewRenameResponse))
      .mockResolvedValueOnce(
        jsonResponse({
          applied: true,
          version: 4,
          changes: previewRenameResponse.changes,
          warnings: [],
        }),
      )
      .mockResolvedValueOnce(jsonResponse(renamedColumnAppliedSchema))
      .mockResolvedValueOnce(jsonResponse(renamedColumnAppliedSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });
    const visibleRequestSchema = getVisibleSchemaDraft(
      renamedColumnRequestSchema,
    );
    const visibleDraftText = JSON.stringify(visibleRequestSchema, null, 2);
    replaceModelEditorValue(visibleDraftText);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Preview" }));

    expect(
      await screen.findByText("Rename column widgets.name to display_name"),
    ).toBeInTheDocument();
    expect(getModelEditorValue()).toBe(visibleDraftText);
    expect(getModelEditorValue()).toContain("renamedFrom");
    expect(fetchMock).toHaveBeenNthCalledWith(
      2,
      "/api/v1/admin/schema/preview",
      expect.objectContaining({
        method: "POST",
        body: JSON.stringify(visibleRequestSchema),
      }),
    );

    await user.click(screen.getByRole("button", { name: "Apply" }));
    const dialog = await screen.findByRole("dialog", {
      name: "Review and apply schema changes",
    });
    const review = within(dialog).getByTestId("schema-apply-review-scroll");
    setScrollMetrics(review, {
      scrollHeight: 400,
      clientHeight: 400,
      scrollTop: 0,
    });
    fireEvent.scroll(review);

    const confirmButton = within(dialog).getByRole("button", {
      name: "Apply",
    });
    await waitFor(() => expect(confirmButton).toBeEnabled());
    await user.click(confirmButton);

    expect(
      await screen.findByText("Schema changes applied"),
    ).toBeInTheDocument();
    expect(fetchMock).toHaveBeenNthCalledWith(
      3,
      "/api/v1/admin/schema",
      expect.objectContaining({
        method: "PUT",
        body: JSON.stringify(visibleRequestSchema),
      }),
    );
    expect(getModelEditorValue()).toBe(
      getVisibleSchemaDraftText(renamedColumnAppliedSchema),
    );
  });

  it("shows a mapped backend error when a primary-key column rename is previewed", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse(populatedSchema))
      .mockResolvedValueOnce(
        jsonResponse(previewPrimaryKeyRenameBlockedResponse),
      );
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });
    const visibleDraftText = JSON.stringify(
      getVisibleSchemaDraft(renamedPrimaryKeyRequestSchema),
      null,
      2,
    );
    const expectedIndex = visibleDraftText.indexOf('"name": "account_id"');
    const expectedLine = visibleDraftText
      .slice(0, expectedIndex)
      .split("\n").length;
    replaceModelEditorValue(visibleDraftText);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Preview" }));

    expect(
      await screen.findByRole("heading", { name: "1 error" }),
    ).toBeInTheDocument();
    expect(
      screen.getByText(
        "Primary key column rename is not supported for table 'widgets'. Keep the primary key column named 'id'.",
      ),
    ).toBeInTheDocument();
    expect(screen.queryByText("primary_key_rename_unsupported")).toBeNull();
    expect(screen.queryByText("tables[0].columns[0].name")).toBeNull();
    expect(screen.getByRole("button", { name: "Apply" })).toBeDisabled();
    expect(fetchMock).toHaveBeenNthCalledWith(
      2,
      "/api/v1/admin/schema/preview",
      expect.objectContaining({
        method: "POST",
        body: JSON.stringify(
          getVisibleSchemaDraft(renamedPrimaryKeyRequestSchema),
        ),
      }),
    );

    await user.click(
      screen.getByRole("button", {
        name: /primary key column rename is not supported/i,
      }),
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
  });

  it("shows primary-key flag changes as backend preview errors instead of visual draft errors", async () => {
    seedAuthenticated("admin");
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse(populatedSchema))
      .mockResolvedValueOnce(
        jsonResponse(previewMovedPrimaryKeyBlockedResponse),
      );
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });
    const visibleDraftText = getVisibleSchemaDraftText(
      movedPrimaryKeyDraftSchema,
    );
    replaceModelEditorValue(visibleDraftText);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));

    expect(await screen.findByTestId("schema-visual-mode")).toBeInTheDocument();
    expect(
      screen.queryByRole("region", { name: "Problems to fix" }),
    ).not.toBeInTheDocument();
    expect(screen.queryByText("visual_primary_key_must_be_id")).toBeNull();

    await user.click(screen.getByRole("button", { name: "Preview" }));

    expect(
      await screen.findByRole("heading", { name: "1 error" }),
    ).toBeInTheDocument();
    expect(
      screen.getAllByText(
        "Table 'widgets' uses 'name' as the primary key. User tables support only the 'id' column as primaryKey.",
      ).length,
    ).toBeGreaterThan(0);
    expect(screen.queryByText("tables[0].columns[1].primaryKey")).toBeNull();
    const visualDiagnostics = within(
      await screen.findByTestId("schema-visual-mode"),
    ).getByRole("region", { name: "Issues found" });
    expect(
      within(visualDiagnostics).getByText("widgets.name"),
    ).toBeInTheDocument();
    await user.click(
      within(visualDiagnostics).getByRole("button", {
        name: /uses 'name' as the primary key/i,
      }),
    );

    const inspector = screen.getByLabelText("Visual schema inspector");
    expect(within(inspector).getByLabelText("Column name")).toHaveValue("name");
    expect(screen.getByRole("button", { name: "Apply" })).toBeDisabled();
    expect(fetchMock).toHaveBeenNthCalledWith(
      2,
      "/api/v1/admin/schema/preview",
      expect.objectContaining({
        method: "POST",
        body: JSON.stringify(getVisibleSchemaDraft(movedPrimaryKeyDraftSchema)),
      }),
    );
  });

  it("exports the current draft as canonical JSON", async () => {
    seedAuthenticated("admin");
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValue(
          jsonResponse(appliedSchemaWithResolvedRelationMetadata),
        ),
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

      await screen.findByRole("textbox", { name: /schema json editor/i });
      expect(getModelEditorValue()).not.toContain("resolvedFromAccessor");
      expect(getModelEditorValue()).not.toContain("resolvedToAccessor");
      expect(getModelEditorValue()).not.toContain("resolvedJunctionTableName");

      const user = userEvent.setup();
      await user.click(screen.getByRole("button", { name: "Export" }));

      await waitFor(() => expect(capturedBlob).not.toBeNull());

      expect(anchorClick).toHaveBeenCalledTimes(1);
      expect(createObjectUrl).toHaveBeenCalledTimes(1);
      expect(revokeObjectUrl).toHaveBeenCalledTimes(1);
      expect(capturedBlob).not.toBeNull();
      expect(await readBlobText(capturedBlob as unknown as Blob)).toBe(
        getVisibleSchemaDraftText(appliedSchema),
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

    await screen.findByRole("textbox", { name: /schema json editor/i });
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

    expect(getModelEditorValue()).toBe(
      getVisibleSchemaDraftText(importedSchema),
    );
  });

  it("keeps invalid JSON editable and shows a local parse error without breaking the page", async () => {
    seedAuthenticated("admin");
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(jsonResponse(populatedSchema)),
    );

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });
    replaceModelEditorValue(
      '{\n  "version": 1,\n  "lable": faalse,\n  "tables": [],\n  "relations": []\n}',
    );

    expect(getModelEditorValue()).toBe(
      '{\n  "version": 1,\n  "lable": faalse,\n  "tables": [],\n  "relations": []\n}',
    );
    expect(screen.queryByText("Syntax error")).not.toBeInTheDocument();
    expect(
      screen.getByRole("heading", { name: "1 error" }),
    ).toBeInTheDocument();
    expect(screen.getByText(/is not valid JSON yet/)).toBeInTheDocument();
    expect(screen.queryByText("json_parse_error")).toBeNull();
    expect(screen.getByRole("button", { name: "Format JSON" })).toBeDisabled();
    expect(
      document.querySelector(
        '.schema-json-editor__gutter-line--active[data-line="3"]',
      ),
    ).not.toBeNull();
    expect(
      screen.queryByText(/backend schema validation is paused/i),
    ).toBeNull();
    expect(screen.queryByText("Fix JSON syntax before previewing")).toBeNull();

    replaceModelEditorValue(
      '{\n  "version": 1,\n  "tables": [],\n  "relations": []\n}',
    );

    expect(screen.queryByText("Syntax error")).not.toBeInTheDocument();
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

    await screen.findByRole("textbox", { name: /schema json editor/i });
    const draftText = getVisibleSchemaDraftText(populatedSchema);
    const expectedIndex = draftText.indexOf('"type": "text"');
    const expectedLine = draftText.slice(0, expectedIndex).split("\n").length;

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Preview" }));

    expect(
      await screen.findByRole("heading", { name: "1 error" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("heading", { name: "1 warning" }),
    ).toBeInTheDocument();
    expect(
      screen.getByText("Column 'widgets.name' is missing a required type."),
    ).toBeInTheDocument();
    expect(
      screen.getByText(
        "Table 'widgets' is a SQL reserved word and may require quoting.",
      ),
    ).toBeInTheDocument();
    expect(screen.queryByText("missing_column_type")).toBeNull();
    expect(screen.queryByText("tables[0].columns[1].type")).toBeNull();

    await user.click(
      screen.getByRole("button", { name: /is missing a required type/i }),
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
        body: JSON.stringify(getVisibleSchemaDraft(populatedSchema)),
      }),
    );
  });

  it("renders preview request failures as inline errors", async () => {
    seedAuthenticated("admin");
    const unsupportedTypeSchema = {
      ...populatedSchema,
      tables: [
        {
          ...populatedSchema.tables[0],
          columns: [
            populatedSchema.tables[0].columns[0],
            {
              ...populatedSchema.tables[0].columns[1],
              type: "taext",
            },
          ],
        },
      ],
    };
    const draftText = JSON.stringify(unsupportedTypeSchema, null, 2);
    const expectedIndex = draftText.indexOf('"type": "taext"');
    const expectedLine = draftText.slice(0, expectedIndex).split("\n").length;
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(jsonResponse(populatedSchema))
        .mockResolvedValueOnce(
          jsonResponse(
            {
              error: {
                code: "VALIDATION_FAILED",
                message: "The schema could not be read: Unsupported column type 'taext'.",
              },
            },
            400,
          ),
        ),
    );

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });
    replaceModelEditorValue(draftText);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Preview" }));

    expect(
      await screen.findByRole("heading", { name: "1 error" }),
    ).toBeInTheDocument();
    expect(
      screen.getByText("The schema could not be read: Unsupported column type 'taext'."),
    ).toBeInTheDocument();
    expect(screen.queryByText("VALIDATION_FAILED")).toBeNull();
    expect(screen.getByText(`Line ${expectedLine}`)).toBeInTheDocument();
    expect(screen.queryByText("Preview unavailable")).not.toBeInTheDocument();
    expect(screen.queryByText("Fix JSON syntax before previewing")).toBeNull();

    await user.click(
      screen.getByRole("button", { name: /unsupported column type/i }),
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
  });

  it("maps server path metadata in preview failures back to editor lines", async () => {
    seedAuthenticated("admin");
    const draftText = getVisibleSchemaDraftText(populatedSchema);
    const expectedIndex = draftText.indexOf('{\n          "name": "id"');
    const expectedLine = draftText.slice(0, expectedIndex).split("\n").length;
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(jsonResponse(populatedSchema))
        .mockResolvedValueOnce(
          jsonResponse(
            {
              error: {
                code: "VALIDATION_FAILED",
                message:
                  "The schema could not be read: a value has the wrong format (table 1, column 1).",
                details: { path: "$.tables[0].columns[0]" },
              },
            },
            400,
          ),
        ),
    );

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Preview" }));

    expect(
      await screen.findByRole("heading", { name: "1 error" }),
    ).toBeInTheDocument();
    expect(screen.getByText(`Line ${expectedLine}`)).toBeInTheDocument();

    await user.click(
      screen.getByRole("button", { name: /wrong format/i }),
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

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Preview" }));

    expect(screen.getByRole("heading", { name: "Tables" })).toBeInTheDocument();
    expect(
      screen.getByRole("heading", { name: "Columns" }),
    ).toBeInTheDocument();
    expect(
      screen.getAllByRole("heading", { name: "Relations" }).length,
    ).toBeGreaterThan(0);
    expect(
      screen.getByRole("heading", { name: "Indexes" }),
    ).toBeInTheDocument();
    expect(screen.getByText("Add table gadgets")).toBeInTheDocument();
    expect(screen.getByText("Add column widgets.color")).toBeInTheDocument();
    expect(screen.getByText("Add relation widget_notes")).toBeInTheDocument();
    expect(screen.getByText("Add index idx_widgets_name")).toBeInTheDocument();

  });

  it("shows destructive relation drops in the grouped preview and schema map", async () => {
    seedAuthenticated("admin");
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(jsonResponse(appliedSchema))
        .mockResolvedValueOnce(
          jsonResponse(previewDestructiveRelationDropResponse),
        ),
    );

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });
    replaceModelEditorValue(getVisibleSchemaDraftText(relationDropDraftSchema));

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Preview" }));

    const dropRelationTitle = await screen.findByText(
      "Drop relation widget_notes",
    );
    const dropRelationCard = dropRelationTitle.closest("li");
    expect(dropRelationCard).toHaveClass(
      "schema-workbench__preview-change--destructive",
    );
    expect(
      within(dropRelationCard as HTMLElement).getByText("Destructive"),
    ).toBeInTheDocument();
    expect(
      within(dropRelationCard as HTMLElement).getByText("Table Impact"),
    ).toBeInTheDocument();
    expect(
      within(dropRelationCard as HTMLElement).getByText("_jt_gadgets_widgets"),
    ).toBeInTheDocument();
    expect(
      within(dropRelationCard as HTMLElement).getByText("2"),
    ).toBeInTheDocument();
    expect(
      within(dropRelationCard as HTMLElement).getByText(
        /gadget_id: 1, widget_id: 1/i,
      ),
    ).toBeInTheDocument();

  });

  it("shows a clear no-op preview state when the draft matches the current schema", async () => {
    seedAuthenticated("admin");
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(jsonResponse(populatedSchema))
        .mockResolvedValueOnce(jsonResponse(previewNoOpWarningResponse)),
    );

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Preview" }));

    expect(
      await screen.findByRole("heading", { name: "No schema changes" }),
    ).toBeInTheDocument();
    expect(
      screen.getByText(
        "Your draft matches the current schema, so there is nothing to apply.",
      ),
    ).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Apply" })).toBeDisabled();
    expect(
      screen.getByRole("heading", { name: "1 warning" }),
    ).toBeInTheDocument();
    expect(
      screen.getByText(
        "Table 'widgets' is a SQL reserved word and may require quoting.",
      ),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("heading", { name: "Tables" }),
    ).not.toBeInTheDocument();
  });

  it("keeps formatting-only JSON edits in the no-op preview state", async () => {
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

    await screen.findByRole("textbox", { name: /schema json editor/i });

    replaceModelEditorValue(`${JSON.stringify(populatedSchema, null, 2)}\n`);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Preview" }));

    expect(
      await screen.findByRole("heading", { name: "No schema changes" }),
    ).toBeInTheDocument();
    expect(screen.getByTestId("schema-preview-no-changes")).toBeInTheDocument();
    expect(
      screen.queryByTestId("schema-preview-layout-only"),
    ).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Apply" })).toBeDisabled();
  });

  it("shows destructive lossy type changes with example casts", async () => {
    seedAuthenticated("admin");
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(jsonResponse(lossySourceSchema))
        .mockResolvedValueOnce(jsonResponse(previewLossyResponse)),
    );

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Preview" }));

    expect(await screen.findByText("1 change")).toBeInTheDocument();
    expect(
      screen.queryByText("Destructive changes require confirmation"),
    ).not.toBeInTheDocument();
    expect(
      screen.getByText("Change type of widgets.score from text to integer"),
    ).toBeInTheDocument();
    expect(
      screen.getByText("How existing values will convert"),
    ).toBeInTheDocument();
    expect(screen.getByText('"1,0" -> 1')).toBeInTheDocument();
    expect(screen.getByText('"2,0" -> 2')).toBeInTheDocument();
    expect(screen.getByText('"2,1" -> 2')).toBeInTheDocument();
    expect(screen.getByText('"two" -> 0')).toBeInTheDocument();
    expect(screen.getByText('"3,9" -> 3')).toBeInTheDocument();

  });

  it("applies a previewed Visual draft through the review modal and refreshes the live schema snapshot", async () => {
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
      .mockResolvedValueOnce(jsonResponse(appliedSchema))
      .mockResolvedValueOnce(jsonResponse(appliedSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));
    await screen.findByTestId("schema-visual-mode");
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
        screen.queryByRole("dialog", {
          name: "Review and apply schema changes",
        }),
      ).not.toBeInTheDocument();
    });

    expect(
      await screen.findByText("Schema changes applied"),
    ).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "JSON" }));
    await screen.findByRole("textbox", { name: /schema json editor/i });
    expect(getModelEditorValue()).toBe(
      getVisibleSchemaDraftText(appliedSchema),
    );
    expect(useSchemaStore.getState().snapshot).toEqual(appliedSchema);
    expect(fetchMock).toHaveBeenNthCalledWith(
      3,
      "/api/v1/admin/schema",
      expect.objectContaining({
        method: "PUT",
        body: JSON.stringify(getVisibleSchemaDraft(populatedSchema)),
      }),
    );
    expect(fetchMock).toHaveBeenNthCalledWith(
      4,
      "/api/v1/admin/schema/model",
      expect.objectContaining({
        method: "GET",
      }),
    );
    expect(fetchMock).toHaveBeenNthCalledWith(
      5,
      "/api/v1/admin/schema",
      expect.objectContaining({
        method: "GET",
      }),
    );
  });

  it("shows a no-op feedback state when apply returns applied false", async () => {
    seedAuthenticated("admin");
    const layoutOnlyPreviewResponse = {
      ...previewNoOpResponse,
      metadataOnly: true,
      normalizedModel: populatedSchema,
    };
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse(populatedSchema))
      .mockResolvedValueOnce(jsonResponse(layoutOnlyPreviewResponse))
      .mockResolvedValueOnce(
        jsonResponse({
          applied: false,
          metadataOnly: false,
          version: 3,
          changes: [],
          warnings: [],
        }),
      )
      .mockResolvedValueOnce(jsonResponse(populatedSchema))
      .mockResolvedValueOnce(jsonResponse(populatedSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Preview" }));
    await screen.findByTestId("schema-preview-layout-only");
    await user.click(screen.getByRole("button", { name: "Apply" }));

    const dialog = await screen.findByRole("dialog", {
      name: "Review and apply schema changes",
    });
    const confirmButton = within(dialog).getByRole("button", { name: "Apply" });
    // The dialog opens with Apply disabled and enables it on the next animation
    // frame once the review scroll position has been measured. Clicking before
    // that frame is a no-op, so wait for the button to become enabled.
    await waitFor(() => expect(confirmButton).toBeEnabled());
    await user.click(confirmButton);

    expect(
      await screen.findByText("No schema changes applied"),
    ).toBeInTheDocument();
    expect(
      screen.queryByText("Schema changes applied"),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByText("Designer settings saved"),
    ).not.toBeInTheDocument();
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

    await screen.findByRole("textbox", { name: /schema json editor/i });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Visual" }));
    await screen.findByTestId("schema-visual-mode");
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
        screen.queryByRole("dialog", {
          name: "Review and apply schema changes",
        }),
      ).not.toBeInTheDocument();
    });

    expect(
      await screen.findByText("Apply blocked by validation errors"),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("heading", { name: "1 error" }),
    ).toBeInTheDocument();
    expect(
      screen.getAllByText("Table 'widgets' already exists.").length,
    ).toBeGreaterThan(0);
    expect(screen.queryByText("duplicate_table_name")).toBeNull();
    const visualDiagnostics = within(
      await screen.findByTestId("schema-visual-mode"),
    ).getByRole("region", { name: "Issues found" });
    expect(within(visualDiagnostics).getByText("Canvas")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "JSON" }));
    await screen.findByRole("textbox", { name: /schema json editor/i });
    expect(getModelEditorValue()).toBe(
      getVisibleSchemaDraftText(populatedSchema),
    );
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
      .mockResolvedValueOnce(jsonResponse(destructiveAppliedSchema))
      .mockResolvedValueOnce(jsonResponse(destructiveAppliedSchema));
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });

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
      await within(dialog).findByText(
        "Confirm data loss",
      ),
    ).toBeInTheDocument();

    const destructiveButton = within(dialog).getByRole("button", {
      name: "Apply destructive changes",
    });
    expect(destructiveButton).toBeEnabled();

    await user.click(destructiveButton);

    await waitFor(() => {
      expect(
        screen.queryByRole("dialog", {
          name: "Review and apply schema changes",
        }),
      ).not.toBeInTheDocument();
    });

    expect(
      await screen.findByText("Schema changes applied"),
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
    expect(useSchemaStore.getState().snapshot).toEqual(
      destructiveAppliedSchema,
    );
  });

  it("rejects invalid imported JSON without replacing the current draft", async () => {
    seedAuthenticated("admin");
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(jsonResponse(populatedSchema)),
    );

    const router = createTestRouter(["/model"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("textbox", { name: /schema json editor/i });
    const originalText = getVisibleSchemaDraftText(populatedSchema);

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
