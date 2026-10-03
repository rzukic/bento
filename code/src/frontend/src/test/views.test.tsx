import {
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
import { createTestRouter } from "../router";
import { resetAppStoreForTest } from "../stores/appStore";
import { seedSetupInitializedForTest } from "../features/setup";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import {
  resetExplorerStoreForTest,
  useExplorerStore,
} from "../stores/explorerStore";
import { resetSchemaStoreForTest, useSchemaStore } from "../stores/schemaStore";
import type { ModelSnapshot } from "../types/api";
import { DEFAULT_EXPLORER_VIEW_CONFIG, type ExplorerViewConfig } from "../types/explorer";
import type { SavedView } from "../types/views";
import { KanbanView } from "../features/data-explorer/components/KanbanView";
import { ViewRenderer } from "../features/data-explorer/components/ViewRenderer";

// ── Shared fixtures ─────────────────────────────────────────────────────────

const sampleSnapshot: ModelSnapshot = {
  version: 1,
  tables: [
    {
      name: "orders",
      displayName: "Orders",
      columns: [
        { name: "id", type: "int64", primaryKey: true },
        { name: "customer_id", type: "relation", references: { table: "customers", column: "id" } },
        { name: "total", type: "double" },
        { name: "status", type: "enum", values: ["pending", "shipped", "delivered"] },
        { name: "created_at", type: "datetime" },
      ],
    },
    {
      name: "customers",
      columns: [
        { name: "id", type: "int64", primaryKey: true },
        { name: "name", type: "string" },
      ],
    },
  ],
  relations: [],
};

const ordersTable = sampleSnapshot.tables[0];

const sampleRows = [
  { id: 101, customer_id: 7, total: 149.5, status: "pending", created_at: "2026-04-12T08:45:00Z" },
  { id: 102, customer_id: null, total: 89, status: "shipped", created_at: "2026-04-13T10:15:00Z" },
];

const savedView1: SavedView = {
  id: 10,
  name: "High Value Orders",
  table_name: "orders",
  owner_user_id: 1,
  type: "table",
  config: {
    ...DEFAULT_EXPLORER_VIEW_CONFIG,
    sort: [{ column: "total", dir: "desc" }],
  },
  is_shared: false,
  created_at: "2026-04-14T10:00:00Z",
  updated_at: "2026-04-14T10:00:00Z",
};

const savedView2: SavedView = {
  id: 11,
  name: "Shared Board",
  table_name: "orders",
  owner_user_id: 99, // Different user
  type: "table",
  config: { ...DEFAULT_EXPLORER_VIEW_CONFIG },
  is_shared: true,
  created_at: "2026-04-14T10:00:00Z",
  updated_at: "2026-04-14T10:00:00Z",
};

const savedView3: SavedView = {
  id: 12,
  name: "Customer Focus",
  table_name: "customers",
  owner_user_id: 1,
  type: "table",
  config: {
    ...DEFAULT_EXPLORER_VIEW_CONFIG,
    search: "priority",
  },
  is_shared: false,
  created_at: "2026-04-14T10:00:00Z",
  updated_at: "2026-04-14T10:00:00Z",
};

// ── Helpers ─────────────────────────────────────────────────────────────────

function seedAuthenticated() {
  useAuthStore.setState({
    status: "authenticated",
    user: { id: 1, username: "admin", role: "admin" },
    accessToken: "test-access-token",
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

function seedTableView(
  table: string,
  rows: Record<string, unknown>[],
  options: {
    savedViews?: SavedView[];
    activeViewId?: number | null;
    config?: ExplorerViewConfig;
  } = {},
) {
  const { savedViews = [], activeViewId = null, config = { ...DEFAULT_EXPLORER_VIEW_CONFIG } } = options;
  useExplorerStore.setState((state) => ({
    activeTable: table,
    views: {
      ...state.views,
      [table]: {
        status: "ready",
        config,
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
        savedViews,
        savedViewsStatus: "ready" as const,
        activeViewId,
        autoSave: false,
      },
    },
  }));
}

function seedOrdersView(
  rows: Record<string, unknown>[],
  options: {
    savedViews?: SavedView[];
    activeViewId?: number | null;
    config?: ExplorerViewConfig;
  } = {},
) {
  seedTableView("orders", rows, options);
}

function mockDataResponse(rows: Record<string, unknown>[] = sampleRows) {
  return new Response(
    JSON.stringify({ data: rows, meta: { page: 1, per_page: 25, total: rows.length, total_pages: 1 } }),
    { status: 200, headers: { "Content-Type": "application/json" } },
  );
}

function mockOkResponse(body: unknown) {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { "Content-Type": "application/json" },
  });
}

// ── Setup / Teardown ─────────────────────────────────────────────────────────

beforeEach(() => {
  resetAppStoreForTest();
  resetAuthStoreForTest();
  seedSetupInitializedForTest();
  resetSchemaStoreForTest();
  resetExplorerStoreForTest();
  vi.stubGlobal(
    "fetch",
    vi.fn(() => { throw new Error("fetch must be mocked in tests"); }),
  );
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

// ── View selector bar ────────────────────────────────────────────────────────

describe("View selector bar", () => {
  it("renders 'All Records' as the active view when no saved views exist", () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedOrdersView(sampleRows);

    render(<RouterProvider router={createTestRouter(["/data/orders"])} />);

    expect(
      screen.getByRole("button", { name: /current view: all records/i }),
    ).toBeInTheDocument();
  });

  it("shows saved views in the dropdown when the table has views", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedOrdersView(sampleRows, { savedViews: [savedView1, savedView2] });

    render(<RouterProvider router={createTestRouter(["/data/orders"])} />);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /current view: all records/i }));

    const listbox = screen.getByRole("listbox", { name: /available views/i });
    expect(within(listbox).getByText("High Value Orders")).toBeInTheDocument();
    expect(within(listbox).getByText("Shared Board")).toBeInTheDocument();
  });

  it("marks shared views from other users with the shared indicator", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedOrdersView(sampleRows, { savedViews: [savedView1, savedView2] });

    const { container } = render(<RouterProvider router={createTestRouter(["/data/orders"])} />);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /current view: all records/i }));

    const listbox = screen.getByRole("listbox", { name: /available views/i });

    // savedView2 is from user 99 — should show the shared indicator span
    const sharedOption = within(listbox).getByText("Shared Board").closest("button")!;
    expect(sharedOption.querySelector('[aria-label="Shared view"]')).not.toBeNull();

    // savedView1 is owned by user 1 — no shared indicator
    const ownOption = within(listbox).getByText("High Value Orders").closest("button")!;
    expect(ownOption.querySelector('[aria-label="Shared view"]')).toBeNull();

    // suppress unused variable warning
    void container;
  });

  it("switches to a saved view, hydrates its config, and triggers a fetch", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedOrdersView(sampleRows, { savedViews: [savedView1] });

    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(typeof input === "string" ? input : input.toString());
      if (url.includes("/api/v1/data/orders?")) return mockDataResponse();
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal("fetch", fetchMock);

    render(<RouterProvider router={createTestRouter(["/data/orders"])} />);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /current view: all records/i }));

    // Options inside the listbox have role="option"
    await user.click(screen.getByRole("option", { name: /high value orders/i }));

    await waitFor(() => {
      expect(
        fetchMock.mock.calls.some((c) => String(c[0]).includes("sort=-total")),
      ).toBe(true);
    });

    expect(
      screen.getByRole("button", { name: /current view: high value orders/i }),
    ).toBeInTheDocument();
  });

  it("switching back to 'All Records' resets to the default config", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedOrdersView(sampleRows, {
      savedViews: [savedView1],
      activeViewId: savedView1.id,
      config: { ...savedView1.config },
    });

    const fetchMock = vi.fn(async () => mockDataResponse());
    vi.stubGlobal("fetch", fetchMock);

    render(<RouterProvider router={createTestRouter(["/data/orders"])} />);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /current view: high value orders/i }));
    await user.click(screen.getByRole("option", { name: /^all records$/i }));

    await waitFor(() => {
      expect(
        screen.getByRole("button", { name: /current view: all records/i }),
      ).toBeInTheDocument();
    });

    // Switching to All Records resets sort — the fetch should not include sort=-total
    await waitFor(() => {
      const lastCall = fetchMock.mock.calls.at(-1) as unknown[] | undefined;
      expect(String(lastCall?.[0])).not.toContain("sort=");
    });
  });

  it("saving a new view appends it to the list and makes it active", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    // Seed with a sort so hasViewModifications is true → "Save to new view" button appears
    seedOrdersView(sampleRows, {
      config: {
        ...DEFAULT_EXPLORER_VIEW_CONFIG,
        sort: [{ column: "total", dir: "asc" }],
      },
    });

    const newView: SavedView = {
      id: 42,
      name: "Budget Orders",
      table_name: "orders",
      owner_user_id: 1,
      type: "table",
      config: { ...DEFAULT_EXPLORER_VIEW_CONFIG, sort: [{ column: "total", dir: "asc" }] },
      is_shared: false,
      created_at: "2026-04-16T12:00:00Z",
      updated_at: "2026-04-16T12:00:00Z",
    };

    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(typeof input === "string" ? input : input.toString());
      if (url.includes("/api/v1/views")) return mockOkResponse(newView);
      if (url.includes("/api/v1/data/orders")) return mockDataResponse();
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal("fetch", fetchMock);

    render(<RouterProvider router={createTestRouter(["/data/orders"])} />);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /save to new view/i }));

    const dialog = screen.getByRole("dialog");
    await user.type(within(dialog).getByRole("textbox", { name: /view name/i }), "Budget Orders");
    await user.click(within(dialog).getByRole("button", { name: /create view/i }));

    await waitFor(() => {
      expect(
        screen.getByRole("button", { name: /current view: budget orders/i }),
      ).toBeInTheDocument();
    });
  });

  it("can create a kanban view from the New view dialog when enum columns exist", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedOrdersView(sampleRows, {
      config: {
        ...DEFAULT_EXPLORER_VIEW_CONFIG,
        sort: [{ column: "total", dir: "asc" }],
      },
    });

    const newView: SavedView = {
      id: 43,
      name: "Orders Board",
      table_name: "orders",
      owner_user_id: 1,
      type: "kanban",
      config: {
        ...DEFAULT_EXPLORER_VIEW_CONFIG,
        type: "kanban",
        kanbanColumn: null,
        cardFieldNames: [],
        hiddenLaneValues: [],
      },
      is_shared: false,
      created_at: "2026-04-16T12:00:00Z",
      updated_at: "2026-04-16T12:00:00Z",
    };

    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(typeof input === "string" ? input : input.toString());
      if (url.includes("/api/v1/views")) return mockOkResponse(newView);
      if (url.includes("/api/v1/data/orders")) return mockDataResponse();
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal("fetch", fetchMock);

    render(<RouterProvider router={createTestRouter(["/data/orders"])} />);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /save to new view/i }));

    const dialog = screen.getByRole("dialog");
    await user.type(within(dialog).getByRole("textbox", { name: /view name/i }), "Orders Board");
    await user.click(within(dialog).getByRole("radio", { name: /kanban/i }));
    await user.click(within(dialog).getByRole("button", { name: /create view/i }));

    await waitFor(() => {
      expect(
        screen.getByRole("button", { name: /current view: orders board/i }),
      ).toBeInTheDocument();
    });

    // Since kanbanColumn is null, the kanban config panel should be auto-opened.
    expect(screen.getByText(/kanban view not configured/i)).toBeInTheDocument();
  });

  it("disables kanban in the New view dialog when the table has no enum columns", async () => {
    const noEnumSnapshot: ModelSnapshot = {
      version: 1,
      tables: [
        {
          name: "items",
          columns: [
            { name: "id", type: "int64", primaryKey: true },
            { name: "title", type: "string" },
          ],
        },
      ],
      relations: [],
    };

    seedAuthenticated();
    seedSchema(noEnumSnapshot);
    useExplorerStore.setState((state) => ({
      activeTable: "items",
      views: {
        ...state.views,
        items: {
          status: "ready",
          config: { ...DEFAULT_EXPLORER_VIEW_CONFIG },
          page: 1,
          perPage: 25,
          rows: [{ id: 1, title: "One" }],
          meta: { page: 1, per_page: 25, total: 1, total_pages: 1 },
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

    const fetchMock = vi.fn(async () => mockDataResponse([{ id: 1, title: "One" }]));
    vi.stubGlobal("fetch", fetchMock);

    render(<RouterProvider router={createTestRouter(["/data/items"])} />);

    const user = userEvent.setup();
    // Create modal is reachable via the view dropdown "New view".
    await user.click(screen.getByRole("button", { name: /current view: all records/i }));
    await user.click(screen.getByRole("option", { name: /new view/i }));

    const dialog = screen.getByRole("dialog");
    const kanbanRadio = within(dialog).getByRole("radio", { name: /kanban/i });
    expect(kanbanRadio).toBeDisabled();
  });

  it("deleting the active view falls back to 'All Records'", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedOrdersView(sampleRows, {
      savedViews: [savedView1],
      activeViewId: savedView1.id,
      config: { ...savedView1.config },
    });

    vi.stubGlobal("confirm", vi.fn(() => true));

    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(typeof input === "string" ? input : input.toString());
      if ((init as RequestInit | undefined)?.method === "DELETE") {
        return new Response(null, { status: 204 });
      }
      if (url.includes("/api/v1/data/orders")) return mockDataResponse();
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal("fetch", fetchMock);

    render(<RouterProvider router={createTestRouter(["/data/orders"])} />);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /view settings/i }));

    const dialog = screen.getByRole("dialog");
    await user.click(within(dialog).getByRole("button", { name: /delete view/i }));

    await waitFor(() => {
      expect(
        screen.getByRole("button", { name: /current view: all records/i }),
      ).toBeInTheDocument();
    });
  });

  it("does not show delete/rename actions for views owned by another user", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    const sharedActiveView: SavedView = { ...savedView2, id: 20 };
    seedOrdersView(sampleRows, {
      savedViews: [sharedActiveView],
      activeViewId: sharedActiveView.id,
      config: { ...sharedActiveView.config },
    });

    render(<RouterProvider router={createTestRouter(["/data/orders"])} />);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /view settings/i }));

    const dialog = screen.getByRole("dialog");
    expect(within(dialog).queryByRole("button", { name: /delete view/i })).not.toBeInTheDocument();
    // Owner-only "Save" button is replaced by "Duplicate as my view" for shared views
    expect(within(dialog).queryByRole("button", { name: /^save$/i })).not.toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: /duplicate as my view/i })).toBeInTheDocument();
  });
});

// ── Auto-save ───────────────────────────────────────────────────────────────

describe("Saved view auto-save", () => {
  it("persists the view that scheduled the save even if activeTable changes before the debounce fires", async () => {
    vi.useFakeTimers();
    try {
      seedAuthenticated();
      seedSchema(sampleSnapshot);
      seedOrdersView(sampleRows, { savedViews: [savedView1], activeViewId: savedView1.id });

      // Enable auto-save for the active saved view.
      useExplorerStore.setState((state) => ({
        views: {
          ...state.views,
          orders: { ...state.views.orders!, autoSave: true },
        },
      }));

      const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);
        const method = (init?.method ?? "GET").toUpperCase();

        if (url.includes("/api/v1/data/orders") && method === "GET") {
          return mockDataResponse(sampleRows);
        }

        if (url.includes(`/api/v1/views/${savedView1.id}`) && method === "PUT") {
          return mockOkResponse({
            ...savedView1,
            config: {
              ...savedView1.config,
              search: "urgent",
            },
            updated_at: "2026-04-17T12:00:00Z",
          });
        }

        throw new Error(`Unexpected fetch: ${method} ${url}`);
      });
      vi.stubGlobal("fetch", fetchMock);

      // Trigger a config change that schedules an auto-save.
      useExplorerStore.getState().setSearch("urgent");

      // Simulate quick navigation to another table before the debounce fires.
      useExplorerStore.getState().setActiveTable("customers");

      vi.advanceTimersByTime(500);
      await Promise.resolve();

      expect(
        fetchMock.mock.calls.some(([url, init]) => {
          const u = String(url);
          const m = String((init as RequestInit | undefined)?.method ?? "GET").toUpperCase();
          return u.includes(`/api/v1/views/${savedView1.id}`) && m === "PUT";
        }),
      ).toBe(true);
    } finally {
      vi.useRealTimers();
    }
  });

  it("keeps pending auto-saves isolated per saved-view target", async () => {
    vi.useFakeTimers();
    try {
      seedAuthenticated();
      seedSchema(sampleSnapshot);
      seedOrdersView(sampleRows, { savedViews: [savedView1], activeViewId: savedView1.id });
      seedTableView(
        "customers",
        [{ id: 7, name: "Acme Corp" }],
        { savedViews: [savedView3], activeViewId: savedView3.id },
      );

      useExplorerStore.setState((state) => ({
        activeTable: "orders",
        views: {
          ...state.views,
          orders: { ...state.views.orders!, autoSave: true },
          customers: { ...state.views.customers!, autoSave: true },
        },
      }));

      const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);
        const method = (init?.method ?? "GET").toUpperCase();

        if (url.includes("/api/v1/data/orders") && method === "GET") {
          return mockDataResponse(sampleRows);
        }

        if (url.includes("/api/v1/data/customers") && method === "GET") {
          return mockDataResponse([{ id: 7, name: "Acme Corp" }]);
        }

        if (url.includes(`/api/v1/views/${savedView1.id}`) && method === "PUT") {
          return mockOkResponse({
            ...savedView1,
            config: {
              ...savedView1.config,
              search: "urgent",
            },
            updated_at: "2026-04-17T12:00:00Z",
          });
        }

        if (url.includes(`/api/v1/views/${savedView3.id}`) && method === "PUT") {
          return mockOkResponse({
            ...savedView3,
            config: {
              ...savedView3.config,
              search: "vip",
            },
            updated_at: "2026-04-17T12:00:00Z",
          });
        }

        throw new Error(`Unexpected fetch: ${method} ${url}`);
      });
      vi.stubGlobal("fetch", fetchMock);

      useExplorerStore.getState().setSearch("urgent");
      useExplorerStore.getState().setActiveTable("customers");
      useExplorerStore.getState().setSearch("vip");

      vi.advanceTimersByTime(500);
      await Promise.resolve();

      expect(
        fetchMock.mock.calls.some(([url, init]) => {
          const u = String(url);
          const m = String((init as RequestInit | undefined)?.method ?? "GET").toUpperCase();
          return u.includes(`/api/v1/views/${savedView1.id}`) && m === "PUT";
        }),
      ).toBe(true);
      expect(
        fetchMock.mock.calls.some(([url, init]) => {
          const u = String(url);
          const m = String((init as RequestInit | undefined)?.method ?? "GET").toUpperCase();
          return u.includes(`/api/v1/views/${savedView3.id}`) && m === "PUT";
        }),
      ).toBe(true);
    } finally {
      vi.useRealTimers();
    }
  });

  it("coalesces repeated edits for the same saved-view target into one auto-save", async () => {
    vi.useFakeTimers();
    try {
      seedAuthenticated();
      seedSchema(sampleSnapshot);
      seedOrdersView(sampleRows, { savedViews: [savedView1], activeViewId: savedView1.id });

      useExplorerStore.setState((state) => ({
        views: {
          ...state.views,
          orders: { ...state.views.orders!, autoSave: true },
        },
      }));

      const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);
        const method = (init?.method ?? "GET").toUpperCase();

        if (url.includes("/api/v1/data/orders") && method === "GET") {
          return mockDataResponse(sampleRows);
        }

        if (url.includes(`/api/v1/views/${savedView1.id}`) && method === "PUT") {
          return mockOkResponse({
            ...savedView1,
            config: {
              ...savedView1.config,
              search: "urgent",
              filters: [{ column: "status", op: "eq", value: "pending" }],
            },
            updated_at: "2026-04-17T12:00:00Z",
          });
        }

        throw new Error(`Unexpected fetch: ${method} ${url}`);
      });
      vi.stubGlobal("fetch", fetchMock);

      useExplorerStore.getState().setSearch("urgent");
      useExplorerStore.getState().setFilters([{ column: "status", op: "eq", value: "pending" }]);

      vi.advanceTimersByTime(500);
      await Promise.resolve();

      expect(
        fetchMock.mock.calls.filter(([url, init]) => {
          const u = String(url);
          const m = String((init as RequestInit | undefined)?.method ?? "GET").toUpperCase();
          return u.includes(`/api/v1/views/${savedView1.id}`) && m === "PUT";
        }),
      ).toHaveLength(1);
    } finally {
      vi.useRealTimers();
    }
  });

  it("does not auto-save after switching away from a saved view before the debounce fires", async () => {
    vi.useFakeTimers();
    try {
      seedAuthenticated();
      seedSchema(sampleSnapshot);
      seedOrdersView(sampleRows, { savedViews: [savedView1], activeViewId: savedView1.id });

      useExplorerStore.setState((state) => ({
        views: {
          ...state.views,
          orders: { ...state.views.orders!, autoSave: true },
        },
      }));

      const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);
        const method = (init?.method ?? "GET").toUpperCase();

        if (url.includes("/api/v1/data/orders") && method === "GET") {
          return mockDataResponse(sampleRows);
        }

        if (url.includes(`/api/v1/views/${savedView1.id}`) && method === "PUT") {
          return mockOkResponse(savedView1);
        }

        throw new Error(`Unexpected fetch: ${method} ${url}`);
      });
      vi.stubGlobal("fetch", fetchMock);

      useExplorerStore.getState().setSearch("urgent");

      // Switching views cancels any pending auto-save.
      useExplorerStore.getState().switchView(null);

      vi.advanceTimersByTime(500);
      await Promise.resolve();

      expect(
        fetchMock.mock.calls.some(([url, init]) => {
          const u = String(url);
          const m = String((init as RequestInit | undefined)?.method ?? "GET").toUpperCase();
          return u.includes(`/api/v1/views/${savedView1.id}`) && m === "PUT";
        }),
      ).toBe(false);
    } finally {
      vi.useRealTimers();
    }
  });

  it("does not auto-save a view owned by another user when the debounce fires", async () => {
    vi.useFakeTimers();
    try {
      seedAuthenticated();
      seedSchema(sampleSnapshot);
      // savedView2 is owned by user 99; the authenticated user is user 1.
      seedOrdersView(sampleRows, { savedViews: [savedView2], activeViewId: savedView2.id });

      // Force-enable auto-save bypassing the setAutoSave guard, to simulate
      // ownership transferring (or any drift) while a flag remained true.
      useExplorerStore.setState((state) => ({
        views: {
          ...state.views,
          orders: { ...state.views.orders!, autoSave: true },
        },
      }));

      const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);
        const method = (init?.method ?? "GET").toUpperCase();

        if (url.includes("/api/v1/data/orders") && method === "GET") {
          return mockDataResponse(sampleRows);
        }

        if (url.includes(`/api/v1/views/${savedView2.id}`) && method === "PUT") {
          return mockOkResponse(savedView2);
        }

        throw new Error(`Unexpected fetch: ${method} ${url}`);
      });
      vi.stubGlobal("fetch", fetchMock);

      useExplorerStore.getState().setSearch("urgent");

      vi.advanceTimersByTime(500);
      await Promise.resolve();

      expect(
        fetchMock.mock.calls.some(([url, init]) => {
          const u = String(url);
          const m = String((init as RequestInit | undefined)?.method ?? "GET").toUpperCase();
          return u.includes(`/api/v1/views/${savedView2.id}`) && m === "PUT";
        }),
      ).toBe(false);
    } finally {
      vi.useRealTimers();
    }
  });
});

// ── ViewRenderer plugin architecture ────────────────────────────────────────

describe("ViewRenderer", () => {
  it("renders the read-only data grid for type 'table'", () => {
    render(
      <ViewRenderer
        type="table"
        table={ordersTable}
        rows={sampleRows}
        page={1}
        perPage={25}
      />,
    );

    expect(screen.getByRole("table")).toBeInTheDocument();
  });

  it("falls back to the table grid and logs a warning for an unknown view type", () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});

    render(
      <ViewRenderer
        type="timeline"
        table={ordersTable}
        rows={sampleRows}
        page={1}
        perPage={25}
      />,
    );

    expect(screen.getByRole("table")).toBeInTheDocument();
    expect(warn).toHaveBeenCalledWith(
      expect.stringContaining('[ViewRenderer] Unknown view type "timeline"'),
    );
  });
});

// ── Kanban board ─────────────────────────────────────────────────────────────

describe("KanbanView", () => {
  it("renders one lane per enum value in schema-declared order", () => {
    render(
      <KanbanView
        table={ordersTable}
        rows={sampleRows}
        kanbanColumn="status"
        cardFieldNames={[]}
      />,
    );

    const lanes = screen.getAllByRole("listitem");
    // 3 declared enum values: pending, shipped, delivered
    // Both rows fit into declared lanes so no Unassigned lane
    expect(lanes).toHaveLength(3);

    const laneLabels = lanes.map((lane) =>
      lane.querySelector(".kanban-lane__label")?.textContent,
    );
    expect(laneLabels).toEqual(["pending", "shipped", "delivered"]);
  });

  it("shows card count badges matching the loaded row set per lane", () => {
    render(
      <KanbanView
        table={ordersTable}
        rows={sampleRows}
        kanbanColumn="status"
        cardFieldNames={[]}
      />,
    );

    const lanes = screen.getAllByRole("listitem");
    const pendingLane = lanes.find((l) =>
      l.querySelector(".kanban-lane__label")?.textContent === "pending",
    )!;
    const shippedLane = lanes.find((l) =>
      l.querySelector(".kanban-lane__label")?.textContent === "shipped",
    )!;
    const deliveredLane = lanes.find((l) =>
      l.querySelector(".kanban-lane__label")?.textContent === "delivered",
    )!;

    expect(pendingLane.querySelector(".kanban-lane__count")?.textContent).toBe("1");
    expect(shippedLane.querySelector(".kanban-lane__count")?.textContent).toBe("1");
    expect(deliveredLane.querySelector(".kanban-lane__count")?.textContent).toBe("0");
  });

  it("resolves FK card labels from the referenced table display column", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);

    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === "string" ? input : input.toString();

      if (
        url.includes("/api/v1/data/customers?") &&
        url.includes("filter%5Bid%5D%5Bin%5D=7")
      ) {
        return mockOkResponse({
          data: [{ id: 7, name: "Acme Co" }],
          meta: { page: 1, per_page: 1, total: 1, total_pages: 1 },
        });
      }

      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal("fetch", fetchMock);

    const onNavigateReference = vi.fn();
    render(
      <KanbanView
        table={ordersTable}
        rows={sampleRows}
        kanbanColumn="status"
        cardFieldNames={["customer_id", "total"]}
        onNavigateReference={onNavigateReference}
      />,
    );

    await waitFor(() => {
      expect(
        screen.getByRole("button", { name: /open related acme co/i }),
      ).toBeInTheDocument();
    });

    expect(screen.getByText("Acme Co")).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("shows the unconfigured state when kanbanColumn is null", () => {
    render(
      <KanbanView
        table={ordersTable}
        rows={sampleRows}
        kanbanColumn={null}
        cardFieldNames={[]}
      />,
    );

    expect(screen.getByText(/kanban view not configured/i)).toBeInTheDocument();
  });

  it("shows the empty state when no rows match", () => {
    render(
      <KanbanView
        table={ordersTable}
        rows={[]}
        kanbanColumn="status"
        cardFieldNames={[]}
      />,
    );

    expect(screen.getByText(/no records match/i)).toBeInTheDocument();
  });

  it("calls PATCH when a card is dragged to a different lane and updates optimistically", async () => {
    seedAuthenticated();

    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(typeof input === "string" ? input : input.toString());
      if ((init as RequestInit | undefined)?.method === "PATCH" && url.includes("/api/v1/data/orders/101")) {
        return new Response(JSON.stringify({ id: 101, status: "shipped" }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      }
      throw new Error(`Unexpected fetch: ${url} ${(init as RequestInit | undefined)?.method}`);
    });
    vi.stubGlobal("fetch", fetchMock);

    const { container } = render(
      <KanbanView
        table={ordersTable}
        rows={sampleRows}
        kanbanColumn="status"
        cardFieldNames={["total"]}
      />,
    );

    const lanes = screen.getAllByRole("listitem");
    const pendingLane = lanes.find((l) =>
      l.querySelector(".kanban-lane__label")?.textContent === "pending",
    )!;
    const shippedLane = lanes.find((l) =>
      l.querySelector(".kanban-lane__label")?.textContent === "shipped",
    )!;

    // The drag handle is a <span draggable> with aria-label — find via CSS selector
    const dragHandle = pendingLane.querySelector(".kanban-card__drag-handle")!;
    expect(dragHandle).not.toBeNull();

    fireEvent.dragStart(dragHandle);
    fireEvent.dragOver(shippedLane);
    fireEvent.drop(shippedLane);

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining("/api/v1/data/orders/101"),
        expect.objectContaining({ method: "PATCH" }),
      );
    });

    // Optimistic update: the card now appears in the shipped lane
    await waitFor(() => {
      expect(within(shippedLane).queryAllByRole("article")).toHaveLength(2);
    });

    void container;
  });

  it("snaps the card back on PATCH failure and shows an error banner", async () => {
    seedAuthenticated();

    const fetchMock = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      if ((init as RequestInit | undefined)?.method === "PATCH") {
        return new Response(
          JSON.stringify({ error: { code: "SERVER_ERROR", message: "Save failed" } }),
          { status: 500, headers: { "Content-Type": "application/json" } },
        );
      }
      throw new Error("Unexpected fetch");
    });
    vi.stubGlobal("fetch", fetchMock);

    render(
      <KanbanView
        table={ordersTable}
        rows={sampleRows}
        kanbanColumn="status"
        cardFieldNames={["total"]}
      />,
    );

    const lanes = screen.getAllByRole("listitem");
    const pendingLane = lanes.find((l) =>
      l.querySelector(".kanban-lane__label")?.textContent === "pending",
    )!;
    const shippedLane = lanes.find((l) =>
      l.querySelector(".kanban-lane__label")?.textContent === "shipped",
    )!;

    const dragHandle = pendingLane.querySelector(".kanban-card__drag-handle")!;
    fireEvent.dragStart(dragHandle);
    fireEvent.dragOver(shippedLane);
    fireEvent.drop(shippedLane);

    await waitFor(() => {
      expect(screen.getByRole("alert")).toBeInTheDocument();
    });

    // After rollback, the pending lane still has its card
    expect(within(pendingLane).queryAllByRole("article").length).toBeGreaterThan(0);
  });
});
