import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import {
  resetExplorerStoreForTest,
  useExplorerStore,
} from "../stores/explorerStore";
import { DEFAULT_EXPLORER_VIEW_CONFIG } from "../types/explorer";

// ── Helpers ────────────────────────────────────────────────────────────────

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
        editingCell: { rowId: "1", columnName: "status" },
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

// ── Setup / Teardown ───────────────────────────────────────────────────────

beforeEach(() => {
  resetAuthStoreForTest();
  resetExplorerStoreForTest();
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

// ── Tests ──────────────────────────────────────────────────────────────────

describe("commitCellEdit", () => {
  it("PATCHes the changed field, applies optimistically, and reconciles on success", async () => {
    seedAuthenticated();
    seedOrders([{ id: 1, status: "pending", total: 25.5 }]);

    const fetchMock = vi.fn(async () =>
      jsonResponse(200, {
        data: { id: 1, status: "shipped", total: 25.5, updated_at: "2026-04-18T10:00:00Z" },
      }),
    );
    vi.stubGlobal("fetch", fetchMock);

    const promise = useExplorerStore
      .getState()
      .commitCellEdit("orders", "1", "status", "shipped");

    // Optimistic apply: the row is updated and edit mode exits before the
    // PATCH resolves. Pending marker is set and prior errors cleared.
    const optimistic = useExplorerStore.getState().views.orders!;
    expect(optimistic.rows[0]!.status).toBe("shipped");
    expect(optimistic.editingCell).toBeNull();
    expect(optimistic.pendingCellSaves).toContain("1:status");

    await promise;

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const firstCall = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    const [url, init] = firstCall;
    expect(String(url)).toBe("/api/v1/data/orders/1");
    expect(init.method).toBe("PATCH");
    expect(JSON.parse(String(init.body))).toEqual({ status: "shipped" });

    const after = useExplorerStore.getState().views.orders!;
    expect(after.rows[0]).toEqual({
      id: 1,
      status: "shipped",
      total: 25.5,
      updated_at: "2026-04-18T10:00:00Z",
    });
    expect(after.pendingCellSaves).toEqual([]);
    expect(after.cellErrors).toEqual({});
  });

  it("does not call the API when the value is unchanged", async () => {
    seedAuthenticated();
    seedOrders([{ id: 1, status: "pending", total: 25.5 }]);

    const fetchMock = vi.fn(async () => jsonResponse(200, { data: {} }));
    vi.stubGlobal("fetch", fetchMock);

    await useExplorerStore
      .getState()
      .commitCellEdit("orders", "1", "status", "pending");

    expect(fetchMock).not.toHaveBeenCalled();
    const view = useExplorerStore.getState().views.orders!;
    expect(view.editingCell).toBeNull();
    expect(view.pendingCellSaves).toEqual([]);
  });

  it("rolls back the value and writes a per-field error on a 4xx field validation failure", async () => {
    seedAuthenticated();
    seedOrders([{ id: 1, status: "pending", total: 25.5 }]);

    const fetchMock = vi.fn(async () =>
      jsonResponse(422, {
        error: {
          code: "VALIDATION",
          message: "Validation failed.",
          details: {
            errors: [{ field: "status", message: "Status must be one of: pending, shipped." }],
          },
        },
      }),
    );
    vi.stubGlobal("fetch", fetchMock);

    await useExplorerStore
      .getState()
      .commitCellEdit("orders", "1", "status", "bogus");

    const view = useExplorerStore.getState().views.orders!;
    expect(view.rows[0]!.status).toBe("pending");
    expect(view.cellErrors).toEqual({
      "1": { status: "Status must be one of: pending, shipped." },
    });
    expect(view.pendingCellSaves).toEqual([]);
  });

  it("rolls back and surfaces a generic cell error on a 5xx / network failure", async () => {
    seedAuthenticated();
    seedOrders([{ id: 1, status: "pending", total: 25.5 }]);

    const fetchMock = vi.fn(async () =>
      jsonResponse(500, {
        error: { code: "INTERNAL", message: "Server exploded." },
      }),
    );
    vi.stubGlobal("fetch", fetchMock);

    await useExplorerStore
      .getState()
      .commitCellEdit("orders", "1", "status", "shipped");

    const view = useExplorerStore.getState().views.orders!;
    expect(view.rows[0]!.status).toBe("pending");
    expect(view.cellErrors["1"]?.status).toBe("Server exploded.");
    expect(view.pendingCellSaves).toEqual([]);
  });
});
