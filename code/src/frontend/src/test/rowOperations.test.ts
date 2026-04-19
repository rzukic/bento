import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import {
  resetExplorerStoreForTest,
  useExplorerStore,
} from "../stores/explorerStore";
import { resetSchemaStoreForTest, useSchemaStore } from "../stores/schemaStore";
import type { ModelSnapshot } from "../types/api";
import { DEFAULT_EXPLORER_VIEW_CONFIG } from "../types/explorer";

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

function seedSchema(snapshot: ModelSnapshot) {
  useSchemaStore.setState({
    status: "ready",
    snapshot,
    error: null,
    lastLoadedAt: Date.now(),
    inFlight: null,
  });
}

function seedOrders(rows: Record<string, unknown>[]) {
  useExplorerStore.setState((state) => ({
    activeTable: "orders",
    views: {
      ...state.views,
      orders: {
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

const sampleSnapshot: ModelSnapshot = {
  version: 4,
  tables: [
    {
      name: "orders",
      columns: [
        { name: "id", type: "int64", primaryKey: true },
        { name: "customer_id", type: "int64", nullable: true },
        { name: "status", type: "string", nullable: true },
        { name: "total", type: "double", nullable: true },
        {
          name: "created_at",
          type: "datetime",
          nullable: false,
          default: "CURRENT_TIMESTAMP",
        },
      ],
    },
  ],
  relations: [],
};

beforeEach(() => {
  resetAuthStoreForTest();
  resetExplorerStoreForTest();
  resetSchemaStoreForTest();
  vi.stubGlobal(
    "fetch",
    vi.fn(() => {
      throw new Error("fetch must be mocked in tests");
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("row operations", () => {
  it("addBlankRow inserts a local draft and first commit POSTs then reconciles to the server row", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedOrders([{ id: 1, status: "pending", total: 25.5 }]);

    const fetchMock = vi.fn(async () =>
      jsonResponse(201, {
        data: {
          id: 2,
          customer_id: null,
          status: "draft",
          total: null,
          created_at: "2026-04-18T12:00:00Z",
        },
      }),
    );
    vi.stubGlobal("fetch", fetchMock);

    useExplorerStore.getState().addBlankRow("orders");

    const added = useExplorerStore.getState().views.orders!;
    expect(added.rows).toHaveLength(2);
    const draftRow = added.rows[1]!;
    const draftId = String(draftRow.id);
    expect(added.pendingNewRowIds).toContain(draftId);
    expect(added.editingCell).toEqual({ rowId: draftId, columnName: "customer_id" });
    expect(added.meta?.total).toBe(2);

    await useExplorerStore
      .getState()
      .commitCellEdit("orders", draftId, "status", "draft");

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(String(url)).toBe("/api/v1/data/orders");
    expect(init.method).toBe("POST");
    expect(JSON.parse(String(init.body))).toEqual({ status: "draft" });

    const after = useExplorerStore.getState().views.orders!;
    expect(after.pendingNewRowIds).toEqual([]);
    expect(after.rows[1]).toEqual({
      id: 2,
      customer_id: null,
      status: "draft",
      total: null,
      created_at: "2026-04-18T12:00:00Z",
    });
    expect(after.meta?.total).toBe(2);
  });

  it("cancelCellEdit drops a never-saved blank row without an API call", () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedOrders([{ id: 1, status: "pending", total: 25.5 }]);

    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);

    useExplorerStore.getState().addBlankRow("orders");
    expect(useExplorerStore.getState().views.orders!.rows).toHaveLength(2);

    useExplorerStore.getState().cancelCellEdit("orders");

    const after = useExplorerStore.getState().views.orders!;
    expect(after.rows).toHaveLength(1);
    expect(after.pendingNewRowIds).toEqual([]);
    expect(after.meta?.total).toBe(1);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("deleteRow removes optimistically, then restores the row and surfaces a row error on failure", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedOrders([
      { id: 1, status: "pending", total: 25.5 },
      { id: 2, status: "shipped", total: 99.0 },
    ]);

    const fetchMock = vi.fn(async () =>
      jsonResponse(500, {
        error: { code: "INTERNAL", message: "Delete failed." },
      }),
    );
    vi.stubGlobal("fetch", fetchMock);

    const promise = useExplorerStore.getState().deleteRow("orders", "2");

    const optimistic = useExplorerStore.getState().views.orders!;
    expect(optimistic.rows).toEqual([{ id: 1, status: "pending", total: 25.5 }]);
    expect(optimistic.meta?.total).toBe(1);

    await promise;

    const after = useExplorerStore.getState().views.orders!;
    expect(after.rows).toEqual([
      { id: 1, status: "pending", total: 25.5 },
      { id: 2, status: "shipped", total: 99.0 },
    ]);
    expect(after.meta?.total).toBe(2);
    expect(after.rowErrors["2"]).toBe("Delete failed.");
  });

  it("duplicateRow POSTs writable values only and inserts the copy after the source row", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedOrders([
      {
        id: 1,
        customer_id: 4,
        status: "pending",
        total: 25.5,
        created_at: "2026-04-18T10:00:00Z",
      },
      {
        id: 2,
        customer_id: 8,
        status: "shipped",
        total: 99.0,
        created_at: "2026-04-18T11:00:00Z",
      },
    ]);

    const fetchMock = vi.fn(async () =>
      jsonResponse(201, {
        data: {
          id: 3,
          customer_id: 4,
          status: "pending",
          total: 25.5,
          created_at: "2026-04-18T12:30:00Z",
        },
      }),
    );
    vi.stubGlobal("fetch", fetchMock);

    await useExplorerStore.getState().duplicateRow("orders", "1");

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(String(url)).toBe("/api/v1/data/orders");
    expect(init.method).toBe("POST");
    expect(JSON.parse(String(init.body))).toEqual({
      customer_id: 4,
      status: "pending",
      total: 25.5,
    });

    const after = useExplorerStore.getState().views.orders!;
    expect(after.rows.map((row) => row.id)).toEqual([1, 3, 2]);
    expect(after.meta?.total).toBe(3);
  });

  it("undoLast recreates a deleted row and reinserts it near its original position", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedOrders([
      {
        id: 2,
        customer_id: 8,
        status: "shipped",
        total: 99.0,
        created_at: "2026-04-18T11:00:00Z",
      },
    ]);

    useExplorerStore.getState().pushUndoDeleteEntry("orders", [
      {
        index: 0,
        row: {
          id: 1,
          customer_id: 4,
          status: "pending",
          total: 25.5,
          created_at: "2026-04-18T10:00:00Z",
        },
      },
    ]);

    const fetchMock = vi.fn(async () =>
      jsonResponse(201, {
        data: {
          id: 10,
          customer_id: 4,
          status: "pending",
          total: 25.5,
          created_at: "2026-04-18T12:30:00Z",
        },
      }),
    );
    vi.stubGlobal("fetch", fetchMock);

    const result = await useExplorerStore.getState().undoLast("orders");

    expect(result).toEqual({ successCount: 1, failureCount: 0, total: 1 });
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(String(url)).toBe("/api/v1/data/orders");
    expect(init.method).toBe("POST");
    expect(JSON.parse(String(init.body))).toEqual({
      customer_id: 4,
      status: "pending",
      total: 25.5,
    });

    const after = useExplorerStore.getState().views.orders!;
    expect(after.rows.map((row) => row.id)).toEqual([10, 2]);
    expect(after.meta?.total).toBe(2);
    expect(useExplorerStore.getState().undoStackByTable.orders).toEqual([]);
  });

  it("caps the undo stack at 10 entries per table", () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedOrders([]);

    for (let index = 0; index < 11; index += 1) {
      useExplorerStore.getState().pushUndoDeleteEntry("orders", [
        {
          index: 0,
          row: { id: index, status: `row-${index}` },
        },
      ]);
    }

    const stack = useExplorerStore.getState().undoStackByTable.orders ?? [];
    expect(stack).toHaveLength(10);
    expect(stack[0]?.rows[0]?.row.id).toBe(1);
    expect(stack.at(-1)?.rows[0]?.row.id).toBe(10);
  });

  it("undoLast consumes the entry when restore POST fails", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedOrders([]);

    useExplorerStore.getState().pushUndoDeleteEntry("orders", [
      {
        index: 0,
        row: {
          id: 1,
          customer_id: 4,
          status: "pending",
          total: 25.5,
          created_at: "2026-04-18T10:00:00Z",
        },
      },
    ]);

    const fetchMock = vi.fn(async () =>
      jsonResponse(500, {
        error: { code: "INTERNAL", message: "Restore failed." },
      }),
    );
    vi.stubGlobal("fetch", fetchMock);

    const result = await useExplorerStore.getState().undoLast("orders");

    expect(result).toEqual({ successCount: 0, failureCount: 1, total: 1 });
    expect(useExplorerStore.getState().undoStackByTable.orders).toEqual([]);
    expect(useExplorerStore.getState().views.orders?.rows).toEqual([]);
  });
});
