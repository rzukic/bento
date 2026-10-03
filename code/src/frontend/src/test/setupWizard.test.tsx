import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { resetSetupStoreForTest, useSetupStore } from "../features/setup";
import { buildSetupSteps } from "../features/setup/SetupPage";
import { createTestRouter } from "../router";
import { resetAppStoreForTest } from "../stores/appStore";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import { resetExplorerStoreForTest } from "../stores/explorerStore";
import { resetSchemaStoreForTest } from "../stores/schemaStore";
import type { SetupStatus } from "../types/setup";

const uninitialized: SetupStatus = {
  initialized: false,
  auth_mode: "standard",
  auth_required: true,
  registration_bootstrap_open: true,
  database_provider: "sqlite",
  schema_version: 0,
  sample_data_available: true,
  version: "0.2.0-test"
};

const jsonResponse = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" }
  });

const errorResponse = (status: number, code: string, message: string) =>
  jsonResponse({ error: { code, message } }, status);

type Handler = (url: string, init?: RequestInit) => Response | Promise<Response> | undefined;

/**
 * Minimal routing fetch mock: the first handler that returns a Response wins;
 * anything unmatched fails loudly so a missing stub cannot hide a real call.
 */
function stubFetch(...handlers: Handler[]) {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === "string" ? input : input.toString();
    for (const handler of handlers) {
      const response = await handler(url, init);
      if (response) return response;
    }
    throw new Error(`Unexpected fetch: ${init?.method ?? "GET"} ${url}`);
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

const statusHandler =
  (status: SetupStatus | (() => SetupStatus)): Handler =>
  (url, init) => {
    if (url.endsWith("/api/v1/setup/status") && (init?.method ?? "GET") === "GET") {
      return jsonResponse(typeof status === "function" ? status() : status);
    }
    return undefined;
  };

const refreshRejected: Handler = (url, init) => {
  if (url.endsWith("/api/v1/auth/refresh") && init?.method === "POST") {
    return errorResponse(401, "unauthorized", "No refresh cookie.");
  }
  return undefined;
};

/** The shell home page loads the schema snapshot; keep it empty and quiet. */
const emptySchema: Handler = (url) => {
  if (url.includes("/api/v1/admin/schema")) {
    return jsonResponse({ version: 0, tables: [] });
  }
  return undefined;
};

beforeEach(() => {
  resetAppStoreForTest();
  resetAuthStoreForTest();
  resetSetupStoreForTest();
  resetSchemaStoreForTest();
  resetExplorerStoreForTest();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("buildSetupSteps", () => {
  it("includes the admin step only when authentication is required", () => {
    expect(buildSetupSteps(uninitialized)).toEqual(["welcome", "environment", "admin", "sample", "done"]);
    expect(buildSetupSteps({ ...uninitialized, auth_mode: "open", auth_required: false })).toEqual([
      "welcome",
      "environment",
      "sample",
      "done"
    ]);
  });

  it("drops the sample data step once the installation has a schema", () => {
    expect(buildSetupSteps({ ...uninitialized, schema_version: 3, sample_data_available: false })).toEqual([
      "welcome",
      "environment",
      "admin",
      "done"
    ]);
  });
});

describe("First-run routing", () => {
  it("sends an uninitialised installation from the workspace root to /setup", async () => {
    stubFetch(statusHandler(uninitialized));
    const router = createTestRouter(["/"]);
    render(<RouterProvider router={router} />);

    expect(await screen.findByRole("heading", { name: /one place to shape it/i })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/setup");
    // The gate decided before auth even tried to resume a session.
    expect(useAuthStore.getState().status).toBe("idle");
  });

  it("sends an uninitialised installation from /login to /setup as well", async () => {
    stubFetch(statusHandler(uninitialized));
    const router = createTestRouter(["/login"]);
    render(<RouterProvider router={router} />);

    expect(await screen.findByRole("heading", { name: /one place to shape it/i })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/setup");
  });

  it("bounces /setup back to the app on an initialised installation", async () => {
    stubFetch(statusHandler({ ...uninitialized, initialized: true, registration_bootstrap_open: false }), refreshRejected);
    const router = createTestRouter(["/setup"]);
    render(<RouterProvider router={router} />);

    // Unauthenticated → the regular login page, not the wizard.
    expect(await screen.findByText(/Stable public route: \/login/i)).toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/login");
  });

  it("does not block the app when the status call fails", async () => {
    stubFetch((url) => {
      if (url.endsWith("/api/v1/setup/status")) return errorResponse(503, "unavailable", "Database is starting.");
      return undefined;
    }, refreshRejected);
    const router = createTestRouter(["/"]);
    render(<RouterProvider router={router} />);

    expect(await screen.findByText(/Stable public route: \/login/i)).toBeInTheDocument();
    expect(useSetupStore.getState().status).toBe("error");
  });

  it("offers a retry on /setup when the status call fails", async () => {
    let attempts = 0;
    stubFetch((url) => {
      if (!url.endsWith("/api/v1/setup/status")) return undefined;
      attempts += 1;
      return attempts === 1
        ? errorResponse(503, "unavailable", "Database is starting.")
        : jsonResponse(uninitialized);
    });
    const router = createTestRouter(["/setup"]);
    render(<RouterProvider router={router} />);

    expect(await screen.findByRole("heading", { name: /setup unavailable/i })).toBeInTheDocument();
    expect(screen.getByText(/database is starting/i)).toBeInTheDocument();

    await userEvent.setup().click(screen.getByRole("button", { name: /retry/i }));
    expect(await screen.findByRole("heading", { name: /one place to shape it/i })).toBeInTheDocument();
  });
});

describe("Setup wizard", () => {
  it("walks through environment, admin registration, sample data and lands in the workspace", async () => {
    const registered: Array<{ username: string; password: string }> = [];
    let sampleCalls = 0;
    const fetchMock = stubFetch(
      statusHandler(uninitialized),
      (url, init) => {
        if (url.endsWith("/api/v1/auth/register") && init?.method === "POST") {
          registered.push(JSON.parse(String(init.body)));
          return jsonResponse({ id: 1, username: "root", role: "admin", bootstrap_admin: true }, 201);
        }
        return undefined;
      },
      (url, init) => {
        if (url.endsWith("/api/v1/auth/login") && init?.method === "POST") {
          return jsonResponse({
            access_token: "wizard-token",
            token_type: "Bearer",
            expires_at: new Date(Date.now() + 15 * 60_000).toISOString(),
            user: { id: 1, username: "root", role: "admin" }
          });
        }
        return undefined;
      },
      (url, init) => {
        if (url.endsWith("/api/v1/setup/sample-data") && init?.method === "POST") {
          sampleCalls += 1;
          return jsonResponse({
            schema_version: 1,
            tables_created: ["customers", "orders", "products"],
            rows_inserted: { customers: 6, orders: 10, products: 5 }
          });
        }
        return undefined;
      },
      emptySchema
    );

    const router = createTestRouter(["/"]);
    render(<RouterProvider router={router} />);
    const user = userEvent.setup();

    // Welcome
    await screen.findByRole("heading", { name: /one place to shape it/i });
    await user.click(screen.getByRole("button", { name: /get started/i }));

    // Environment: read-only facts from the status endpoint
    await screen.findByRole("heading", { name: /read from your deployment/i });
    expect(screen.getByText("sqlite")).toBeInTheDocument();
    expect(screen.getByText("standard")).toBeInTheDocument();
    expect(screen.getByText("0.2.0-test")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: /continue/i }));

    // Admin account: confirm must match before the submit enables
    await screen.findByRole("heading", { name: /first account runs the place/i });
    await user.type(screen.getByRole("textbox", { name: /username/i }), "root");
    await user.type(screen.getByLabelText(/^password/i), "correct horse battery");
    await user.type(screen.getByLabelText(/confirm password/i), "wrong");
    expect(screen.getByText(/passwords do not match/i)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /create account and continue/i })).toBeDisabled();
    await user.clear(screen.getByLabelText(/confirm password/i));
    await user.type(screen.getByLabelText(/confirm password/i), "correct horse battery");
    await user.click(screen.getByRole("button", { name: /create account and continue/i }));

    // Sample data: loaded via the authenticated client
    await screen.findByRole("heading", { name: /start with something to see/i });
    expect(registered).toEqual([{ username: "root", password: "correct horse battery" }]);
    expect(useAuthStore.getState().status).toBe("authenticated");
    expect(useSetupStore.getState().info?.initialized).toBe(true);
    expect(useSetupStore.getState().wizardActive).toBe(true);

    await user.click(screen.getByRole("button", { name: /load sample data/i }));
    expect(await screen.findByText(/demo workspace loaded/i)).toBeInTheDocument();
    expect(screen.getByText(/3 tables \(customers, orders, products\), 21 rows, schema version 1/i)).toBeInTheDocument();
    expect(sampleCalls).toBe(1);
    const sampleCall = fetchMock.mock.calls.find(([input]) => String(input).endsWith("/api/v1/setup/sample-data"));
    expect(new Headers(sampleCall?.[1]?.headers).get("Authorization")).toBe("Bearer wizard-token");
    await user.click(screen.getByRole("button", { name: /continue/i }));

    // Done → workspace
    await screen.findByRole("heading", { name: /^ready\.$/i });
    await user.click(screen.getByRole("button", { name: /open workspace/i }));

    await waitFor(() => {
      expect(screen.getByRole("navigation", { name: "Main navigation" })).toBeInTheDocument();
    });
    expect(router.state.location.pathname).toBe("/");
    expect(useSetupStore.getState().wizardActive).toBe(false);
  });

  it("skips the admin step in open auth mode", async () => {
    stubFetch(statusHandler({ ...uninitialized, auth_mode: "open", auth_required: false, registration_bootstrap_open: false }));
    const router = createTestRouter(["/setup"]);
    render(<RouterProvider router={router} />);
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: /get started/i }));
    await user.click(await screen.findByRole("button", { name: /continue/i }));

    expect(await screen.findByRole("heading", { name: /start with something to see/i })).toBeInTheDocument();
    expect(screen.queryByText(/admin account/i)).not.toBeInTheDocument();
  });

  it("lets the user skip sample data", async () => {
    stubFetch(statusHandler({ ...uninitialized, auth_mode: "open", auth_required: false }));
    const router = createTestRouter(["/setup"]);
    render(<RouterProvider router={router} />);
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: /get started/i }));
    await user.click(await screen.findByRole("button", { name: /continue/i }));
    await user.click(await screen.findByRole("button", { name: /skip for now/i }));

    expect(await screen.findByRole("heading", { name: /^ready\.$/i })).toBeInTheDocument();
  });

  it("explains when another admin was registered first and points to sign-in", async () => {
    let statusCalls = 0;
    stubFetch(
      statusHandler(() => {
        statusCalls += 1;
        // First call: still uninitialised. After the failed registration the
        // wizard re-reads the status and the backend now reports an admin.
        return statusCalls === 1 ? uninitialized : { ...uninitialized, initialized: true, registration_bootstrap_open: false };
      }),
      (url, init) => {
        if (url.endsWith("/api/v1/auth/register") && init?.method === "POST") {
          return errorResponse(403, "forbidden", "Only administrators can register users.");
        }
        return undefined;
      }
    );
    const router = createTestRouter(["/setup"]);
    render(<RouterProvider router={router} />);
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: /get started/i }));
    await user.click(await screen.findByRole("button", { name: /continue/i }));
    await screen.findByRole("heading", { name: /first account runs the place/i });
    await user.type(screen.getByRole("textbox", { name: /username/i }), "late");
    await user.type(screen.getByLabelText(/^password/i), "pw-123456");
    await user.type(screen.getByLabelText(/confirm password/i), "pw-123456");
    await user.click(screen.getByRole("button", { name: /create account and continue/i }));

    expect(await screen.findByText(/an administrator account already exists/i)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /go to sign in/i })).toHaveAttribute("href", "/login");
    await waitFor(() => expect(useSetupStore.getState().info?.initialized).toBe(true));
    expect(useAuthStore.getState().status).toBe("idle");
  });

  it("surfaces backend validation errors from registration", async () => {
    stubFetch(statusHandler(uninitialized), (url, init) => {
      if (url.endsWith("/api/v1/auth/register") && init?.method === "POST") {
        return errorResponse(400, "validation_error", "Password must be at least 8 characters.");
      }
      return undefined;
    });
    const router = createTestRouter(["/setup"]);
    render(<RouterProvider router={router} />);
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: /get started/i }));
    await user.click(await screen.findByRole("button", { name: /continue/i }));
    await screen.findByRole("heading", { name: /first account runs the place/i });
    await user.type(screen.getByRole("textbox", { name: /username/i }), "root");
    await user.type(screen.getByLabelText(/^password/i), "short");
    await user.type(screen.getByLabelText(/confirm password/i), "short");
    await user.click(screen.getByRole("button", { name: /create account and continue/i }));

    expect(await screen.findByText(/at least 8 characters/i)).toBeInTheDocument();
    // Still on the admin step with the form usable.
    expect(screen.getByRole("button", { name: /create account and continue/i })).toBeEnabled();
    expect(useSetupStore.getState().info?.initialized).toBe(false);
  });
});
