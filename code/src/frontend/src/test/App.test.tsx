import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createTestRouter } from "../router";
import { resetAppStoreForTest } from "../stores/appStore";
import { seedSetupInitializedForTest } from "../features/setup";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import { resetExplorerStoreForTest } from "../stores/explorerStore";
import { resetSchemaStoreForTest, useSchemaStore } from "../stores/schemaStore";

function seedAuthenticated(username = "admin", role = "admin") {
  useAuthStore.setState({
    status: "authenticated",
    user: { id: 1, username, role },
    accessToken: "test-access-token",
    expiresAt: Date.now() + 60_000,
    loginInFlight: false,
    error: null
  });
}

function seedUnauthenticated() {
  useAuthStore.setState({
    status: "unauthenticated",
    user: null,
    accessToken: null,
    expiresAt: null,
    loginInFlight: false,
    error: null
  });
}

beforeEach(() => {
  resetAppStoreForTest();
  resetAuthStoreForTest();
  seedSetupInitializedForTest();
  resetSchemaStoreForTest();
  resetExplorerStoreForTest();
  // Fail any unmocked fetch loudly so a missing stub cannot hide a real
  // network call during tests.
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

describe("App scaffold", () => {
  it("renders the shell navigation and breadcrumbs for an authenticated app route", () => {
    seedAuthenticated();
    // Seed schema so the table detail page finds the table
    useSchemaStore.setState({
      status: "ready",
      snapshot: {
        version: 1,
        tables: [{ name: "orders", columns: [{ name: "id", type: "int64", primaryKey: true }] }],
        relations: []
      },
      error: null,
      lastLoadedAt: Date.now(),
      inFlight: null
    });

    const router = createTestRouter(["/data/orders"]);

    render(<RouterProvider router={router} />);

    expect(screen.getByRole("navigation", { name: "Main navigation" })).toBeInTheDocument();
    expect(screen.getByRole("navigation", { name: "Breadcrumb" })).toBeInTheDocument();
    expect(screen.getByRole("navigation", { name: "Tables" })).toBeInTheDocument();
    expect(screen.getByText("Workspace")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /^orders$/i })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: /orders/i })).not.toBeInTheDocument();
  });

  it("keeps the login route outside the main shell navigation", () => {
    seedUnauthenticated();
    const router = createTestRouter(["/login"]);

    render(<RouterProvider router={router} />);

    expect(screen.queryByRole("navigation", { name: "Main navigation" })).not.toBeInTheDocument();
    expect(screen.getByText(/Stable public route: \/login/i)).toBeInTheDocument();
  });
});

describe("Route guard", () => {
  it("redirects unauthenticated visitors from a protected route to /login", () => {
    seedUnauthenticated();
    const router = createTestRouter(["/data"]);

    render(<RouterProvider router={router} />);

    expect(screen.queryByRole("navigation", { name: "Main navigation" })).not.toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Sign in" })).toBeInTheDocument();
    expect(screen.getByText(/Stable public route: \/login/i)).toBeInTheDocument();
  });

  it("bounces already-authenticated visitors away from /login", () => {
    seedAuthenticated();
    const router = createTestRouter(["/login"]);

    render(<RouterProvider router={router} />);

    // Landed back on the workspace shell instead of the login card.
    expect(screen.getByRole("navigation", { name: "Main navigation" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Sign in" })).not.toBeInTheDocument();
  });

  it("shows the resuming-session splash while bootstrap is in flight", async () => {
    // Leave status = "idle" so RequireAuth triggers bootstrap, then make the
    // refresh call hang so we can observe the splash state.
    const never = new Promise<Response>(() => {});
    vi.stubGlobal("fetch", vi.fn().mockReturnValue(never));

    const router = createTestRouter(["/"]);
    render(<RouterProvider router={router} />);

    expect(await screen.findByText(/resuming session/i)).toBeInTheDocument();
  });
});

describe("Login form", () => {
  it("posts credentials, stores the session, and redirects to the workspace", async () => {
    seedUnauthenticated();

    const jsonResponse = (body: unknown, status = 200) =>
      new Response(JSON.stringify(body), {
        status,
        headers: { "Content-Type": "application/json" }
      });

    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === "string" ? input : input.toString();
      if (url.endsWith("/api/v1/auth/login")) {
        return jsonResponse({
          access_token: "fresh-token",
          token_type: "Bearer",
          expires_at: new Date(Date.now() + 15 * 60_000).toISOString(),
          user: { id: 1, username: "admin", role: "admin" }
        });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/login"]);
    render(<RouterProvider router={router} />);

    const user = userEvent.setup();
    await user.type(screen.getByRole("textbox", { name: /username/i }), "admin");
    // password input is not a textbox role; grab by label
    const password = screen.getByLabelText(/password/i);
    await user.type(password, "hunter22!");
    await user.click(screen.getByRole("button", { name: /sign in/i }));

    await waitFor(() => {
      expect(useAuthStore.getState().status).toBe("authenticated");
    });
    expect(useAuthStore.getState().accessToken).toBe("fresh-token");
    expect(useAuthStore.getState().user?.username).toBe("admin");

    // Landed on the workspace shell.
    await waitFor(() => {
      expect(screen.getByRole("navigation", { name: "Main navigation" })).toBeInTheDocument();
    });

    expect(fetchMock).toHaveBeenCalledWith(
      "/api/v1/auth/login",
      expect.objectContaining({
        method: "POST",
        credentials: "include"
      })
    );
  });

  it("surfaces a backend error message when credentials are rejected", async () => {
    seedUnauthenticated();

    const fetchMock = vi.fn(
      async () =>
        new Response(
          JSON.stringify({ error: { code: "UNAUTHORIZED", message: "Invalid username or password." } }),
          { status: 401, headers: { "Content-Type": "application/json" } }
        )
    );
    vi.stubGlobal("fetch", fetchMock);

    const router = createTestRouter(["/login"]);
    render(<RouterProvider router={router} />);

    const user = userEvent.setup();
    await user.type(screen.getByRole("textbox", { name: /username/i }), "admin");
    await user.type(screen.getByLabelText(/password/i), "wrong");
    await user.click(screen.getByRole("button", { name: /sign in/i }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Invalid username or password.");
    expect(useAuthStore.getState().status).toBe("unauthenticated");
  });
});

describe("Logout", () => {
  it("confirms sign out, revokes the server session, then clears the local session and returns the user to /login", async () => {
    seedAuthenticated();
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === "string" ? input : input.toString();
      if (url.endsWith("/api/v1/auth/logout")) {
        return new Response(null, { status: 204 });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal("fetch", fetchMock);
    const router = createTestRouter(["/"]);

    render(<RouterProvider router={router} />);

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /sign out/i }));

    expect(screen.getByRole("dialog", { name: /sign out of this workspace/i })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /^cancel$/i }));
    expect(
      screen.queryByRole("dialog", { name: /sign out of this workspace/i })
    ).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /sign out/i }));
    const dialog = screen.getByRole("dialog", { name: /sign out of this workspace/i });
    await user.click(within(dialog).getByRole("button", { name: /^sign out$/i }));

    await waitFor(() => {
      expect(useAuthStore.getState().status).toBe("unauthenticated");
    });
    expect(useAuthStore.getState().user).toBeNull();
    expect(useAuthStore.getState().accessToken).toBeNull();
    await waitFor(() => {
      expect(screen.getByRole("heading", { name: "Sign in" })).toBeInTheDocument();
    });
    // The HttpOnly refresh cookie can only be ended server-side.
    expect(fetchMock).toHaveBeenCalledWith(
      "/api/v1/auth/logout",
      expect.objectContaining({ method: "POST", credentials: "include" })
    );
  });
});

describe("Theme switching", () => {
  it("applies the selected theme mode to the document root", async () => {
    seedAuthenticated();
    const router = createTestRouter(["/"]);

    render(<RouterProvider router={router} />);

    const user = userEvent.setup();
    const appearance = screen.getByRole("group", { name: /appearance/i });

    await user.click(within(appearance).getByRole("button", { name: /^light$/i }));
    expect(document.documentElement.dataset.theme).toBe("light");
    expect(within(appearance).getByRole("button", { name: /^light$/i })).toHaveAttribute("aria-pressed", "true");

    await user.click(within(appearance).getByRole("button", { name: /^dark$/i }));
    expect(document.documentElement.dataset.theme).toBe("dark");
  });
});
