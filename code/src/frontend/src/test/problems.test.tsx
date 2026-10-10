import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { Problem } from "../api/problems";
import { ProblemsPanel } from "../features/problems/ProblemsPanel";
import { SettingsPage } from "../routes/SettingsPage";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";

const api = vi.hoisted(() => ({
  listProblems: vi.fn(),
  clearProblems: vi.fn(),
}));
vi.mock("../api/problems", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/problems")>()), ...api }));

const problems: Problem[] = [
  {
    id: 2,
    loggedAt: "2026-10-10T12:05:00.000Z",
    kind: "timeout",
    method: "POST",
    path: "/api/v1/query",
    status: 504,
    durationMs: 30_012,
    userId: 1,
    username: "ruben",
    via: null,
    source: "/dashboards/6?interval.net=week",
    message: "The database took too long and stopped the query.",
    detail: '{"query":{"source":"securities"}}',
  },
  {
    id: 1,
    loggedAt: "2026-10-10T12:04:00.000Z",
    kind: "slow",
    method: "POST",
    path: "/api/v1/query",
    status: null,
    durationMs: 4200,
    userId: 1,
    username: "ruben",
    via: "Claude",
    source: null,
    message: "Query on trades took 4.2 s (12 rows).",
    detail: null,
  },
];

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
  resetAuthStoreForTest();
});

describe("Problems", () => {
  it("lists slow queries, timeouts and errors with where they came from, and clears them", async () => {
    api.listProblems.mockResolvedValue({ problems, slowMs: 3000 });
    api.clearProblems.mockResolvedValue(undefined);
    const user = userEvent.setup();
    render(<ProblemsPanel />);

    expect(await screen.findByText("/dashboards/6?interval.net=week")).toBeInTheDocument();
    expect(screen.getByText(/Queries slower than 3.0 s/)).toBeInTheDocument();
    const rows = screen.getAllByRole("row").slice(1);
    expect(within(rows[0]).getByText("timeout")).toBeInTheDocument();
    expect(within(rows[0]).getByText("30.0 s")).toBeInTheDocument();
    // No page: the request path; through an app: its name.
    expect(within(rows[1]).getByText("/api/v1/query")).toBeInTheDocument();
    expect(within(rows[1]).getByText(/· Claude/)).toBeInTheDocument();

    await user.click(within(rows[0]).getByRole("button", { name: "Show details" }));
    expect(screen.getByText("POST /api/v1/query → 504 · ruben")).toBeInTheDocument();
    expect(screen.getByText(/"source": "securities"/)).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Clear" }));
    await waitFor(() => expect(screen.getByText("No problems noted.")).toBeInTheDocument());
    expect(api.clearProblems).toHaveBeenCalledTimes(1);
  });

  it("shows on Settings for admins only", async () => {
    api.listProblems.mockResolvedValue({ problems: [], slowMs: 3000 });
    useAuthStore.setState({ status: "authenticated", user: { id: 2, username: "viewer", role: "viewer" }, accessToken: "token" });
    const { unmount } = render(<MemoryRouter><SettingsPage /></MemoryRouter>);
    expect(screen.queryByRole("heading", { name: "Problems" })).not.toBeInTheDocument();
    unmount();

    useAuthStore.setState({ user: { id: 1, username: "ruben", role: "admin" } });
    render(<MemoryRouter><SettingsPage /></MemoryRouter>);
    expect(await screen.findByRole("heading", { name: "Problems" })).toBeInTheDocument();
    expect(await screen.findByText("No problems noted.")).toBeInTheDocument();
  });
});
