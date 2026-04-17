import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { buildQueryString } from "../api/data";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import {
  extractExplorerViewConfig,
  getExplorerQueryForView,
  hydrateTableViewFromConfig,
  resetExplorerStoreForTest,
  useExplorerStore
} from "../stores/explorerStore";
import {
  DEFAULT_EXPLORER_VIEW_CONFIG,
  DEFAULT_QUERY
} from "../types/explorer";
import type { ExplorerQuery, ListResponse } from "../types/explorer";

// ── Helpers ──────────────────────────────────────────────────────

function seedAuthenticated() {
  useAuthStore.setState({
    status: "authenticated",
    user: { id: 1, username: "admin", role: "admin" },
    accessToken: "test-token",
    expiresAt: Date.now() + 60_000,
    loginInFlight: false,
    error: null
  });
}

function mockListResponse(rows: Record<string, unknown>[], total: number, page = 1, perPage = 25): ListResponse {
  return {
    data: rows,
    meta: {
      page,
      per_page: perPage,
      total,
      total_pages: Math.ceil(total / perPage)
    }
  };
}

// ── Setup ────────────────────────────────────────────────────────

beforeEach(() => {
  resetAuthStoreForTest();
  resetExplorerStoreForTest();
  vi.stubGlobal(
    "fetch",
    vi.fn(() => {
      throw new Error("fetch must be mocked in tests");
    })
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

// ── buildQueryString ─────────────────────────────────────────────

describe("buildQueryString", () => {
  it("serialises the default query with page and per_page", () => {
    const qs = buildQueryString(DEFAULT_QUERY);
    expect(qs).toContain("page=1");
    expect(qs).toContain("per_page=25");
    expect(qs).not.toContain("sort=");
    expect(qs).not.toContain("search=");
    expect(qs).not.toContain("filter");
  });

  it("encodes ascending and descending sorts", () => {
    const query: ExplorerQuery = {
      ...DEFAULT_QUERY,
      sort: [
        { column: "name", dir: "asc" },
        { column: "created_at", dir: "desc" }
      ]
    };
    const qs = buildQueryString(query);
    expect(qs).toContain("sort=name%2C-created_at");
  });

  it("encodes a search term", () => {
    const query: ExplorerQuery = { ...DEFAULT_QUERY, search: "hello world" };
    const qs = buildQueryString(query);
    expect(qs).toContain("search=hello+world");
  });

  it("preserves multi-word search terms for backend token matching", () => {
    const query: ExplorerQuery = { ...DEFAULT_QUERY, search: "james lastname" };
    const qs = buildQueryString(query);
    expect(qs).toContain("search=james+lastname");
  });

  it("encodes an equality filter as shorthand", () => {
    const query: ExplorerQuery = {
      ...DEFAULT_QUERY,
      filters: [{ column: "status", op: "eq", value: "active" }]
    };
    const qs = buildQueryString(query);
    // Shorthand: filter[status]=active (no [eq])
    expect(qs).toContain("filter%5Bstatus%5D=active");
    expect(qs).not.toContain("[eq]");
  });

  it("encodes operator filters with the full bracket syntax", () => {
    const query: ExplorerQuery = {
      ...DEFAULT_QUERY,
      filters: [
        { column: "amount", op: "gte", value: "100" },
        { column: "amount", op: "lt", value: "500" }
      ]
    };
    const qs = buildQueryString(query);
    expect(qs).toContain("filter%5Bamount%5D%5Bgte%5D=100");
    expect(qs).toContain("filter%5Bamount%5D%5Blt%5D=500");
  });

  it("encodes nullary operators (null, notnull) with empty value", () => {
    const query: ExplorerQuery = {
      ...DEFAULT_QUERY,
      filters: [{ column: "deleted_at", op: "null" }]
    };
    const qs = buildQueryString(query);
    expect(qs).toContain("filter%5Bdeleted_at%5D%5Bnull%5D=");
  });
});

// ── explorerStore ────────────────────────────────────────────────

describe("explorerStore", () => {
  it("creates a fresh view entry when setActiveTable is called", () => {
    useExplorerStore.getState().setActiveTable("orders");
    const state = useExplorerStore.getState();
    expect(state.activeTable).toBe("orders");
    expect(state.views.orders).toBeDefined();
    expect(state.views.orders.status).toBe("idle");
    expect(state.views.orders.config).toEqual(DEFAULT_EXPLORER_VIEW_CONFIG);
    expect(getExplorerQueryForView(state.views.orders)).toEqual(DEFAULT_QUERY);
  });

  it("preserves existing view state when switching back to a table", () => {
    const store = useExplorerStore.getState();
    store.setActiveTable("orders");
    // Mutate the query to simulate navigation
    useExplorerStore.setState((s) => ({
      views: {
        ...s.views,
        orders: {
          ...s.views.orders,
          page: 3
        }
      }
    }));

    // Switch away
    store.setActiveTable("products");
    expect(useExplorerStore.getState().activeTable).toBe("products");

    // Switch back — orders retains page 3
    useExplorerStore.getState().setActiveTable("orders");
    expect(useExplorerStore.getState().views.orders.page).toBe(3);
  });

  it("fetches rows and populates the view on success", async () => {
    seedAuthenticated();

    const response = mockListResponse(
      [{ id: 1, name: "Widget" }, { id: 2, name: "Gadget" }],
      42,
      1,
      25
    );

    vi.stubGlobal(
      "fetch",
      vi.fn(async () =>
        new Response(JSON.stringify(response), {
          status: 200,
          headers: { "Content-Type": "application/json" }
        })
      )
    );

    useExplorerStore.getState().setActiveTable("orders");
    await useExplorerStore.getState().fetchRows();

    const view = useExplorerStore.getState().views.orders;
    expect(view.status).toBe("ready");
    expect(view.rows).toHaveLength(2);
    expect(view.meta?.total).toBe(42);
    expect(view.meta?.total_pages).toBe(2);
  });

  it("fetches related rows through the backend relation sub-resource endpoint", async () => {
    seedAuthenticated();

    const fetchMock = vi.fn(async (_input: RequestInfo | URL) =>
      new Response(JSON.stringify(mockListResponse([{ id: 101, total: 149.5 }], 1)), {
        status: 200,
        headers: { "Content-Type": "application/json" }
      })
    );

    vi.stubGlobal("fetch", fetchMock);

    useExplorerStore.getState().openRelatedContext(
      "orders",
      "customers",
      "7",
      "customer_orders"
    );
    await useExplorerStore.getState().fetchRows();

    expect(String(fetchMock.mock.calls.at(0)?.[0])).toContain(
      "/api/v1/data/customers/7/customer_orders?page=1&per_page=25"
    );

    const view = useExplorerStore.getState().views.orders;
    expect(view.status).toBe("ready");
    expect(view.context).toEqual({
      kind: "related",
      parentTable: "customers",
      parentRowIds: ["7"],
      accessor: "customer_orders",
      via: "endpoint"
    });
    expect(view.config.type).toBe("table");
  });

  it("opens filter-backed related rows as a normal table query", async () => {
    seedAuthenticated();

    const fetchMock = vi.fn(async (_input: RequestInfo | URL) =>
      new Response(JSON.stringify(mockListResponse([{ id: 101, total: 149.5 }], 1)), {
        status: 200,
        headers: { "Content-Type": "application/json" }
      })
    );

    vi.stubGlobal("fetch", fetchMock);

    useExplorerStore.getState().openFilteredRelatedContext(
      "orders",
      "customers",
      ["7", "8"],
      "customer_orders",
      "customer_id",
      [{ column: "customer_id", op: "in", value: "7,8" }]
    );
    await useExplorerStore.getState().fetchRows();

    const requestUrl = String(fetchMock.mock.calls.at(0)?.[0]);
    expect(requestUrl).toContain("/api/v1/data/orders?");
    expect(requestUrl).toContain("filter%5Bcustomer_id%5D%5Bin%5D=7%2C8");

    const view = useExplorerStore.getState().views.orders;
    expect(view.status).toBe("ready");
    expect(view.context).toEqual({
      kind: "related",
      parentTable: "customers",
      parentRowIds: ["7", "8"],
      accessor: "customer_orders",
      via: "filter",
      filterColumn: "customer_id"
    });
    expect(view.config.filters).toEqual([
      { column: "customer_id", op: "in", value: "7,8" }
    ]);
  });

  it("captures errors when the fetch fails", async () => {
    seedAuthenticated();

    vi.stubGlobal(
      "fetch",
      vi.fn(async () =>
        new Response(
          JSON.stringify({ error: { code: "NOT_FOUND", message: "Table not found." } }),
          { status: 404, headers: { "Content-Type": "application/json" } }
        )
      )
    );

    useExplorerStore.getState().setActiveTable("nonexistent");
    await useExplorerStore.getState().fetchRows();

    const view = useExplorerStore.getState().views.nonexistent;
    expect(view.status).toBe("error");
    expect(view.error).toBe("Table not found.");
  });

  it("setPage updates the page without resetting other query fields", async () => {
    seedAuthenticated();

    const response = mockListResponse([], 0, 2, 25);
    vi.stubGlobal(
      "fetch",
      vi.fn(async () =>
        new Response(JSON.stringify(response), {
          status: 200,
          headers: { "Content-Type": "application/json" }
        })
      )
    );

    useExplorerStore.getState().setActiveTable("orders");
    // Set some sort first
    useExplorerStore.setState((s) => ({
      views: {
        ...s.views,
        orders: {
          ...s.views.orders,
          config: {
            ...s.views.orders.config,
            sort: [{ column: "name", dir: "asc" }]
          }
        }
      }
    }));

    useExplorerStore.getState().setPage(2);

    // Wait for async fetch to settle
    await vi.waitFor(() => {
      const view = useExplorerStore.getState().views.orders;
      return view.status === "ready" || view.status === "error";
    });

    const view = useExplorerStore.getState().views.orders;
    expect(view.page).toBe(2);
    expect(view.config.sort).toEqual([{ column: "name", dir: "asc" }]);
  });

  it("setSearch resets page to 1 and fetches", async () => {
    seedAuthenticated();

    const response = mockListResponse([], 0);
    vi.stubGlobal(
      "fetch",
      vi.fn(async () =>
        new Response(JSON.stringify(response), {
          status: 200,
          headers: { "Content-Type": "application/json" }
        })
      )
    );

    useExplorerStore.getState().setActiveTable("orders");
    // Set page to 3 first
    useExplorerStore.setState((s) => ({
      views: {
        ...s.views,
        orders: {
          ...s.views.orders,
          page: 3
        }
      }
    }));

    useExplorerStore.getState().setSearch("hello");

    await vi.waitFor(() => {
      const view = useExplorerStore.getState().views.orders;
      return view.status === "ready" || view.status === "error";
    });

    const view = useExplorerStore.getState().views.orders;
    expect(view.config.search).toBe("hello");
    expect(view.page).toBe(1);
  });

  it("extracts only the durable config for saved views", () => {
    useExplorerStore.getState().setActiveTable("orders");
    useExplorerStore.setState((s) => ({
      views: {
        ...s.views,
        orders: {
          ...s.views.orders,
          status: "ready",
          config: {
            type: "table",
            search: "alpha",
            sort: [{ column: "name", dir: "asc" }],
            filters: [{ column: "status", op: "eq", value: "active" }],
            hiddenColumnNames: ["internal_notes"],
            columnOrder: ["id", "name", "status"]
          },
          page: 4,
          perPage: 100,
          rows: [{ id: 1 }],
          meta: { page: 4, per_page: 100, total: 1, total_pages: 1 },
          error: "boom",
          selectedRowIds: ["1"],
          context: {
            kind: "reference",
            rowId: 1,
            sourceTable: "customers",
            sourceRowId: 7,
            sourceColumn: "customer_id"
          }
        }
      }
    }));

    const config = extractExplorerViewConfig(useExplorerStore.getState().views.orders);
    expect(config).toEqual({
      type: "table",
      search: "alpha",
      sort: [{ column: "name", dir: "asc" }],
      filters: [{ column: "status", op: "eq", value: "active" }],
      hiddenColumnNames: ["internal_notes"],
      columnOrder: ["id", "name", "status"]
    });
  });

  it("hydrates runtime state from saved-view config without leaking runtime fields", () => {
    const hydrated = hydrateTableViewFromConfig({
      type: "kanban",
      search: "alpha",
      sort: [{ column: "created_at", dir: "desc" }],
      filters: [{ column: "status", op: "in", value: "pending,shipped" }],
      hiddenColumnNames: ["internal_notes"],
      columnOrder: ["id", "status", "created_at"],
      kanbanColumn: "status",
      cardFieldNames: ["total", "customer_id"],
      hiddenLaneValues: []
    });

    expect(hydrated.config).toEqual({
      type: "kanban",
      search: "alpha",
      sort: [{ column: "created_at", dir: "desc" }],
      filters: [{ column: "status", op: "in", value: "pending,shipped" }],
      hiddenColumnNames: ["internal_notes"],
      columnOrder: ["id", "status", "created_at"],
      kanbanColumn: "status",
      cardFieldNames: ["total", "customer_id"],
      hiddenLaneValues: []
    });
    expect(hydrated.status).toBe("idle");
    expect(hydrated.page).toBe(1);
    expect(hydrated.perPage).toBe(25);
    expect(hydrated.rows).toEqual([]);
    expect(hydrated.meta).toBeNull();
    expect(hydrated.error).toBeNull();
    expect(hydrated.selectedRowIds).toEqual([]);
    expect(hydrated.context).toBeNull();
    expect(hydrated.source).toEqual({ kind: "table" });
    expect(getExplorerQueryForView(hydrated)).toEqual({
      page: 1,
      perPage: 25,
      search: "alpha",
      sort: [{ column: "created_at", dir: "desc" }],
      filters: [{ column: "status", op: "in", value: "pending,shipped" }]
    });
  });

  it("is reset on logout", () => {
    useExplorerStore.getState().setActiveTable("orders");
    expect(useExplorerStore.getState().activeTable).toBe("orders");

    // Trigger logout hooks
    useAuthStore.getState().logout();

    expect(useExplorerStore.getState().activeTable).toBeNull();
    expect(useExplorerStore.getState().views).toEqual({});
  });
});
