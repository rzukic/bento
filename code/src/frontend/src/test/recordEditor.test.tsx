import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RecordEditor } from "../features/data-explorer/RecordEditor";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import {
  resetExplorerStoreForTest,
  useExplorerStore,
} from "../stores/explorerStore";
import { resetSchemaStoreForTest, useSchemaStore } from "../stores/schemaStore";
import type { ModelSnapshot } from "../types/api";
import { DEFAULT_EXPLORER_VIEW_CONFIG } from "../types/explorer";

const explorerSnapshot: ModelSnapshot = {
  version: 3,
  tables: [
    {
      name: "orders",
      displayName: "Orders",
      columns: [
        { name: "id", type: "int64", primaryKey: true },
        {
          name: "customer_id",
          displayName: "Customer",
          type: "int64",
          nullable: true,
          references: { table: "users", column: "id" },
        },
        { name: "status", type: "enum", values: ["pending", "shipped"], nullable: false },
        { name: "note", type: "string", nullable: true },
        { name: "total", type: "double", nullable: true },
        { name: "created_at", type: "computed" },
      ],
    },
    {
      name: "users",
      displayName: "Users",
      columns: [
        { name: "id", type: "int64", primaryKey: true },
        { name: "name", type: "string", nullable: false },
        { name: "email", type: "string", nullable: true },
      ],
    },
  ],
};

const baseOrderRow: Record<string, unknown> = {
  id: 1,
  customer_id: null,
  status: "pending",
  note: "Parent order",
  total: 25,
};

const relatedUserRow: Record<string, unknown> = {
  id: 7,
  name: "Alice",
  email: "alice@example.com",
};

function seedAuthenticated() {
  useAuthStore.setState({
    status: "authenticated",
    user: { id: 1, username: "admin", role: "admin" },
    accessToken: "test-token",
    expiresAt: Date.now() + 60_000,
    loginInFlight: false,
    error: null,
  });
}

function seedSchema(snapshot: ModelSnapshot = explorerSnapshot) {
  useSchemaStore.setState({
    status: "ready",
    snapshot,
    error: null,
    lastLoadedAt: Date.now(),
    inFlight: null,
  });
}

function seedExplorerView(table: string, rows: Record<string, unknown>[]) {
  useExplorerStore.setState((state) => ({
    activeTable: table,
    views: {
      ...state.views,
      [table]: {
        status: "ready",
        config: { ...DEFAULT_EXPLORER_VIEW_CONFIG },
        page: 1,
        perPage: 25,
        rows,
        meta: { page: 1, per_page: 25, total: rows.length, total_pages: 1 },
        error: null,
        source: { kind: "table" },
        selectedRowIds: [],
        editingCell: null,
        cellErrors: {},
        pendingCellSaves: [],
        pendingNewRowIds: [],
        rowErrors: {},
        context: null,
        savedViews: [],
        savedViewsStatus: "ready" as const,
        activeViewId: null,
        autoSave: false,
      },
    },
  }));
}

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function listResponse(rows: Record<string, unknown>[]): Response {
  return jsonResponse(200, {
    data: rows,
    meta: { page: 1, per_page: Math.max(rows.length, 1), total: rows.length, total_pages: 1 },
  });
}

function getTopEntry() {
  return useExplorerStore.getState().recordEditor?.stack.at(-1) ?? null;
}

function renderRecordEditor() {
  return render(
    <MemoryRouter>
      <RecordEditor />
    </MemoryRouter>,
  );
}

function makeFetchMock(options?: {
  failOrderFetch?: boolean;
  failUserFetch?: boolean;
  updatedNote?: string;
  orderRow?: Record<string, unknown>;
}) {
  const failOrderFetch = options?.failOrderFetch ?? false;
  const failUserFetch = options?.failUserFetch ?? false;
  const updatedNote = options?.updatedNote ?? "updated";
  const orderRow = { ...baseOrderRow, ...(options?.orderRow ?? {}) };

  return vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);

    if (
      url.includes("/api/v1/data/users?") &&
      url.includes("filter%5Bid%5D%5Bin%5D=7")
    ) {
      return listResponse([relatedUserRow]);
    }

    if (init?.method === "PATCH" && url.includes("/api/v1/data/orders/1")) {
      return jsonResponse(200, {
        data: { ...orderRow, note: updatedNote },
      });
    }

    if (url.includes("/api/v1/data/orders/1")) {
      if (failOrderFetch) {
        return jsonResponse(500, { error: { code: "INTERNAL", message: "Boom." } });
      }
      return jsonResponse(200, {
        data: { ...orderRow, created_at: "2026-04-18T00:00:00Z" },
      });
    }

    if (url.includes("/api/v1/data/users/7")) {
      if (failUserFetch) {
        return jsonResponse(500, { error: { code: "INTERNAL", message: "User boom." } });
      }
      return jsonResponse(200, { data: relatedUserRow });
    }

    return jsonResponse(404, { error: { code: "NOT_FOUND", message: `Unhandled: ${url}` } });
  });
}

beforeEach(() => {
  resetAuthStoreForTest();
  resetExplorerStoreForTest();
  resetSchemaStoreForTest();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("RecordEditor — TASK-098 scaffold", () => {
  it("renders nothing when the editor is closed", () => {
    seedAuthenticated();
    seedSchema();
    renderRecordEditor();
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("opens via openRecordEditor, fetches the row, and renders editors per column", async () => {
    seedAuthenticated();
    seedSchema();
    seedExplorerView("orders", [{ ...baseOrderRow, note: "hi" }]);

    vi.stubGlobal("fetch", makeFetchMock({ orderRow: { note: "hi" } }));

    renderRecordEditor();
    void useExplorerStore.getState().openRecordEditor({ table: "orders", rowId: 1 });

    await waitFor(() =>
      expect(getTopEntry()?.status).toBe("ready"),
    );

    const dialog = await screen.findByRole("dialog");
    expect(
      within(dialog).queryByRole("button", {
        name: /id/i,
      }),
    ).toBeNull();

    const noteField = within(dialog)
      .getAllByRole("button")
      .find((el) => el.id === "record-editor-field-orders-note");
    expect(noteField?.textContent).toContain("hi");

    await userEvent.click(noteField!);
    const noteInput = within(dialog).getByRole("textbox", { name: "note" });
    expect((noteInput as HTMLInputElement).value).toBe("hi");
    expect(within(dialog).queryByRole("textbox", { name: "created_at" })).toBeNull();
  });

  it("Esc and the close button dismiss the modal at root depth", async () => {
    seedAuthenticated();
    seedSchema();
    seedExplorerView("orders", [baseOrderRow]);

    vi.stubGlobal("fetch", makeFetchMock());

    renderRecordEditor();
    await useExplorerStore.getState().openRecordEditor({ table: "orders", rowId: 1 });

    await screen.findByRole("dialog");
    await userEvent.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());

    await useExplorerStore.getState().openRecordEditor({ table: "orders", rowId: 1 });
    await screen.findByRole("dialog");
    const closeButtons = screen.getAllByRole("button", { name: /close record editor/i });
    await userEvent.click(closeButtons[0]!);
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("commits a field edit through commitCellEdit and reflects the new value", async () => {
    seedAuthenticated();
    seedSchema();
    seedExplorerView("orders", [{ ...baseOrderRow, note: "hi" }]);

    const fetchMock = makeFetchMock({
      updatedNote: "updated",
      orderRow: { note: "hi" },
    });
    vi.stubGlobal("fetch", fetchMock);

    renderRecordEditor();
    await useExplorerStore.getState().openRecordEditor({ table: "orders", rowId: 1 });
    const dialog = await screen.findByRole("dialog");

    const noteField = within(dialog)
      .getAllByRole("button")
      .find((el) => el.id === "record-editor-field-orders-note");
    await userEvent.click(noteField!);
    const noteInput = within(dialog).getByRole("textbox", { name: "note" });
    await userEvent.clear(noteInput);
    await userEvent.type(noteInput, "updated");
    noteInput.blur();

    await waitFor(() =>
      expect(
        fetchMock.mock.calls.some(
          ([url, init]) =>
            String(url).includes("/api/v1/data/orders/1") &&
            (init as RequestInit | undefined)?.method === "PATCH",
        ),
      ).toBe(true),
    );

    await waitFor(() => {
      const view = useExplorerStore.getState().views.orders!;
      expect(view.rows[0]!.note).toBe("updated");
    });
  });

  it("shows an error and Retry when the initial fetch fails", async () => {
    seedAuthenticated();
    seedSchema();
    seedExplorerView("orders", []);

    vi.stubGlobal("fetch", makeFetchMock({ failOrderFetch: true }));

    renderRecordEditor();
    await useExplorerStore.getState().openRecordEditor({ table: "orders", rowId: 1 });

    await waitFor(() =>
      expect(getTopEntry()?.status).toBe("error"),
    );
    expect(screen.getByText(/boom\./i)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /retry/i })).toBeInTheDocument();
  });
});

describe("RecordEditor — TASK-099 FK navigation stack", () => {
  it("pushes a related record onto the stack and renders breadcrumbs", async () => {
    seedAuthenticated();
    seedSchema();
    seedExplorerView("orders", [{ ...baseOrderRow, customer_id: 7 }]);

    vi.stubGlobal("fetch", makeFetchMock({ orderRow: { customer_id: 7 } }));

    renderRecordEditor();
    await useExplorerStore.getState().openRecordEditor({ table: "orders", rowId: 1 });
    await useExplorerStore.getState().pushRecordEditor({ table: "users", rowId: 7 });

    await waitFor(() => {
      expect(useExplorerStore.getState().recordEditor?.stack).toHaveLength(2);
      expect(getTopEntry()?.table).toBe("users");
      expect(getTopEntry()?.status).toBe("ready");
    });

    expect(screen.getByTestId("record-editor-back")).toBeInTheDocument();
    const breadcrumbs = screen.getByTestId("record-editor-breadcrumbs");
    expect(within(breadcrumbs).getByText("Parent order")).toBeInTheDocument();
    expect(within(breadcrumbs).getByText("Alice")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Alice" })).toBeInTheDocument();
  });

  it("uses Escape as pop at depth > 1 and closes only after returning to root", async () => {
    seedAuthenticated();
    seedSchema();
    seedExplorerView("orders", [{ ...baseOrderRow, customer_id: 7 }]);

    vi.stubGlobal("fetch", makeFetchMock({ orderRow: { customer_id: 7 } }));

    renderRecordEditor();
    await useExplorerStore.getState().openRecordEditor({ table: "orders", rowId: 1 });
    await useExplorerStore.getState().pushRecordEditor({ table: "users", rowId: 7 });
    await waitFor(() => expect(getTopEntry()?.table).toBe("users"));

    await userEvent.keyboard("{Escape}");
    await waitFor(() => {
      expect(useExplorerStore.getState().recordEditor?.stack).toHaveLength(1);
      expect(getTopEntry()?.table).toBe("orders");
    });
    expect(screen.getByRole("dialog")).toBeInTheDocument();

    await userEvent.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("pops back without refetching and keeps the ancestor entry intact after a failed deep push", async () => {
    seedAuthenticated();
    seedSchema();
    seedExplorerView("orders", [{ ...baseOrderRow, customer_id: 7 }]);

    const fetchMock = makeFetchMock({
      failUserFetch: true,
      orderRow: { customer_id: 7 },
    });
    vi.stubGlobal("fetch", fetchMock);

    renderRecordEditor();
    await useExplorerStore.getState().openRecordEditor({ table: "orders", rowId: 1 });
    await useExplorerStore.getState().pushRecordEditor({ table: "users", rowId: 7 });
    await waitFor(() => expect(getTopEntry()?.status).toBe("error"));

    const orderFetchCallsBeforePop = fetchMock.mock.calls.filter(([url]) =>
      String(url).includes("/api/v1/data/orders/1"),
    ).length;

    const editorState = useExplorerStore.getState().recordEditor;
    expect(editorState?.stack[0]?.table).toBe("orders");
    expect(editorState?.stack[0]?.status).toBe("ready");
    expect(editorState?.stack[1]?.table).toBe("users");
    expect(editorState?.stack[1]?.error).toMatch(/user boom\./i);
    expect(screen.getByText(/user boom\./i)).toBeInTheDocument();

    await userEvent.click(screen.getByTestId("record-editor-back"));
    await waitFor(() => {
      expect(useExplorerStore.getState().recordEditor?.stack).toHaveLength(1);
      expect(getTopEntry()?.table).toBe("orders");
    });

    const orderFetchCallsAfterPop = fetchMock.mock.calls.filter(([url]) =>
      String(url).includes("/api/v1/data/orders/1"),
    ).length;
    expect(orderFetchCallsAfterPop).toBe(orderFetchCallsBeforePop);
    expect(screen.getByRole("heading", { name: "Parent order" })).toBeInTheDocument();
  });

  it("lets FK fields enter edit mode by clicking the full-width field", async () => {
    seedAuthenticated();
    seedSchema();
    seedExplorerView("orders", [{ ...baseOrderRow, customer_id: 7 }]);

    vi.stubGlobal("fetch", makeFetchMock({ orderRow: { customer_id: 7 } }));

    renderRecordEditor();
    await useExplorerStore.getState().openRecordEditor({ table: "orders", rowId: 1 });

    const customerField = screen
      .getAllByRole("button")
      .find((element) => element.id === "record-editor-field-orders-customer_id");
    expect(customerField).toBeTruthy();
    await waitFor(() => expect(customerField).toHaveTextContent("Alice"));
    await userEvent.click(customerField!);

    await waitFor(() => {
      const view = useExplorerStore.getState().views.orders!;
      expect(view.editingCell).toEqual({ rowId: "1", columnName: "customer_id" });
    });
    expect(useExplorerStore.getState().recordEditor?.stack).toHaveLength(1);
    expect(getTopEntry()?.table).toBe("orders");
  });

  it("opens the related record when the FK chip itself is clicked", async () => {
    seedAuthenticated();
    seedSchema();
    seedExplorerView("orders", [{ ...baseOrderRow, customer_id: 7 }]);

    vi.stubGlobal("fetch", makeFetchMock({ orderRow: { customer_id: 7 } }));

    renderRecordEditor();
    await useExplorerStore.getState().openRecordEditor({ table: "orders", rowId: 1 });

    const chip = await screen.findByTestId("record-editor-reference-chip-customer_id");
    await waitFor(() => expect(chip).toHaveTextContent("Alice"));
    await userEvent.click(chip);

    await waitFor(() => {
      expect(useExplorerStore.getState().recordEditor?.stack).toHaveLength(2);
      expect(getTopEntry()?.table).toBe("users");
      expect(getTopEntry()?.status).toBe("ready");
    });

    expect(screen.getByRole("heading", { name: "Alice" })).toBeInTheDocument();
    expect(useExplorerStore.getState().views.orders?.editingCell).toBeNull();
  });
});

describe("RecordEditor — TASK-108 row cache isolation", () => {
  it("keeps an off-query FK record out of the destination table view while still editing it in the modal", async () => {
    seedAuthenticated();
    seedSchema();
    seedExplorerView("orders", [{ ...baseOrderRow, customer_id: 7 }]);
    seedExplorerView("users", [
      { id: 99, name: "Bob", email: "bob@example.com" },
    ]);

    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (
        url.includes("/api/v1/data/users?") &&
        url.includes("filter%5Bid%5D%5Bin%5D=7")
      ) {
        return listResponse([relatedUserRow]);
      }

      if (url.includes("/api/v1/data/orders/1")) {
        return jsonResponse(200, {
          data: { ...baseOrderRow, customer_id: 7, created_at: "2026-04-18T00:00:00Z" },
        });
      }

      if (init?.method === "PATCH" && url.includes("/api/v1/data/users/7")) {
        return jsonResponse(200, {
          data: { ...relatedUserRow, name: "Alicia" },
        });
      }

      if (url.includes("/api/v1/data/users/7")) {
        return jsonResponse(200, { data: relatedUserRow });
      }

      return jsonResponse(404, {
        error: { code: "NOT_FOUND", message: `Unhandled: ${url}` },
      });
    });
    vi.stubGlobal("fetch", fetchMock);

    renderRecordEditor();
    await useExplorerStore.getState().openRecordEditor({ table: "orders", rowId: 1 });
    await useExplorerStore.getState().pushRecordEditor({ table: "users", rowId: 7 });

    expect(await screen.findByRole("heading", { name: "Alice" })).toBeInTheDocument();

    let usersView = useExplorerStore.getState().views.users!;
    expect(usersView.rows).toEqual([{ id: 99, name: "Bob", email: "bob@example.com" }]);
    expect(usersView.meta?.total).toBe(1);
    expect(useExplorerStore.getState().recordCacheByTable.users?.["7"]).toEqual(relatedUserRow);

    const nameField = screen
      .getAllByRole("button")
      .find((element) => element.id === "record-editor-field-users-name");
    expect(nameField).toBeTruthy();

    const user = userEvent.setup();
    await user.click(nameField!);
    const nameInput = screen.getByRole("textbox", { name: "name" });
    await user.clear(nameInput);
    await user.type(nameInput, "Alicia");
    nameInput.blur();

    await waitFor(() =>
      expect(
        fetchMock.mock.calls.some(
          ([url, requestInit]) =>
            String(url).includes("/api/v1/data/users/7") &&
            (requestInit as RequestInit | undefined)?.method === "PATCH",
        ),
      ).toBe(true),
    );

    await waitFor(() =>
      expect(screen.getByRole("heading", { name: "Alicia" })).toBeInTheDocument(),
    );

    usersView = useExplorerStore.getState().views.users!;
    expect(usersView.rows).toEqual([{ id: 99, name: "Bob", email: "bob@example.com" }]);
    expect(usersView.meta?.total).toBe(1);
    expect(useExplorerStore.getState().recordCacheByTable.users?.["7"]).toEqual({
      ...relatedUserRow,
      name: "Alicia",
    });
  });
});

describe("RecordEditor — TASK-101 relation sections", () => {
  it("renders incoming relation sections with count and preview rows", async () => {
    seedAuthenticated();
    seedSchema();
    seedExplorerView("users", [relatedUserRow]);

    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes("/api/v1/data/users/7")) {
        return jsonResponse(200, { data: relatedUserRow });
      }

      if (
        url.includes("/api/v1/data/orders?") &&
        url.includes("filter%5Bcustomer_id%5D=7") &&
        url.includes("per_page=5")
      ) {
        return listResponse([
          {
            id: 11,
            customer_id: 7,
            status: "pending",
            note: "First related order",
            total: 120,
          },
          {
            id: 12,
            customer_id: 7,
            status: "shipped",
            note: "Second related order",
            total: 80,
          },
        ]);
      }

      return jsonResponse(404, { error: { code: "NOT_FOUND", message: `Unhandled: ${url}` } });
    });
    vi.stubGlobal("fetch", fetchMock);

    renderRecordEditor();
    await useExplorerStore.getState().openRecordEditor({ table: "users", rowId: 7 });

    expect(await screen.findByRole("heading", { name: "Alice" })).toBeInTheDocument();
    expect(await screen.findByRole("heading", { name: "Orders" })).toBeInTheDocument();
    expect(await screen.findByText("2")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /first related order/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /second related order/i })).toBeInTheDocument();
  });

  it("pushes a preview row onto the editor stack when clicked", async () => {
    seedAuthenticated();
    seedSchema();
    seedExplorerView("users", [relatedUserRow]);

    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes("/api/v1/data/users/7")) {
        return jsonResponse(200, { data: relatedUserRow });
      }

      if (
        url.includes("/api/v1/data/orders?") &&
        url.includes("filter%5Bcustomer_id%5D=7")
      ) {
        return listResponse([
          {
            id: 11,
            customer_id: 7,
            status: "pending",
            note: "First related order",
            total: 120,
          },
        ]);
      }

      if (url.includes("/api/v1/data/orders/11")) {
        return jsonResponse(200, {
          data: {
            id: 11,
            customer_id: 7,
            status: "pending",
            note: "First related order",
            total: 120,
          },
        });
      }

      return jsonResponse(404, { error: { code: "NOT_FOUND", message: `Unhandled: ${url}` } });
    });
    vi.stubGlobal("fetch", fetchMock);

    renderRecordEditor();
    await useExplorerStore.getState().openRecordEditor({ table: "users", rowId: 7 });

    const preview = await screen.findByRole("button", { name: /first related order/i });
    await userEvent.click(preview);

    await waitFor(() => {
      expect(useExplorerStore.getState().recordEditor?.stack).toHaveLength(2);
      expect(getTopEntry()?.table).toBe("orders");
    });
    expect(screen.getByRole("heading", { name: "First related order" })).toBeInTheDocument();
  });

  it("isolates relation-section errors so one failed section does not break another", async () => {
    seedAuthenticated();
    seedSchema({
      version: 4,
      tables: [
        ...explorerSnapshot.tables,
        {
          name: "tickets",
          displayName: "Tickets",
          columns: [
            { name: "id", type: "int64", primaryKey: true },
            {
              name: "customer_id",
              displayName: "Customer",
              type: "int64",
              references: { table: "users", column: "id" },
            },
            { name: "subject", type: "string", nullable: false },
          ],
        },
      ],
    });
    seedExplorerView("users", [relatedUserRow]);

    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes("/api/v1/data/users/7")) {
        return jsonResponse(200, { data: relatedUserRow });
      }

      if (url.includes("/api/v1/data/orders?") && url.includes("filter%5Bcustomer_id%5D=7")) {
        return listResponse([
          {
            id: 11,
            customer_id: 7,
            status: "pending",
            note: "First related order",
            total: 120,
          },
        ]);
      }

      if (url.includes("/api/v1/data/tickets?") && url.includes("filter%5Bcustomer_id%5D=7")) {
        return jsonResponse(500, {
          error: { code: "INTERNAL", message: "Ticket relation failed." },
        });
      }

      return jsonResponse(404, { error: { code: "NOT_FOUND", message: `Unhandled: ${url}` } });
    });
    vi.stubGlobal("fetch", fetchMock);

    renderRecordEditor();
    await useExplorerStore.getState().openRecordEditor({ table: "users", rowId: 7 });

    expect(await screen.findByRole("heading", { name: "Orders" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /first related order/i })).toBeInTheDocument();
    expect(await screen.findByRole("heading", { name: "Tickets" })).toBeInTheDocument();
    expect(
      await screen.findByTestId("record-editor-relation-error-tickets"),
    ).toHaveTextContent(/ticket relation failed\./i);
  });

  it("renders no relation footer when the table has no incoming foreign keys", async () => {
    seedAuthenticated();
    seedSchema();
    seedExplorerView("orders", [baseOrderRow]);

    vi.stubGlobal("fetch", makeFetchMock());

    renderRecordEditor();
    await useExplorerStore.getState().openRecordEditor({ table: "orders", rowId: 1 });

    await screen.findByRole("heading", { name: "Parent order" });
    expect(
      screen.queryByRole("button", { name: /view all/i }),
    ).toBeNull();
    expect(
      screen.queryByTestId("record-editor-relation-error-orders"),
    ).toBeNull();
  });
});
