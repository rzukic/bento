import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiClientError } from "../api/client";
import { rateLimitRetryDelayMs, resetAuthStoreForTest, useAuthStore } from "../stores/authStore";

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

const session = {
  access_token: "fresh-token",
  token_type: "Bearer",
  expires_at: new Date(Date.now() + 15 * 60_000).toISOString(),
  user: { id: 1, username: "admin", role: "admin" }
};

const rateLimited = (retryAfterSeconds = 1) =>
  jsonResponse(
    { error: { code: "TOO_MANY_REQUESTS", message: "Too many auth attempts. Try again later.", details: { retryAfterSeconds } } },
    429
  );

beforeEach(() => {
  resetAuthStoreForTest();
  vi.useFakeTimers();
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("auth bootstrap", () => {
  it("retries a rate-limited refresh and resumes the session", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(rateLimited(2))
      .mockResolvedValueOnce(jsonResponse(session));
    vi.stubGlobal("fetch", fetchMock);

    const done = useAuthStore.getState().bootstrap();
    expect(useAuthStore.getState().status).toBe("bootstrapping");
    await vi.advanceTimersByTimeAsync(2_000);
    await done;

    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(useAuthStore.getState().status).toBe("authenticated");
    expect(useAuthStore.getState().accessToken).toBe("fresh-token");
  });

  it("gives up after three rate-limited attempts", async () => {
    const fetchMock = vi.fn().mockImplementation(async () => rateLimited(1));
    vi.stubGlobal("fetch", fetchMock);

    const done = useAuthStore.getState().bootstrap();
    await vi.advanceTimersByTimeAsync(5_000);
    await done;

    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(useAuthStore.getState().status).toBe("unauthenticated");
  });

  it("does not retry when the refresh cookie is simply invalid", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValue(jsonResponse({ error: { code: "UNAUTHORIZED", message: "No refresh cookie." } }, 401));
    vi.stubGlobal("fetch", fetchMock);

    await useAuthStore.getState().bootstrap();

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(useAuthStore.getState().status).toBe("unauthenticated");
  });
});

describe("rateLimitRetryDelayMs", () => {
  it("uses the backend hint and caps it at five seconds", () => {
    expect(rateLimitRetryDelayMs(new ApiClientError("x", 429, "TOO_MANY_REQUESTS", { retryAfterSeconds: 2 }))).toBe(2_000);
    expect(rateLimitRetryDelayMs(new ApiClientError("x", 429, "TOO_MANY_REQUESTS", { retryAfterSeconds: 45 }))).toBe(5_000);
    expect(rateLimitRetryDelayMs(new ApiClientError("x", 401))).toBeNull();
    expect(rateLimitRetryDelayMs(new Error("network"))).toBeNull();
  });
});
