import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import { RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createTestRouter } from "../router";
import { resetAppStoreForTest } from "../stores/appStore";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import { resetExplorerStoreForTest, useExplorerStore } from "../stores/explorerStore";
import { resetSchemaStoreForTest, useSchemaStore } from "../stores/schemaStore";
import type { ModelSnapshot } from "../types/api";

function seedAuthenticated() {
  useAuthStore.setState({
    status: "authenticated",
    user: { id: 1, username: "admin", role: "admin" },
    accessToken: "test-access-token",
    expiresAt: Date.now() + 60_000,
    loginInFlight: false,
    error: null
  });
}

function seedSchema(snapshot: ModelSnapshot) {
  useSchemaStore.setState({
    status: "ready",
    snapshot,
    error: null,
    lastLoadedAt: Date.now(),
    inFlight: null
  });
}

function seedExplorerRows(table: string, rows: Record<string, unknown>[]) {
  useExplorerStore.setState((state) => ({
    activeTable: table,
    views: {
      ...state.views,
      [table]: {
        status: "ready",
        query: {
          page: 1,
          perPage: 25,
          sort: [],
          search: "",
          filters: []
        },
        rows,
        meta: {
          page: 1,
          per_page: 25,
          total: rows.length,
          total_pages: 1
        },
        error: null
      }
    }
  }));
}

const sampleSnapshot: ModelSnapshot = {
  version: 3,
  tables: [
    {
      name: "orders",
      displayName: "Orders",
      description: "Customer orders",
      columns: [
        { name: "id", type: "int64", primaryKey: true },
        { name: "customer_id", type: "relation", references: { table: "customers", column: "id" } },
        { name: "total", type: "double" },
        { name: "status", type: "enum", values: ["pending", "shipped", "delivered"] },
        { name: "created_at", type: "datetime" }
      ]
    },
    {
      name: "customers",
      displayName: "Customers",
      columns: [
        { name: "id", type: "int64", primaryKey: true },
        { name: "name", type: "string" },
        { name: "email", type: "string", unique: true }
      ]
    },
    {
      name: "products",
      columns: [
        { name: "id", type: "int64", primaryKey: true },
        { name: "title", type: "string" },
        { name: "price", type: "double" }
      ]
    }
  ],
  relations: [
    { name: "orders_customer", type: "many-to-one", fromTable: "orders", toTable: "customers" }
  ]
};

beforeEach(() => {
  resetAppStoreForTest();
  resetAuthStoreForTest();
  resetSchemaStoreForTest();
  resetExplorerStoreForTest();
  vi.stubGlobal(
    "fetch",
    vi.fn(() => {
      throw new Error("fetch must be mocked in tests");
    })
  );
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("Data Explorer landing (/data)", () => {
  it("shows a loading spinner while the schema is being fetched", () => {
    seedAuthenticated();
    // Leave schema in idle — the page triggers load(), which hangs since fetch is mocked to throw.
    // Seed status=loading to simulate in-flight.
    useSchemaStore.setState({ status: "loading", inFlight: new Promise(() => {}) });

    const router = createTestRouter(["/data"]);
    render(<RouterProvider router={router} />);

    expect(screen.getByTestId("explorer-loading")).toBeInTheDocument();
    expect(screen.getByText(/loading schema/i)).toBeInTheDocument();
  });

  it("shows the empty state when schema has no tables", () => {
    seedAuthenticated();
    seedSchema({ version: 0, tables: [], relations: [] });

    const router = createTestRouter(["/data"]);
    render(<RouterProvider router={router} />);

    expect(screen.getByText("No tables yet")).toBeInTheDocument();
    expect(screen.getByText(/create a model json schema/i)).toBeInTheDocument();
  });

  it("displays the live table list derived from the schema snapshot", () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);

    const router = createTestRouter(["/data"]);
    render(<RouterProvider router={router} />);

    // All three tables are rendered
    expect(screen.getByText("Orders")).toBeInTheDocument();
    expect(screen.getByText("Customers")).toBeInTheDocument();
    expect(screen.getByText("products")).toBeInTheDocument();

    // Summary chips
    expect(screen.getByText(/3 tables/)).toBeInTheDocument();
    expect(screen.getByText(/11 columns/)).toBeInTheDocument();
    expect(screen.getByText(/1 relation/)).toBeInTheDocument();
    expect(screen.getByText("v3")).toBeInTheDocument();
  });

  it("shows table cards with column counts and reference info", () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);

    const router = createTestRouter(["/data"]);
    render(<RouterProvider router={router} />);

    // Orders has 5 columns and 1 FK reference
    expect(screen.getByText("5 cols")).toBeInTheDocument();
    expect(screen.getByText("1 ref")).toBeInTheDocument();

    // Customers and products both have 3 columns
    const threeColChips = screen.getAllByText("3 cols");
    expect(threeColChips.length).toBe(2);
  });

  it("renders table links that navigate to /data/:tableId", () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);

    const router = createTestRouter(["/data"]);
    render(<RouterProvider router={router} />);

    const ordersLink = screen.getByText("Orders").closest("a");
    expect(ordersLink).toHaveAttribute("href", "/data/orders");

    const customersLink = screen.getByText("Customers").closest("a");
    expect(customersLink).toHaveAttribute("href", "/data/customers");
  });

  it("shows an error state with retry when schema load fails", () => {
    seedAuthenticated();
    useSchemaStore.setState({ status: "error", error: "Network error", inFlight: null });

    const router = createTestRouter(["/data"]);
    render(<RouterProvider router={router} />);

    expect(screen.getByText(/schema unavailable/i)).toBeInTheDocument();
    expect(screen.getByText(/network error/i)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /retry/i })).toBeInTheDocument();
  });
});

describe("Table detail (/data/:tableId)", () => {
  it("shows the column list for a table that exists in the schema", () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);

    const router = createTestRouter(["/data/orders"]);
    render(<RouterProvider router={router} />);

    // Column names visible
    expect(screen.getByText("id")).toBeInTheDocument();
    expect(screen.getByText("customer_id")).toBeInTheDocument();
    expect(screen.getByText("total")).toBeInTheDocument();
    expect(screen.getByText("status")).toBeInTheDocument();
    expect(screen.getByText("created_at")).toBeInTheDocument();

    // Column types
    expect(screen.getByText("int64")).toBeInTheDocument();
    expect(screen.getByText("double")).toBeInTheDocument();
    expect(screen.getByText("datetime")).toBeInTheDocument();

    // Summary chips
    expect(screen.getByText(/5 columns/)).toBeInTheDocument();

    // Back link
    expect(screen.getByText("All tables")).toBeInTheDocument();
  });

  it("shows PK and FK badges on the right columns", () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);

    const router = createTestRouter(["/data/orders"]);
    render(<RouterProvider router={router} />);

    const pkBadges = screen.getAllByText("PK");
    expect(pkBadges.length).toBeGreaterThanOrEqual(1);

    const fkBadges = screen.getAllByText("FK");
    expect(fkBadges.length).toBeGreaterThanOrEqual(1);
  });

  it("renders a real read-only grid when row data is ready", () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedExplorerRows("orders", [
      {
        id: 101,
        customer_id: 7,
        total: 149.5,
        status: "pending",
        created_at: "2026-04-12T08:45:00Z"
      },
      {
        id: 102,
        customer_id: null,
        total: 89,
        status: "shipped",
        created_at: "2026-04-13T10:15:00Z"
      }
    ]);

    const router = createTestRouter(["/data/orders"]);
    render(<RouterProvider router={router} />);

    const table = screen.getByRole("table");
    expect(table).toBeInTheDocument();
    expect(within(table).getByText("#")).toBeInTheDocument();
    expect(within(table).getByText("customer_id")).toBeInTheDocument();
    expect(within(table).getByText("149.5")).toBeInTheDocument();
    expect(within(table).getByText("pending")).toBeInTheDocument();
    expect(within(table).getByText("7")).toBeInTheDocument();
    expect(within(table).getByText("—")).toBeInTheDocument();
    expect(within(table).getByText("101")).toBeInTheDocument();
    expect(within(table).getByText("102")).toBeInTheDocument();
  });

  it("shows a 'table not found' state when the tableId doesn't match any schema table", () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);

    const router = createTestRouter(["/data/nonexistent"]);
    render(<RouterProvider router={router} />);

    expect(screen.getByText("Table not found")).toBeInTheDocument();
    expect(screen.getByText(/no table named "nonexistent"/i)).toBeInTheDocument();
  });

  it("shows the loading state while schema is fetching", () => {
    seedAuthenticated();
    useSchemaStore.setState({ status: "loading", inFlight: new Promise(() => {}) });

    const router = createTestRouter(["/data/orders"]);
    render(<RouterProvider router={router} />);

    expect(screen.getByTestId("explorer-loading")).toBeInTheDocument();
  });
});
