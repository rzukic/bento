import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createTestRouter } from "../router";
import { resetAppStoreForTest } from "../stores/appStore";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import { resetExplorerStoreForTest, useExplorerStore } from "../stores/explorerStore";
import { resetSchemaStoreForTest, useSchemaStore } from "../stores/schemaStore";
import type { ModelSnapshot } from "../types/api";
import { DEFAULT_EXPLORER_VIEW_CONFIG } from "../types/explorer";

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
        config: { ...DEFAULT_EXPLORER_VIEW_CONFIG },
        page: 1,
        perPage: 25,
        rows,
        meta: {
          page: 1,
          per_page: 25,
          total: rows.length,
          total_pages: 1
        },
        error: null,
        source: { kind: "table" },
        selectedRowIds: [],
        context: null
      }
    }
  }));
}

function mockListResponse(rows: Record<string, unknown>[], total: number, page = 1, perPage = 25) {
  return {
    data: rows,
    meta: {
      page,
      per_page: perPage,
      total,
      total_pages: Math.max(1, Math.ceil(total / perPage))
    }
  };
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
    {
      name: "customer_orders",
      type: "1:N",
      from: "customers",
      to: "orders",
      column: "customer_id",
      resolvedFromAccessor: "customer_orders",
      resolvedToAccessor: "customer"
    }
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
  it("shows a neutral loading spinner during initial fetch and never shows scaffold placeholder copy", () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    // Seed the view in loading state with no rows — simulates the first-load transition
    // before the fetch response arrives. This is the state that previously showed the
    // "Data grid coming soon / TASK-069 will add the read-only data grid here" placeholder.
    useExplorerStore.setState((state) => ({
      activeTable: "orders",
      views: {
        ...state.views,
        orders: {
          status: "loading",
          config: { ...DEFAULT_EXPLORER_VIEW_CONFIG },
          page: 1,
          perPage: 25,
          rows: [],
          meta: null,
          error: null,
          source: { kind: "table" },
          selectedRowIds: [],
          context: null,
        },
      },
    }));

    const router = createTestRouter(["/data/orders"]);
    render(<RouterProvider router={router} />);

    expect(screen.getByTestId("data-loading")).toBeInTheDocument();
    expect(screen.getByText(/loading rows/i)).toBeInTheDocument();
    expect(screen.queryByText(/data grid coming soon/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/task-069/i)).not.toBeInTheDocument();
  });

  it("shows the table metadata without the old columns side panel", () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);

    const router = createTestRouter(["/data/orders"]);
    const { container } = render(<RouterProvider router={router} />);

    expect(container.querySelector(".column-list")).toBeNull();
    expect(screen.getByText("Orders")).toBeInTheDocument();
    expect(screen.getByText("Customers")).toBeInTheDocument();
  });

  it("renders schema-driven table tabs instead of the old back link and summary strip", () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);

    const router = createTestRouter(["/data/orders"]);
    render(<RouterProvider router={router} />);

    const ordersTab = screen.getByRole("link", { name: /^orders$/i });
    const customersTab = screen.getByRole("link", { name: /^customers$/i });
    expect(ordersTab).toHaveAttribute("href", "/data/orders");
    expect(customersTab).toHaveAttribute("href", "/data/customers");
    expect(screen.queryByText("All tables")).not.toBeInTheDocument();
    expect(screen.queryByText(/5 columns/i)).not.toBeInTheDocument();
  });

  it("hides the large shell page header on per-table views", () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);

    const router = createTestRouter(["/data/orders"]);
    render(<RouterProvider router={router} />);

    expect(screen.queryByRole("heading", { name: "Orders" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /view logs/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^new$/i })).not.toBeInTheDocument();
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
    expect(within(table).getByText("customer_id")).toBeInTheDocument();
    expect(within(table).getByText("149.5")).toBeInTheDocument();
    expect(within(table).getByText("pending")).toBeInTheDocument();
    expect(within(table).getByText("7")).toBeInTheDocument();
    expect(within(table).getByText("—")).toBeInTheDocument();
    expect(within(table).getAllByText("101").length).toBeGreaterThan(0);
    expect(within(table).getAllByText("102").length).toBeGreaterThan(0);
  });

  it("lets columns be hidden from a dedicated columns foldout while keeping the pk locked visible", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedExplorerRows("customers", [
      {
        id: 1,
        name: "Customer 1",
        email: "customer1@example.com"
      }
    ]);

    const router = createTestRouter(["/data/customers"]);
    render(<RouterProvider router={router} />);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /^columns$/i }));

    const pkCheckbox = screen.getByRole("checkbox", { name: /^id/i });
    expect(pkCheckbox).toBeDisabled();

    const emailCheckbox = screen.getByRole("checkbox", { name: /^email$/i });
    expect(emailCheckbox).toBeChecked();

    await user.click(emailCheckbox);

    const table = screen.getByRole("table");
    expect(within(table).queryByText("email")).not.toBeInTheDocument();
    expect(within(table).queryByText("customer1@example.com")).not.toBeInTheDocument();
    expect(within(table).getByText("Customer 1")).toBeInTheDocument();
  });

  it("can hide a column directly from the column header context menu", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedExplorerRows("customers", [
      {
        id: 1,
        name: "Customer 1",
        email: "customer1@example.com"
      }
    ]);

    const router = createTestRouter(["/data/customers"]);
    render(<RouterProvider router={router} />);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /column actions for email/i }));
    await user.click(screen.getByRole("menuitem", { name: /hide column/i }));

    const table = screen.getByRole("table");
    expect(within(table).queryByText("email")).not.toBeInTheDocument();
    expect(within(table).queryByText("customer1@example.com")).not.toBeInTheDocument();
  });

  it("navigates a reference cell into the referenced row context", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedExplorerRows("orders", [
      {
        id: 101,
        customer_id: 7,
        total: 149.5,
        status: "pending",
        created_at: "2026-04-12T08:45:00Z"
      }
    ]);

    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === "string" ? input : input.toString();

      if (url.includes("/api/v1/data/customers?")) {
        return new Response(
          JSON.stringify(
            mockListResponse(
              [
                {
                  id: 7,
                  name: "Customer 7",
                  email: "customer7@example.com"
                }
              ],
              1
            )
          ),
          {
            status: 200,
            headers: { "Content-Type": "application/json" }
          }
        );
      }

      throw new Error(`Unexpected fetch: ${url}`);
    });

    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/data/orders"]);
    render(<RouterProvider router={router} />);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /open related 7/i }));

    await waitFor(() => {
      expect(String(fetchMock.mock.calls.at(-1)?.[0])).toContain("/api/v1/data/customers?");
      expect(String(fetchMock.mock.calls.at(-1)?.[0])).toContain("filter%5Bid%5D=7");
    });

    expect(await screen.findByText(/focused record/i)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /^filter/i })).toHaveAttribute("aria-expanded", "false");
    expect(screen.getByText(/customers row #7/i)).toBeInTheDocument();
    expect(screen.getByText(/opened from orders #101 via customer_id/i)).toBeInTheDocument();
  });

  it("opens related records from the current selection", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    useExplorerStore.getState().openRecordContext("customers", 7, {
      sourceTable: "orders",
      sourceRowId: 101,
      sourceColumn: "customer_id"
    });

    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === "string" ? input : input.toString();

      if (url.includes("/api/v1/data/customers?")) {
        return new Response(
          JSON.stringify(
            mockListResponse(
              [
                {
                  id: 7,
                  name: "Customer 7",
                  email: "customer7@example.com"
                }
              ],
              1
            )
          ),
          {
            status: 200,
            headers: { "Content-Type": "application/json" }
          }
        );
      }

      if (url.includes("/api/v1/data/orders?")) {
        return new Response(
          JSON.stringify(
            mockListResponse(
              [
                {
                  id: 101,
                  customer_id: 7,
                  total: 149.5,
                  status: "pending",
                  created_at: "2026-04-12T08:45:00Z"
                }
              ],
              1
            )
          ),
          {
            status: 200,
            headers: { "Content-Type": "application/json" }
          }
        );
      }

      throw new Error(`Unexpected fetch: ${url}`);
    });

    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/data/customers"]);
    render(<RouterProvider router={router} />);

    await screen.findByText(/focused record/i);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /select row 7/i }));
    await user.click(screen.getByRole("button", { name: /selection operations/i }));
    await user.click(screen.getByRole("button", { name: /^orders$/i }));

    await waitFor(() => {
      expect(String(fetchMock.mock.calls.at(-1)?.[0])).toContain("/api/v1/data/orders?");
      expect(String(fetchMock.mock.calls.at(-1)?.[0])).toContain("filter%5Bcustomer_id%5D=7");
    });

    expect(await screen.findByText(/related records/i)).toBeInTheDocument();
    expect(screen.getByText(/orders related to customers row 7/i)).toBeInTheDocument();
    expect(
      screen.getByText(/using the normal filter flow on customer_id via customer_orders/i)
    ).toBeInTheDocument();
  });

  it("uses the resolved relation accessor when opening related records", async () => {
    seedAuthenticated();
    seedSchema({
      ...sampleSnapshot,
      relations: sampleSnapshot.relations?.map((relation) =>
        relation.name === "customer_orders"
          ? {
              ...relation,
              resolvedFromAccessor: "vip_orders"
            }
          : relation
      )
    });
    useExplorerStore.getState().openRecordContext("customers", 7, {
      sourceTable: "orders",
      sourceRowId: 101,
      sourceColumn: "customer_id"
    });

    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === "string" ? input : input.toString();

      if (url.includes("/api/v1/data/customers?")) {
        return new Response(
          JSON.stringify(
            mockListResponse(
              [
                {
                  id: 7,
                  name: "Customer 7",
                  email: "customer7@example.com"
                }
              ],
              1
            )
          ),
          {
            status: 200,
            headers: { "Content-Type": "application/json" }
          }
        );
      }

      if (url.includes("/api/v1/data/orders?")) {
        return new Response(
          JSON.stringify(
            mockListResponse(
              [
                {
                  id: 101,
                  customer_id: 7,
                  total: 149.5,
                  status: "pending",
                  created_at: "2026-04-12T08:45:00Z"
                }
              ],
              1
            )
          ),
          {
            status: 200,
            headers: { "Content-Type": "application/json" }
          }
        );
      }

      throw new Error(`Unexpected fetch: ${url}`);
    });

    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/data/customers"]);
    render(<RouterProvider router={router} />);

    await screen.findByText(/focused record/i);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /select row 7/i }));
    await user.click(screen.getByRole("button", { name: /selection operations/i }));
    await user.click(screen.getByRole("button", { name: /^orders$/i }));

    await waitFor(() => {
      expect(String(fetchMock.mock.calls.at(-1)?.[0])).toContain("/api/v1/data/orders?");
      expect(String(fetchMock.mock.calls.at(-1)?.[0])).toContain("filter%5Bcustomer_id%5D=7");
    });

    expect(await screen.findByText(/using the normal filter flow on customer_id via vip_orders/i)).toBeInTheDocument();
  });

  it("surfaces many-to-many relation actions from schema relation metadata", async () => {
    seedAuthenticated();
    seedSchema({
      version: 1,
      tables: [
        {
          name: "products",
          displayName: "Products",
          columns: [
            { name: "id", type: "int64", primaryKey: true },
            { name: "title", type: "string" }
          ]
        },
        {
          name: "tags",
          displayName: "Tags",
          columns: [
            { name: "id", type: "int64", primaryKey: true },
            { name: "label", type: "string" }
          ]
        }
      ],
      relations: [
        {
          name: "product_tags",
          type: "N:M",
          from: "products",
          to: "tags",
          resolvedFromAccessor: "tags",
          resolvedToAccessor: "products",
          resolvedJunctionTableName: "_jt_products_tags"
        }
      ]
    });
    seedExplorerRows("products", [
      {
        id: 1,
        title: "Notebook"
      }
    ]);

    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === "string" ? input : input.toString();

      if (url.includes("/api/v1/data/products/1/tags?")) {
        return new Response(
          JSON.stringify(
            mockListResponse(
              [
                { id: 10, label: "office" },
                { id: 11, label: "paper" }
              ],
              2
            )
          ),
          {
            status: 200,
            headers: { "Content-Type": "application/json" }
          }
        );
      }

      throw new Error(`Unexpected fetch: ${url}`);
    });

    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/data/products"]);
    render(<RouterProvider router={router} />);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /select row 1/i }));
    await user.click(screen.getByRole("button", { name: /selection operations/i }));
    await user.click(screen.getByRole("button", { name: /^tags$/i }));

    await waitFor(() => {
      expect(String(fetchMock.mock.calls.at(-1)?.[0])).toContain("/api/v1/data/products/1/tags?");
    });

    expect(await screen.findByText(/tags related to products row 1/i)).toBeInTheDocument();
    expect(screen.getByText(/using relation accessor tags through the backend relation endpoint/i)).toBeInTheDocument();
  });

  it("uses a normal one-of filter when opening related records from multiple selected rows", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedExplorerRows("customers", [
      {
        id: 1,
        name: "Customer 1",
        email: "customer1@example.com"
      },
      {
        id: 2,
        name: "Customer 2",
        email: "customer2@example.com"
      }
    ]);

    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === "string" ? input : input.toString();

      if (url.includes("/api/v1/data/orders?")) {
        return new Response(
          JSON.stringify(
            mockListResponse(
              [
                { id: 101, customer_id: 1, total: 149.5, status: "pending", created_at: "2026-04-12T08:45:00Z" },
                { id: 102, customer_id: 2, total: 89, status: "shipped", created_at: "2026-04-13T10:15:00Z" }
              ],
              2
            )
          ),
          {
            status: 200,
            headers: { "Content-Type": "application/json" }
          }
        );
      }

      throw new Error(`Unexpected fetch: ${url}`);
    });

    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/data/customers"]);
    render(<RouterProvider router={router} />);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /select all visible rows/i }));
    await user.click(screen.getByRole("button", { name: /selection operations/i }));
    await user.click(screen.getByRole("button", { name: /^orders$/i }));

    await waitFor(() => {
      expect(String(fetchMock.mock.calls.at(-1)?.[0])).toContain("/api/v1/data/orders?");
      expect(String(fetchMock.mock.calls.at(-1)?.[0])).toContain(
        "filter%5Bcustomer_id%5D%5Bin%5D=1%2C2"
      );
    });

    expect(await screen.findByText(/orders related to customers rows 1, 2/i)).toBeInTheDocument();
    expect(screen.getByText("customer_id one of 1, 2")).toBeInTheDocument();
    expect(
      screen.getByText(/using the normal filter flow on customer_id via customer_orders/i)
    ).toBeInTheDocument();
  });

  it("clears the related-record banner when the query changes", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedExplorerRows("customers", [
      {
        id: 1,
        name: "Customer 1",
        email: "customer1@example.com"
      },
      {
        id: 2,
        name: "Customer 2",
        email: "customer2@example.com"
      }
    ]);

    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === "string" ? input : input.toString();

      if (url.includes("/api/v1/data/orders?")) {
        return new Response(
          JSON.stringify(
            mockListResponse(
              [
                { id: 101, customer_id: 1, total: 149.5, status: "pending", created_at: "2026-04-12T08:45:00Z" },
                { id: 102, customer_id: 2, total: 89, status: "shipped", created_at: "2026-04-13T10:15:00Z" }
              ],
              2
            )
          ),
          {
            status: 200,
            headers: { "Content-Type": "application/json" }
          }
        );
      }

      throw new Error(`Unexpected fetch: ${url}`);
    });

    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/data/customers"]);
    render(<RouterProvider router={router} />);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /select all visible rows/i }));
    await user.click(screen.getByRole("button", { name: /selection operations/i }));
    await user.click(screen.getByRole("button", { name: /^orders$/i }));

    expect(await screen.findByText(/related records/i)).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /^search$/i }));
    await user.type(screen.getByLabelText(/search rows/i), "pending");

    await waitFor(() => {
      expect(String(fetchMock.mock.calls.at(-1)?.[0])).toContain("search=pending");
    });

    await waitFor(() => {
      expect(screen.queryByText(/related records/i)).not.toBeInTheDocument();
    });
  });

  it("keeps related selection actions for schemas that only expose FK references", async () => {
    seedAuthenticated();
    seedSchema({
      ...sampleSnapshot,
      relations: []
    });
    seedExplorerRows("customers", [
      {
        id: 1,
        name: "Customer 1",
        email: "customer1@example.com"
      }
    ]);

    const router = createTestRouter(["/data/customers"]);
    render(<RouterProvider router={router} />);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /select row 1/i }));

    expect(screen.getByRole("button", { name: /selection operations/i })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /selection operations/i }));
    expect(screen.getByRole("button", { name: /^orders$/i })).toBeInTheDocument();
  });

  it("keeps many-to-many related actions single-row because they need the relation endpoint", async () => {
    seedAuthenticated();
    seedSchema({
      version: 1,
      tables: [
        {
          name: "products",
          displayName: "Products",
          columns: [
            { name: "id", type: "int64", primaryKey: true },
            { name: "title", type: "string" }
          ]
        },
        {
          name: "tags",
          displayName: "Tags",
          columns: [
            { name: "id", type: "int64", primaryKey: true },
            { name: "label", type: "string" }
          ]
        }
      ],
      relations: [
        {
          name: "product_tags",
          type: "N:M",
          from: "products",
          to: "tags",
          resolvedFromAccessor: "tags",
          resolvedToAccessor: "products",
          resolvedJunctionTableName: "_jt_products_tags"
        }
      ]
    });
    seedExplorerRows("products", [
      { id: 1, title: "Notebook" },
      { id: 2, title: "Marker" }
    ]);

    const router = createTestRouter(["/data/products"]);
    render(<RouterProvider router={router} />);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /select all visible rows/i }));
    await user.click(screen.getByRole("button", { name: /selection operations/i }));

    expect(screen.getByRole("button", { name: /^tags$/i })).toBeDisabled();
  });

  it("selects rows, shows selection operations, and clears selection when filters change", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);

    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === "string" ? input : input.toString();

      return new Response(
        JSON.stringify(
          mockListResponse(
            [
              {
                id: 1,
                name: "Customer 1",
                email: "customer1@example.com"
              },
              {
                id: 2,
                name: "Customer 2",
                email: "customer2@example.com"
              }
            ],
            2
          )
        ),
        {
          status: 200,
          headers: { "Content-Type": "application/json" }
        }
      );
    });

    vi.stubGlobal("fetch", fetchMock);
    seedExplorerRows("customers", [
      {
        id: 1,
        name: "Customer 1",
        email: "customer1@example.com"
      },
      {
        id: 2,
        name: "Customer 2",
        email: "customer2@example.com"
      }
    ]);

    const router = createTestRouter(["/data/customers"]);
    render(<RouterProvider router={router} />);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /select row 1/i }));
    expect(screen.getByRole("button", { name: /selection operations/i })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /^filter/i }));
    await user.selectOptions(screen.getByRole("combobox", { name: /filter column/i }), "name");
    await user.selectOptions(screen.getByRole("combobox", { name: /operator/i }), "like");
    await user.type(screen.getByRole("textbox", { name: /value/i }), "Customer");
    await user.click(screen.getByRole("button", { name: /add filter/i }));

    await waitFor(() => {
      expect(String(fetchMock.mock.calls.at(-1)?.[0])).toContain("filter%5Bname%5D%5Blike%5D=Customer");
    });

    expect(screen.queryByRole("button", { name: /selection operations/i })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: /select row 1/i })).toBeInTheDocument();
  });

  it("shows the same selection actions from the row context menu", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedExplorerRows("customers", [
      {
        id: 1,
        name: "Customer 1",
        email: "customer1@example.com"
      }
    ]);

    const router = createTestRouter(["/data/customers"]);
    const { container } = render(<RouterProvider router={router} />);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /select row 1/i }));

    const selectedRow = container.querySelector(".data-grid__row--selected");
    expect(selectedRow).not.toBeNull();

    if (!selectedRow) {
      throw new Error("Expected a selected row");
    }

    await user.pointer([
      {
        keys: "[MouseRight]",
        target: selectedRow,
      },
    ]);

    expect(screen.getByRole("menuitem", { name: /clear selection/i })).toBeInTheDocument();
    expect(screen.getByRole("menuitem", { name: /^orders$/i })).toBeInTheDocument();
  });

  it("clears a partial selection with escape", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedExplorerRows("customers", [
      {
        id: 1,
        name: "Customer 1",
        email: "customer1@example.com"
      },
      {
        id: 2,
        name: "Customer 2",
        email: "customer2@example.com"
      }
    ]);

    const router = createTestRouter(["/data/customers"]);
    render(<RouterProvider router={router} />);

    const user = userEvent.setup();
    await user.click(screen.getByRole("cell", { name: "1" }));

    await user.keyboard("{Escape}");
    expect(screen.getByRole("button", { name: /select row 1/i })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /selection operations/i })).not.toBeInTheDocument();
  });

  it("selects a row when clicking anywhere in the primary key cell", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedExplorerRows("customers", [
      {
        id: 1,
        name: "Customer 1",
        email: "customer1@example.com"
      }
    ]);

    const router = createTestRouter(["/data/customers"]);
    render(<RouterProvider router={router} />);

    const user = userEvent.setup();
    const primaryCell = screen.getByRole("cell", { name: "1" });
    await user.click(primaryCell);

    expect(screen.getByRole("button", { name: /deselect row 1/i })).toBeInTheDocument();
  });

  it("selects a visible row range when dragging across primary key cells", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedExplorerRows("customers", [
      {
        id: 1,
        name: "Customer 1",
        email: "customer1@example.com"
      },
      {
        id: 2,
        name: "Customer 2",
        email: "customer2@example.com"
      },
      {
        id: 3,
        name: "Customer 3",
        email: "customer3@example.com"
      }
    ]);

    const router = createTestRouter(["/data/customers"]);
    render(<RouterProvider router={router} />);

    const firstPrimaryCell = screen.getByRole("cell", { name: "1" });
    const thirdPrimaryCell = screen.getByRole("cell", { name: "3" });

    fireEvent.mouseDown(firstPrimaryCell, { button: 0, buttons: 1 });
    fireEvent.mouseEnter(thirdPrimaryCell, { buttons: 1 });
    fireEvent.mouseUp(document);

    expect(screen.getByRole("button", { name: /deselect row 1/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /deselect row 2/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /deselect row 3/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /selection operations/i })).toBeInTheDocument();
  });

  it("adds a dragged row range onto an existing selection", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedExplorerRows("customers", [
      {
        id: 1,
        name: "Customer 1",
        email: "customer1@example.com"
      },
      {
        id: 2,
        name: "Customer 2",
        email: "customer2@example.com"
      },
      {
        id: 3,
        name: "Customer 3",
        email: "customer3@example.com"
      },
      {
        id: 4,
        name: "Customer 4",
        email: "customer4@example.com"
      }
    ]);

    const router = createTestRouter(["/data/customers"]);
    render(<RouterProvider router={router} />);

    const user = userEvent.setup();
    await user.click(screen.getByRole("cell", { name: "1" }));

    const thirdPrimaryCell = screen.getByRole("cell", { name: "3" });
    const fourthPrimaryCell = screen.getByRole("cell", { name: "4" });

    fireEvent.mouseDown(thirdPrimaryCell, { button: 0, buttons: 1 });
    fireEvent.mouseEnter(fourthPrimaryCell, { buttons: 1 });
    fireEvent.mouseUp(document);

    expect(screen.getByRole("button", { name: /deselect row 1/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /deselect row 3/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /deselect row 4/i })).toBeInTheDocument();
  });

  it("selects the full range between the anchor row and a shift-clicked row", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedExplorerRows("customers", [
      {
        id: 1,
        name: "Customer 1",
        email: "customer1@example.com"
      },
      {
        id: 2,
        name: "Customer 2",
        email: "customer2@example.com"
      },
      {
        id: 3,
        name: "Customer 3",
        email: "customer3@example.com"
      },
      {
        id: 4,
        name: "Customer 4",
        email: "customer4@example.com"
      }
    ]);

    const router = createTestRouter(["/data/customers"]);
    render(<RouterProvider router={router} />);

    const user = userEvent.setup();
    await user.click(screen.getByRole("cell", { name: "1" }));
    await user.keyboard("[ShiftLeft>]");
    await user.click(screen.getByRole("cell", { name: "4" }));
    await user.keyboard("[/ShiftLeft]");

    expect(screen.getByRole("button", { name: /deselect row 1/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /deselect row 2/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /deselect row 3/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /deselect row 4/i })).toBeInTheDocument();
  });

  it("renders datetime values in a human-readable format for mixed input variants", () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedExplorerRows("orders", [
      {
        id: 201,
        customer_id: 7,
        total: 149.5,
        status: "pending",
        created_at: "2026-04-10 12:00:00"
      },
      {
        id: 202,
        customer_id: 8,
        total: 89,
        status: "shipped",
        created_at: "2026-04-011T10:00:00Z"
      },
      {
        id: 203,
        customer_id: 9,
        total: 42,
        status: "pending",
        created_at: "2026-04-012T10:00:00Z"
      }
    ]);

    const router = createTestRouter(["/data/orders"]);
    render(<RouterProvider router={router} />);

    const table = screen.getByRole("table");
    expect(within(table).queryByText("2026-04-10 12:00:00")).not.toBeInTheDocument();
    expect(within(table).queryByText("2026-04-011T10:00:00Z")).not.toBeInTheDocument();
    expect(within(table).queryByText("2026-04-012T10:00:00Z")).not.toBeInTheDocument();
    expect(within(table).getAllByText(/2026/)).toHaveLength(3);
  });

  it("renders boolean columns as true and false even when values arrive as 1 and 0", () => {
    seedAuthenticated();
    seedSchema({
      version: 1,
      tables: [
        {
          name: "flags",
          columns: [
            { name: "id", type: "int64", primaryKey: true },
            { name: "enabled", type: "boolean" }
          ]
        }
      ],
      relations: []
    });
    seedExplorerRows("flags", [
      { id: 101, enabled: 1 },
      { id: 102, enabled: 0 }
    ]);

    const router = createTestRouter(["/data/flags"]);
    render(<RouterProvider router={router} />);

    const table = screen.getByRole("table");
    expect(within(table).getByText("true")).toBeInTheDocument();
    expect(within(table).getByText("false")).toBeInTheDocument();
    expect(within(table).queryByText(/^0$/)).not.toBeInTheDocument();
  });

  it("shows the first query controls above the grid", () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedExplorerRows("orders", [
      {
        id: 101,
        customer_id: 7,
        total: 149.5,
        status: "pending",
        created_at: "2026-04-12T08:45:00Z"
      }
    ]);

    const router = createTestRouter(["/data/orders"]);
    render(<RouterProvider router={router} />);

    expect(screen.getByRole("button", { name: /^search$/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /^sort$/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /^filter/i })).toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: /rows per page/i })).toBeInTheDocument();
    expect(screen.getByText(/page 1 of 1/i)).toBeInTheDocument();
    expect(screen.getByText(/showing 1-1 of 1/i)).toBeInTheDocument();
    expect(screen.getByText(/1 row/i)).toBeInTheDocument();
  });

  it("opens the filter panel for a column from the header context menu", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedExplorerRows("orders", [
      {
        id: 101,
        customer_id: 7,
        total: 149.5,
        status: "pending",
        created_at: "2026-04-12T08:45:00Z"
      }
    ]);

    const router = createTestRouter(["/data/orders"]);
    render(<RouterProvider router={router} />);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /column actions for status/i }));
    await user.click(screen.getByRole("menuitem", { name: /filter column/i }));

    expect(screen.getByRole("button", { name: /^filter/i })).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByRole("combobox", { name: /filter column/i })).toHaveValue("status");
  });

  it("adds a filter when enter is pressed in the filter value field", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);

    const fetchMock = vi.fn(async () => {
      return new Response(
        JSON.stringify(
          mockListResponse(
            [
              {
                id: 101,
                customer_id: 7,
                total: 149.5,
                status: "pending",
                created_at: "2026-04-12T08:45:00Z"
              }
            ],
            1
          )
        ),
        {
          status: 200,
          headers: { "Content-Type": "application/json" }
        }
      );
    });

    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/data/orders"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("table");

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /^filter/i }));
    await user.selectOptions(screen.getByRole("combobox", { name: /filter column/i }), "total");
    await user.selectOptions(screen.getByRole("combobox", { name: /operator/i }), "gte");
    await user.type(screen.getByLabelText(/^value$/i), "100{enter}");

    await waitFor(() => {
      const lastCall = fetchMock.mock.calls.at(-1) as unknown[] | undefined;
      if (!lastCall) {
        throw new Error("Expected filter request to be issued");
      }
      const requestUrl = String(lastCall[0]);
      expect(requestUrl).toContain("filter%5Btotal%5D%5Bgte%5D=100");
    });
    expect(screen.getByText("total >= 100")).toBeInTheDocument();
  });

  it("adapts filter controls to enum and datetime columns", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedExplorerRows("orders", [
      {
        id: 101,
        customer_id: 7,
        total: 149.5,
        status: "pending",
        created_at: "2026-04-12T08:45:00Z"
      }
    ]);

    const router = createTestRouter(["/data/orders"]);
    render(<RouterProvider router={router} />);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /^filter/i }));

    await user.selectOptions(screen.getByRole("combobox", { name: /filter column/i }), "status");

    const enumValueSelect = screen.getByRole("button", { name: /select value/i });
    expect(enumValueSelect).toBeInTheDocument();
    expect(screen.queryByRole("textbox", { name: /^value$/i })).not.toBeInTheDocument();

    const operatorSelect = screen.getByRole("combobox", { name: /operator/i });
    const operatorLabels = within(operatorSelect).getAllByRole("option").map((option) => option.textContent);
    expect(operatorLabels).toEqual([
      "Equals (=)",
      "Does not equal (!=)",
      "One of (in)",
      "Is empty (null)",
      "Is not empty (!null)"
    ]);

    await user.selectOptions(operatorSelect, "in");
    const enumMultiTrigger = screen.getByRole("button", { name: /choose values/i });
    await user.click(enumMultiTrigger);
    const enumMultiSelect = screen.getByRole("listbox", { name: /choose values/i });
    await user.click(within(enumMultiSelect).getByRole("option", { name: "pending" }));
    await user.click(within(enumMultiSelect).getByRole("option", { name: "shipped" }));
    expect(within(enumMultiSelect).getByRole("option", { name: "pending" })).toHaveAttribute("aria-selected", "true");
    expect(within(enumMultiSelect).getByRole("option", { name: "shipped" })).toHaveAttribute("aria-selected", "true");
    await user.click(screen.getByRole("button", { name: /^done$/i }));

    await user.selectOptions(screen.getByRole("combobox", { name: /filter column/i }), "created_at");

    const datetimeInput = screen.getByLabelText(/choose date and time/i);
    expect(datetimeInput).toHaveAttribute("type", "datetime-local");
    expect(screen.queryByRole("button", { name: /select value/i })).not.toBeInTheDocument();

    const datetimeOperatorLabels = within(screen.getByRole("combobox", { name: /operator/i }))
      .getAllByRole("option")
      .map((option) => option.textContent);
    expect(datetimeOperatorLabels).toEqual([
      "On date/time (=)",
      "Not on date/time (!=)",
      "After date/time (>)",
      "On or after date/time (>=)",
      "Before date/time (<)",
      "On or before date/time (<=)",
      "Is empty (null)",
      "Is not empty (!null)"
    ]);
  });

  it("supports one-of filters for foreign-key and primary-key columns", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedExplorerRows("orders", [
      {
        id: 101,
        customer_id: 7,
        total: 149.5,
        status: "pending",
        created_at: "2026-04-12T08:45:00Z"
      }
    ]);

    const router = createTestRouter(["/data/orders"]);
    render(<RouterProvider router={router} />);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /^filter/i }));

    await user.selectOptions(screen.getByRole("combobox", { name: /filter column/i }), "customer_id");
    const relationOperatorLabels = within(screen.getByRole("combobox", { name: /operator/i }))
      .getAllByRole("option")
      .map((option) => option.textContent);
    expect(relationOperatorLabels).toContain("One of (in)");

    await user.selectOptions(screen.getByRole("combobox", { name: /filter column/i }), "id");
    const primaryKeyOperatorLabels = within(screen.getByRole("combobox", { name: /operator/i }))
      .getAllByRole("option")
      .map((option) => option.textContent);
    expect(primaryKeyOperatorLabels).toContain("One of (in)");
  });

  it("shows an info hint for comma-separated key one-of filters", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedExplorerRows("orders", [
      {
        id: 101,
        customer_id: 7,
        total: 149.5,
        status: "pending",
        created_at: "2026-04-12T08:45:00Z"
      }
    ]);

    const router = createTestRouter(["/data/orders"]);
    render(<RouterProvider router={router} />);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /^filter/i }));
    await user.selectOptions(screen.getByRole("combobox", { name: /filter column/i }), "customer_id");
    await user.selectOptions(screen.getByRole("combobox", { name: /operator/i }), "in");

    expect(screen.getByText("(i)")).toBeInTheDocument();
    expect(screen.getByTitle("Use , to separate values")).toBeInTheDocument();
  });

  it("supports enum one-of filters with multi-select values", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);

    const fetchMock = vi.fn(async () => {
      return new Response(
        JSON.stringify(
          mockListResponse(
            [
              {
                id: 101,
                customer_id: 7,
                total: 149.5,
                status: "pending",
                created_at: "2026-04-12T08:45:00Z"
              }
            ],
            1
          )
        ),
        {
          status: 200,
          headers: { "Content-Type": "application/json" }
        }
      );
    });

    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/data/orders"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("table");

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /^filter/i }));
    await user.selectOptions(screen.getByRole("combobox", { name: /filter column/i }), "status");
    await user.selectOptions(screen.getByRole("combobox", { name: /operator/i }), "in");
    await user.click(screen.getByRole("button", { name: /choose values/i }));
    const enumMultiSelect = screen.getByRole("listbox", { name: /choose values/i });
    await user.click(within(enumMultiSelect).getByRole("option", { name: "pending" }));
    await user.click(within(enumMultiSelect).getByRole("option", { name: "shipped" }));
    await user.click(screen.getByRole("button", { name: /^done$/i }));
    await user.click(screen.getByRole("button", { name: /add filter/i }));

    await waitFor(() => {
      const lastCall = fetchMock.mock.calls.at(-1) as unknown[] | undefined;
      if (!lastCall) {
        throw new Error("Expected enum one-of filter request to be issued");
      }
      const requestUrl = String(lastCall[0]);
      expect(requestUrl).toContain("filter%5Bstatus%5D%5Bin%5D=pending%2Cshipped");
    });

    expect(screen.getByText("status one of pending, shipped")).toBeInTheDocument();
  });

  it("lets existing filters be reopened for editing from the active chip", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);

    const fetchMock = vi.fn(async () => {
      return new Response(
        JSON.stringify(
          mockListResponse(
            [
              {
                id: 101,
                customer_id: 7,
                total: 149.5,
                status: "pending",
                created_at: "2026-04-12T08:45:00Z"
              }
            ],
            1
          )
        ),
        {
          status: 200,
          headers: { "Content-Type": "application/json" }
        }
      );
    });

    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/data/orders"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("table");

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /^filter/i }));
    await user.selectOptions(screen.getByRole("combobox", { name: /filter column/i }), "status");
    await user.click(screen.getByRole("button", { name: /select value/i }));
    await user.click(screen.getByRole("option", { name: "pending" }));
    await user.click(screen.getByRole("button", { name: /add filter/i }));

    const chipLabel = screen.getByText("status = pending");
    await user.hover(chipLabel);
    const chipEditButton = screen.getByRole("button", { name: /edit filter status = pending/i });
    await user.click(chipEditButton);

    expect(screen.getByRole("button", { name: /^filter/i })).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByRole("combobox", { name: /filter column/i })).toHaveValue("status");
    expect(screen.getByRole("combobox", { name: /operator/i })).toHaveValue("eq");
    expect(screen.getByRole("button", { name: /select value/i })).toHaveTextContent("pending");
    expect(screen.getByRole("button", { name: /save filter/i })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /select value/i }));
    await user.click(screen.getByRole("option", { name: "shipped" }));
    await user.click(screen.getByRole("button", { name: /save filter/i }));

    await waitFor(() => {
      const lastCall = fetchMock.mock.calls.at(-1) as unknown[] | undefined;
      if (!lastCall) {
        throw new Error("Expected edited filter request to be issued");
      }
      expect(String(lastCall[0])).toContain("filter%5Bstatus%5D=shipped");
    });

    expect(screen.getByText("status = shipped")).toBeInTheDocument();
    expect(screen.queryByText("status = pending")).not.toBeInTheDocument();
  });

  it("lets search and sort chips be edited and cleared from the active chips", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);

    const fetchMock = vi.fn(async () => {
      return new Response(
        JSON.stringify(
          mockListResponse(
            [
              {
                id: 101,
                customer_id: 7,
                total: 149.5,
                status: "pending",
                created_at: "2026-04-12T08:45:00Z"
              }
            ],
            1
          )
        ),
        {
          status: 200,
          headers: { "Content-Type": "application/json" }
        }
      );
    });

    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/data/orders"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("table");

    const user = userEvent.setup();

    await user.click(screen.getByRole("button", { name: /^search$/i }));
    await user.type(screen.getByLabelText(/search rows/i), "Customer");
    await user.click(screen.getByRole("button", { name: /apply search/i }));

    await user.click(screen.getByRole("button", { name: /^sort$/i }));
    await user.selectOptions(screen.getByRole("combobox", { name: /sort field/i }), "status");
    await user.selectOptions(screen.getByRole("combobox", { name: /direction/i }), "desc");
    await user.click(screen.getByRole("button", { name: /apply sort/i }));

    const searchChip = screen.getByText(/search: Customer/i);
    await user.hover(searchChip);
    await user.click(screen.getByRole("button", { name: /edit search: Customer/i }));
    expect(screen.getByRole("button", { name: /^search$/i })).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByLabelText(/search rows/i)).toHaveValue("Customer");

    const sortChip = screen.getByText(/sort: status desc/i);
    await user.hover(sortChip);
    await user.click(screen.getByRole("button", { name: /edit sort: status desc/i }));
    expect(screen.getByRole("button", { name: /^sort$/i })).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByRole("combobox", { name: /sort field/i })).toHaveValue("status");
    expect(screen.getByRole("combobox", { name: /direction/i })).toHaveValue("desc");

    await user.hover(searchChip);
    await user.click(screen.getByRole("button", { name: /remove search: Customer/i }));
    await waitFor(() => {
      const lastCall = fetchMock.mock.calls.at(-1) as unknown[] | undefined;
      if (!lastCall) {
        throw new Error("Expected search removal request to be issued");
      }
      expect(String(lastCall[0])).not.toContain("search=Customer");
    });

    await user.hover(sortChip);
    await user.click(screen.getByRole("button", { name: /remove sort: status desc/i }));
    await waitFor(() => {
      const lastCall = fetchMock.mock.calls.at(-1) as unknown[] | undefined;
      if (!lastCall) {
        throw new Error("Expected sort removal request to be issued");
      }
      expect(String(lastCall[0])).not.toContain("sort=-status");
    });

    expect(screen.queryByText(/search: Customer/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/sort: status desc/i)).not.toBeInTheDocument();
  });

  it("closes an open foldout when clicking outside the controls", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedExplorerRows("orders", [
      {
        id: 101,
        customer_id: 7,
        total: 149.5,
        status: "pending",
        created_at: "2026-04-12T08:45:00Z"
      }
    ]);

    const router = createTestRouter(["/data/orders"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("table");

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /^filter/i }));
    expect(screen.getByRole("button", { name: /^filter/i })).toHaveAttribute("aria-expanded", "true");

    await user.click(document.body);

    await waitFor(() => {
      expect(screen.getByRole("button", { name: /^filter/i })).toHaveAttribute("aria-expanded", "false");
    });
  });

  it("closes an open foldout when switching to another table tab", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedExplorerRows("orders", [
      {
        id: 101,
        customer_id: 7,
        total: 149.5,
        status: "pending",
        created_at: "2026-04-12T08:45:00Z"
      }
    ]);
    seedExplorerRows("customers", [
      {
        id: 7,
        name: "Customer 7",
        email: "customer7@example.com"
      }
    ]);

    const router = createTestRouter(["/data/orders"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("table");

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /^filter/i }));
    expect(screen.getByRole("button", { name: /^filter/i })).toHaveAttribute("aria-expanded", "true");

    await user.click(screen.getByRole("link", { name: /^customers$/i }));

    await waitFor(() => {
      expect(screen.getByRole("button", { name: /^filter/i })).toHaveAttribute("aria-expanded", "false");
    });
  });

  it("shows numeric comparison operators for integer and float schema aliases", async () => {
    seedAuthenticated();
    seedSchema({
      version: 1,
      tables: [
        {
          name: "metrics",
          columns: [
            { name: "id", type: "integer", primaryKey: true },
            { name: "count", type: "integer" },
            { name: "price", type: "float" }
          ]
        }
      ],
      relations: []
    });
    seedExplorerRows("metrics", [{ id: 1, count: 10, price: 9.5 }]);

    const router = createTestRouter(["/data/metrics"]);
    render(<RouterProvider router={router} />);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /^filter/i }));
    await user.selectOptions(screen.getByRole("combobox", { name: /filter column/i }), "count");

    const operatorLabels = within(screen.getByRole("combobox", { name: /operator/i }))
      .getAllByRole("option")
      .map((option) => option.textContent);

    expect(operatorLabels).toEqual([
      "Equals (=)",
      "Does not equal (!=)",
      "Greater than (>)",
      "Greater or equal (>=)",
      "Less than (<)",
      "Less or equal (<=)",
      "Is empty (null)",
      "Is not empty (!null)"
    ]);
  });

  it("shows type-aware sort labels for text, numeric, relation, and datetime columns", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedExplorerRows("orders", [
      {
        id: 101,
        customer_id: 7,
        total: 149.5,
        status: "pending",
        created_at: "2026-04-12T08:45:00Z"
      }
    ]);

    const router = createTestRouter(["/data/orders"]);
    render(<RouterProvider router={router} />);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /^sort$/i }));

    const fieldLabels = within(screen.getByRole("combobox", { name: /sort field/i }))
      .getAllByRole("option")
      .map((option) => option.textContent);
    const directionLabels = within(screen.getByRole("combobox", { name: /direction/i }))
      .getAllByRole("option")
      .map((option) => option.textContent);

    expect(fieldLabels).toEqual(["Choose field", "id", "customer_id", "total", "status", "created_at"]);
    expect(directionLabels).toEqual(["Ascending", "Descending"]);

    await user.selectOptions(screen.getByRole("combobox", { name: /sort field/i }), "status");
    expect(
      within(screen.getByRole("combobox", { name: /direction/i }))
        .getAllByRole("option")
        .map((option) => option.textContent)
    ).toEqual(["A → Z", "Z → A"]);

    await user.selectOptions(screen.getByRole("combobox", { name: /sort field/i }), "total");
    expect(
      within(screen.getByRole("combobox", { name: /direction/i }))
        .getAllByRole("option")
        .map((option) => option.textContent)
    ).toEqual(["Lowest first", "Highest first"]);

    await user.selectOptions(screen.getByRole("combobox", { name: /sort field/i }), "created_at");
    expect(
      within(screen.getByRole("combobox", { name: /direction/i }))
        .getAllByRole("option")
        .map((option) => option.textContent)
    ).toEqual(["Earliest time first", "Latest time first"]);
  });

  it("maps search, sort, filters, and pagination controls to the live list contract", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);

    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === "string" ? input : input.toString();
      const parsed = new URL(url, "http://localhost");
      const page = Number(parsed.searchParams.get("page") ?? "1");
      const perPage = Number(parsed.searchParams.get("per_page") ?? "25");

      return new Response(
        JSON.stringify(
          mockListResponse(
            [
              {
                id: 101,
                customer_id: 7,
                total: 149.5,
                status: "pending",
                created_at: "2026-04-12T08:45:00Z"
              }
            ],
            40,
            page,
            perPage
          )
        ),
        {
          status: 200,
          headers: { "Content-Type": "application/json" }
        }
      );
    });

    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/data/orders"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("table");

    const user = userEvent.setup();

    await user.click(screen.getByRole("button", { name: /^search$/i }));
    await user.type(screen.getByLabelText(/search rows/i), "Customer");
    await user.click(screen.getByRole("button", { name: /apply search/i }));
    await waitFor(() => {
      expect(String(fetchMock.mock.calls.at(-1)?.[0])).toContain("search=Customer");
    });

    await user.click(screen.getByRole("button", { name: /^sort$/i }));
    await user.selectOptions(screen.getByRole("combobox", { name: /sort field/i }), "status");
    await user.selectOptions(screen.getByRole("combobox", { name: /direction/i }), "desc");
    await user.click(screen.getByRole("button", { name: /add sort level/i }));
    await user.selectOptions(screen.getByRole("combobox", { name: /then by 2/i }), "created_at");
    await user.selectOptions(screen.getAllByRole("combobox", { name: /direction/i })[1], "desc");
    await user.click(screen.getByRole("button", { name: /apply sort/i }));
    await waitFor(() => {
      expect(String(fetchMock.mock.calls.at(-1)?.[0])).toContain("sort=-status%2C-created_at");
    });

    await user.click(screen.getByRole("button", { name: /^filter/i }));
    await user.selectOptions(screen.getByRole("combobox", { name: /filter column/i }), "status");
    await user.selectOptions(screen.getByRole("combobox", { name: /operator/i }), "eq");
    await user.click(screen.getByRole("button", { name: /select value/i }));
    await user.click(screen.getByRole("option", { name: "pending" }));
    await user.click(screen.getByRole("button", { name: /add filter/i }));
    await waitFor(() => {
      expect(String(fetchMock.mock.calls.at(-1)?.[0])).toContain("filter%5Bstatus%5D=pending");
    });

    await user.selectOptions(screen.getByRole("combobox", { name: /rows per page/i }), "10");
    await waitFor(() => {
      expect(String(fetchMock.mock.calls.at(-1)?.[0])).toContain("per_page=10");
    });

    await user.click(screen.getAllByRole("button", { name: /^next$/i })[0]);
    await waitFor(() => {
      expect(String(fetchMock.mock.calls.at(-1)?.[0])).toContain("page=2");
    });

    expect(screen.getByText(/search: Customer/i)).toBeInTheDocument();
    expect(screen.getByText(/sort: status desc, created_at desc/i)).toBeInTheDocument();
    expect(screen.getByText(/status = pending/i)).toBeInTheDocument();
  });

  it("can start sorting from the column header menu", async () => {
    seedAuthenticated();
    seedSchema(sampleSnapshot);
    seedExplorerRows("orders", [
      {
        id: 101,
        customer_id: 7,
        total: 149.5,
        status: "pending",
        created_at: "2026-04-12T08:45:00Z"
      }
    ]);

    const router = createTestRouter(["/data/orders"]);
    render(<RouterProvider router={router} />);

    await screen.findByRole("table");

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /column actions for status/i }));
    await user.click(screen.getByRole("menuitem", { name: /sort by column/i }));

    expect(screen.getByRole("button", { name: /^sort$/i })).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByRole("combobox", { name: /sort field/i })).toHaveValue("status");
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

  it("can load a table detail route directly without crashing on hook order", async () => {
    seedAuthenticated();

    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === "string" ? input : input.toString();

      if (url.includes("/api/v1/admin/schema")) {
        return new Response(JSON.stringify(sampleSnapshot), {
          status: 200,
          headers: { "Content-Type": "application/json" }
        });
      }

      if (url.includes("/api/v1/data/orders")) {
        return new Response(
          JSON.stringify(
            mockListResponse(
              [
                {
                  id: 101,
                  customer_id: 7,
                  total: 149.5,
                  status: "pending",
                  created_at: "2026-04-12T08:45:00Z"
                }
              ],
              1
            )
          ),
          {
            status: 200,
            headers: { "Content-Type": "application/json" }
          }
        );
      }

      throw new Error(`Unexpected fetch: ${url}`);
    });

    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/data/orders"]);
    render(<RouterProvider router={router} />);

    expect(screen.getByTestId("explorer-loading")).toBeInTheDocument();
    await screen.findByText("Orders");
    await screen.findByRole("table");

    expect(fetchMock).toHaveBeenCalled();
    expect(screen.getByRole("link", { name: /^orders$/i })).toBeInTheDocument();
  });
});
