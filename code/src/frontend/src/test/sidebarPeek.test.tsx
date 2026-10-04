import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { seedSetupInitializedForTest } from "../features/setup";
import { createTestRouter } from "../router";
import { resetAppStoreForTest, useAppStore } from "../stores/appStore";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import { resetSchemaStoreForTest, useSchemaStore } from "../stores/schemaStore";
import type { ModelSnapshot } from "../types/api";

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
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const shell = () => document.querySelector(".shell")!;

describe("collapsed sidebar", () => {
  it("opens over the page while hovered or focused, without unpinning the collapse", async () => {
    act(() => useAppStore.setState({ sidebarOpen: false }));
    render(<RouterProvider router={createTestRouter(["/settings"])} />);
    const sidebar = await screen.findByRole("complementary", { name: "Primary sidebar" });
    expect(shell()).toHaveClass("shell--collapsed");

    fireEvent.mouseEnter(sidebar);
    await waitFor(() => expect(shell()).toHaveClass("shell--peek"));
    expect(useAppStore.getState().sidebarOpen).toBe(false);

    fireEvent.mouseLeave(sidebar);
    await waitFor(() => expect(shell()).not.toHaveClass("shell--peek"));

    // The keyboard opens it at once; Escape closes it.
    fireEvent.focus(sidebar);
    await waitFor(() => expect(shell()).toHaveClass("shell--peek"));
    fireEvent.keyDown(sidebar, { key: "Escape" });
    await waitFor(() => expect(shell()).not.toHaveClass("shell--peek"));
  });

  it("does nothing while the sidebar is open", async () => {
    act(() => useAppStore.setState({ sidebarOpen: true }));
    render(<RouterProvider router={createTestRouter(["/settings"])} />);
    const sidebar = await screen.findByRole("complementary", { name: "Primary sidebar" });
    fireEvent.mouseEnter(sidebar);
    await new Promise((resolve) => setTimeout(resolve, 250));
    expect(shell()).not.toHaveClass("shell--peek");
  });
});
