import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { buildQueryString } from "../api/data";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import {
  resetExplorerStoreForTest,
  useExplorerStore
} from "../stores/explorerStore";
import { DEFAULT_QUERY } from "../types/explorer";
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
    expect(state.views.orders.query).toEqual(DEFAULT_QUERY);
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
          query: { ...s.views.orders.query, page: 3 }
        }
      }
    }));

    // Switch away
    store.setActiveTable("products");
    expect(useExplorerStore.getState().activeTable).toBe("products");

    // Switch back — orders retains page 3
    useExplorerStore.getState().setActiveTable("orders");
    expect(useExplorerStore.getState().views.orders.query.page).toBe(3);
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
          query: {
            ...s.views.orders.query,
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
    expect(view.query.page).toBe(2);
    expect(view.query.sort).toEqual([{ column: "name", dir: "asc" }]);
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
          query: { ...s.views.orders.query, page: 3 }
        }
      }
    }));

    useExplorerStore.getState().setSearch("hello");

    await vi.waitFor(() => {
      const view = useExplorerStore.getState().views.orders;
      return view.status === "ready" || view.status === "error";
    });

    const view = useExplorerStore.getState().views.orders;
    expect(view.query.search).toBe("hello");
    expect(view.query.page).toBe(1);
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
