import { afterEach, describe, expect, it, vi } from "vitest";
import { apiClient, dataVersion } from "../api/client";
import { FRESH_MS, loadCached, peekCached } from "../api/queryCache";

function respond() {
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ data: {} }), { status: 200, headers: { "Content-Type": "application/json" } })));
}

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("query cache", () => {
  it("keeps a result fresh for a while, then asks again", async () => {
    vi.useFakeTimers({ now: new Date("2026-10-10T12:00:00Z"), toFake: ["Date"] });
    const load = vi.fn(async () => ({ total: 1 }));

    expect(peekCached("a")).toBeUndefined();
    await loadCached("a", load);
    expect(peekCached("a")).toEqual({ value: { total: 1 }, fresh: true });

    vi.setSystemTime(Date.now() + FRESH_MS + 1);
    // Still there to show, but no longer current.
    expect(peekCached("a")).toEqual({ value: { total: 1 }, fresh: false });
  });

  it("sends the same request once while it is on its way; force asks again", async () => {
    let resolve: (value: number) => void = () => {};
    const load = vi.fn(() => new Promise<number>((r) => (resolve = r)));
    const first = loadCached("b", load);
    const second = loadCached("b", load);
    expect(load).toHaveBeenCalledTimes(1);
    resolve(7);
    expect(await Promise.all([first, second])).toEqual([7, 7]);

    await loadCached("b", async () => 8, true);
    expect(peekCached("b")?.value).toBe(8);
  });

  it("a change through the API makes cached results out of date; queries do not", async () => {
    respond();
    await loadCached("c", async () => 1);

    const before = dataVersion();
    await apiClient.post("/query", { source: "x" });
    await apiClient.post("/queries/3/run", {});
    await apiClient.post("/formulas/validate", {});
    await apiClient.get("/data/transactions");
    expect(dataVersion()).toBe(before);
    expect(peekCached("c")?.fresh).toBe(true);

    await apiClient.patch("/data/transactions/1", { amount: 2 });
    expect(dataVersion()).toBe(before + 1);
    expect(peekCached("c")).toEqual({ value: 1, fresh: false });
  });

  it("does not keep failures", async () => {
    await expect(loadCached("d", async () => Promise.reject(new Error("down")))).rejects.toThrow("down");
    expect(peekCached("d")).toBeUndefined();
  });
});
