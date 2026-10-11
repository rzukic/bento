import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { EChart, type ChartClick } from "../features/dashboards/charts/EChart";
import { seedSetupInitializedForTest } from "../features/setup";
import { createTestRouter } from "../router";
import { resetAppStoreForTest, useAppStore } from "../stores/appStore";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import { resetSchemaStoreForTest, useSchemaStore } from "../stores/schemaStore";
import type { ModelSnapshot } from "../types/api";

// A fake ECharts: the test plays the chart's click events.
const chart = vi.hoisted(() => ({ handlers: {} as Record<string, (event: unknown) => void>, zr: {} as Record<string, (event: unknown) => void> }));
vi.mock("echarts/core", () => ({
  use: vi.fn(),
  init: () => ({
    on: (name: string, handler: (event: unknown) => void) => (chart.handlers[name] = handler),
    getZr: () => ({ on: (name: string, handler: (event: unknown) => void) => (chart.zr[name] = handler) }),
    setOption: vi.fn(),
    dispatchAction: vi.fn(),
    resize: vi.fn(),
    clear: vi.fn(),
    dispose: vi.fn(),
  }),
}));

/** Media queries as on a phone (narrow, touch) or a desktop. */
function screenLike(phone: boolean) {
  vi.stubGlobal("matchMedia", (query: string) => ({
    matches: phone ? /max-width|hover: none|pointer: coarse/.test(query) : /min-width/.test(query),
    media: query,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  }));
}

beforeEach(() => {
  resetAppStoreForTest();
  resetAuthStoreForTest();
  seedSetupInitializedForTest();
  resetSchemaStoreForTest();
  useAuthStore.setState({
    status: "authenticated",
    user: { id: 1, username: "admin", role: "admin" },
    accessToken: "token",
    expiresAt: Date.now() + 60_000,
    loginInFlight: false,
    error: null,
  });
  useSchemaStore.setState({ status: "ready", snapshot: { version: 1, tables: [], relations: [] } as unknown as ModelSnapshot, error: null, lastLoadedAt: Date.now(), inFlight: null });
  vi.stubGlobal("fetch", vi.fn(() => Promise.reject(new Error("fetch must be mocked in tests"))));
  chart.handlers = {};
  chart.zr = {};
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("phone navigation", () => {
  it("has a tab bar at the thumb and the rest in a drawer", async () => {
    screenLike(true);
    // A collapsed desktop sidebar does not make the phone's drawer narrow.
    act(() => useAppStore.setState({ sidebarOpen: false }));
    render(<RouterProvider router={createTestRouter(["/settings"])} />);

    // jsdom applies the stylesheet without its media queries: the phone-only bar is display: none there,
    // and hidden elements get no accessible name, so it is found by its label attribute.
    await waitFor(() => expect(document.querySelector('nav[aria-label="Tab bar"]')).not.toBeNull());
    const tabs = document.querySelector<HTMLElement>('nav[aria-label="Tab bar"]')!;
    expect([...tabs.querySelectorAll("a")].map((link) => link.textContent)).toEqual(["Home", "Dashboards", "Forms", "Data"]);
    const shell = document.querySelector(".shell")!;
    expect(shell).not.toHaveClass("shell--collapsed");
    expect(screen.getByRole("complementary", { name: "Primary sidebar", hidden: true })).toHaveAttribute("inert");

    fireEvent.click(within(tabs).getByText("Menu"));
    expect(shell).toHaveClass("shell--drawer");
    expect(screen.getByRole("complementary", { name: "Primary sidebar", hidden: true })).not.toHaveAttribute("inert");

    // Going somewhere closes it.
    fireEvent.click(screen.getByRole("link", { name: "Schema", hidden: true }));
    await waitFor(() => expect(shell).not.toHaveClass("shell--drawer"));
  });

  it("is not there on a desktop", async () => {
    screenLike(false);
    render(<RouterProvider router={createTestRouter(["/settings"])} />);
    await screen.findByRole("complementary", { name: "Primary sidebar" });
    expect(document.querySelector('nav[aria-label="Tab bar"]')).toBeNull();
  });
});

describe("tapping a chart", () => {
  const point = { componentType: "series", seriesIndex: 0, dataIndex: 7, name: "2026-08-01" };

  it("on a touch screen shows the values first; the rows come with a second tap or the button", () => {
    screenLike(true);
    const onItemClick = vi.fn();
    render(<EChart option={{}} label="Net worth" onItemClick={onItemClick} describeItem={() => "August 2026"} />);

    act(() => chart.handlers.click!(point));
    expect(onItemClick).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "Show rows: August 2026 →" }));
    expect(onItemClick).toHaveBeenCalledWith(expect.objectContaining({ dataIndex: 7 }));
    expect(screen.queryByRole("button", { name: /Show rows/ })).not.toBeInTheDocument();

    act(() => chart.handlers.click!(point));
    act(() => chart.handlers.click!(point));
    expect(onItemClick).toHaveBeenCalledTimes(2);

    // Another point first shows its values; empty space puts the button away.
    act(() => chart.handlers.click!({ ...point, dataIndex: 3 }));
    expect(screen.getByRole("button", { name: /Show rows/ })).toBeInTheDocument();
    act(() => chart.zr.click!({ target: null }));
    expect(screen.queryByRole("button", { name: /Show rows/ })).not.toBeInTheDocument();
    expect(onItemClick).toHaveBeenCalledTimes(2);
  });

  it("with a mouse opens the rows at once", () => {
    screenLike(false);
    const onItemClick = vi.fn<(click: ChartClick) => void>();
    render(<EChart option={{}} label="Net worth" onItemClick={onItemClick} />);
    act(() => chart.handlers.click!(point));
    expect(onItemClick).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole("button", { name: /Show rows/ })).not.toBeInTheDocument();
  });
});
