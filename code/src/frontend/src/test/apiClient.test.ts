import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { apiClient, ApiClientError } from "../api/client";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import { resetSchemaStoreForTest, useSchemaStore } from "../stores/schemaStore";

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" }
  });
}

function unauthorizedResponse(message = "Bearer token is invalid or expired."): Response {
  return jsonResponse({ error: { code: "UNAUTHORIZED", message } }, 401);
}

function authenticated(accessToken: string): void {
  useAuthStore.setState({
    status: "authenticated",
    user: { id: 1, username: "admin", role: "admin" },
    accessToken,
    expiresAt: Date.now() + 60_000,
    loginInFlight: false,
    error: null,
    refreshInFlight: null
  });
}

beforeEach(() => {
  resetAuthStoreForTest();
  resetSchemaStoreForTest();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("apiClient", () => {
  it("injects the bearer token from the auth store on authenticated requests", async () => {
    authenticated("token-1");
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ ok: true }));
    vi.stubGlobal("fetch", fetchMock);

    await apiClient.get("/admin/schema");

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe("/api/v1/admin/schema");
    const headers = new Headers(init.headers);
    expect(headers.get("Authorization")).toBe("Bearer token-1");
    expect(init.credentials).toBe("include");
  });

  it("omits the Authorization header on anonymous requests", async () => {
    authenticated("token-1");
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ status: "healthy" }));
    vi.stubGlobal("fetch", fetchMock);

    await apiClient.get("/admin/health", { anonymous: true });

    const [, init] = fetchMock.mock.calls[0];
    const headers = new Headers(init.headers);
    expect(headers.has("Authorization")).toBe(false);
  });

  it("JSON-encodes POST bodies and sets Content-Type", async () => {
    authenticated("token-1");
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ ok: true }));
    vi.stubGlobal("fetch", fetchMock);

    await apiClient.post("/admin/schema", { version: 2 });

    const [, init] = fetchMock.mock.calls[0];
    expect(init.method).toBe("POST");
    expect(init.body).toBe(JSON.stringify({ version: 2 }));
    const headers = new Headers(init.headers);
    expect(headers.get("Content-Type")).toBe("application/json");
  });

  it("parses the backend error envelope into ApiClientError", async () => {
    authenticated("token-1");
    const fetchMock = vi.fn().mockResolvedValue(
      jsonResponse(
        { error: { code: "VALIDATION_FAILED", message: "Schema invalid.", details: { field: "x" } } },
        400
      )
    );
    vi.stubGlobal("fetch", fetchMock);

    await expect(apiClient.get("/admin/schema")).rejects.toMatchObject({
      name: "ApiClientError",
      status: 400,
      code: "VALIDATION_FAILED",
      message: "Schema invalid.",
      details: { field: "x" }
    });
  });

  it("refreshes once and retries when the first call returns 401", async () => {
    authenticated("stale-token");

    const fetchMock = vi.fn();
    let calls = 0;
    fetchMock.mockImplementation(async (input: RequestInfo | URL) => {
      const url = typeof input === "string" ? input : input.toString();
      calls += 1;
      if (url.endsWith("/admin/schema") && calls === 1) {
        return unauthorizedResponse();
      }
      if (url.endsWith("/auth/refresh")) {
        return jsonResponse({
          access_token: "fresh-token",
          token_type: "Bearer",
          expires_at: new Date(Date.now() + 60_000).toISOString(),
          user: { id: 1, username: "admin", role: "admin" }
        });
      }
      if (url.endsWith("/admin/schema") && calls >= 2) {
        return jsonResponse({ version: 7, tables: [], relations: [] });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal("fetch", fetchMock);

    const result = await apiClient.get<{ version: number }>("/admin/schema");

    expect(result).toEqual({ version: 7, tables: [], relations: [] });
    expect(useAuthStore.getState().accessToken).toBe("fresh-token");
    // first call, refresh, retry with fresh token
    expect(fetchMock).toHaveBeenCalledTimes(3);

    const [, retryInit] = fetchMock.mock.calls[2];
    const retryHeaders = new Headers(retryInit.headers);
    expect(retryHeaders.get("Authorization")).toBe("Bearer fresh-token");
  });

  it("logs the session out when the refresh call itself fails", async () => {
    authenticated("stale-token");

    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === "string" ? input : input.toString();
      if (url.endsWith("/admin/schema")) return unauthorizedResponse();
      if (url.endsWith("/auth/refresh")) {
        return unauthorizedResponse("Refresh session is invalid, expired, or revoked.");
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    vi.stubGlobal("fetch", fetchMock);

    await expect(apiClient.get("/admin/schema")).rejects.toBeInstanceOf(ApiClientError);
    expect(useAuthStore.getState().status).toBe("unauthenticated");
    expect(useAuthStore.getState().accessToken).toBeNull();
  });

  it("does not attempt refresh for 401s when skipAuthRetry is set (auth endpoints)", async () => {
    authenticated("stale-token");

    const fetchMock = vi.fn().mockResolvedValue(
      unauthorizedResponse("Invalid username or password.")
    );
    vi.stubGlobal("fetch", fetchMock);

    await expect(
      apiClient.post("/auth/login", { username: "x", password: "y" }, { skipAuthRetry: true })
    ).rejects.toMatchObject({ status: 401, message: "Invalid username or password." });

    // Exactly one call — no refresh attempted.
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});

describe("schemaStore", () => {
  it("loads the snapshot through the shared client and caches it", async () => {
    authenticated("token-1");

    const snapshot = { version: 3, tables: [{ name: "users", columns: [{ name: "id", type: "int64" }] }], relations: [] };
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(snapshot));
    vi.stubGlobal("fetch", fetchMock);

    const result = await useSchemaStore.getState().load();

    expect(result).toEqual(snapshot);
    expect(useSchemaStore.getState().status).toBe("ready");
    expect(useSchemaStore.getState().snapshot).toEqual(snapshot);
    expect(fetchMock).toHaveBeenCalledTimes(1);

    // Concurrent callers share the in-flight promise, so no extra fetches.
    await Promise.all([
      useSchemaStore.getState().load(),
      useSchemaStore.getState().load()
    ]);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("captures the backend error message on failure and exposes it for retry", async () => {
    authenticated("token-1");

    const fetchMock = vi.fn().mockResolvedValue(
      jsonResponse({ error: { code: "INTERNAL_ERROR", message: "Schema engine offline." } }, 500)
    );
    vi.stubGlobal("fetch", fetchMock);

    const result = await useSchemaStore.getState().load();

    expect(result).toBeNull();
    expect(useSchemaStore.getState().status).toBe("error");
    expect(useSchemaStore.getState().error).toBe("Schema engine offline.");
  });

  it("is reset when the auth store logs out", async () => {
    authenticated("token-1");
    const snapshot = { version: 1, tables: [], relations: [] };
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse(snapshot)));

    await useSchemaStore.getState().load();
    expect(useSchemaStore.getState().snapshot).toEqual(snapshot);

    useAuthStore.getState().logout();

    expect(useSchemaStore.getState().snapshot).toBeNull();
    expect(useSchemaStore.getState().status).toBe("idle");
  });
});
