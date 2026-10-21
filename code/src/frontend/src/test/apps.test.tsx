import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { BentoApp } from "../api/apps";
import { AppsPage } from "../features/apps/AppsPage";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import { resetSchemaStoreForTest, useSchemaStore } from "../stores/schemaStore";
import type { ModelSnapshot } from "../types/api";

const api = vi.hoisted(() => ({
  listApps: vi.fn(),
  createApp: vi.fn(),
  updateApp: vi.fn(),
  deleteApp: vi.fn(),
  createAppPassword: vi.fn(),
  revokeAppPassword: vi.fn(),
}));
vi.mock("../api/apps", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/apps")>()), ...api }));

const app: BentoApp = {
  id: 4,
  name: "My calendar",
  kind: "calendar",
  config: { calendars: [{ name: "Termine", slug: "termine", table: "appointments", title: "title", start: "starts_at", end: "ends_at", color: "#3B82F6" }] },
  enabled: true,
  passwords: [{ id: 9, name: "Mac", prefix: "bento_app_AbCd", createdAt: "2026-10-20T10:00:00Z", lastUsedAt: null }],
  createdAt: "2026-10-20T10:00:00Z",
  updatedAt: "2026-10-20T10:00:00Z",
};

beforeEach(() => {
  resetAuthStoreForTest();
  resetSchemaStoreForTest();
  useAuthStore.setState({ status: "authenticated", user: { id: 1, username: "admin", role: "admin" }, accessToken: "token", expiresAt: Date.now() + 60_000, loginInFlight: false, error: null });
  useSchemaStore.setState({
    status: "ready",
    snapshot: {
      version: 1,
      tables: [
        {
          name: "appointments",
          displayName: "Appointments",
          columns: [
            { name: "id", type: "integer", primaryKey: true },
            { name: "title", type: "text" },
            { name: "starts_at", type: "datetime" },
            { name: "ends_at", type: "datetime" },
            { name: "place", type: "text" },
            { name: "private", type: "boolean" },
          ],
        },
      ],
      relations: [],
    } as unknown as ModelSnapshot,
    error: null,
    lastLoadedAt: Date.now(),
    inFlight: null,
  });
  api.listApps.mockResolvedValue([]);
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("apps", () => {
  it("makes a calendar of a table: title, start, end, location and a filter", async () => {
    api.createApp.mockResolvedValue(app);
    const user = userEvent.setup();
    render(<AppsPage />);

    await user.click(await screen.findByRole("button", { name: "Add calendar app" }));
    await user.type(screen.getByLabelText("Name"), "My calendar");
    await user.type(screen.getByLabelText("Calendar name (calendar 1)"), "Termine");
    await user.selectOptions(screen.getByLabelText("Table (calendar 1)"), "appointments");
    // The first text and the first date-time column are taken.
    expect(screen.getByLabelText("Title (calendar 1)")).toHaveValue("title");
    expect(screen.getByLabelText("Starts (calendar 1)")).toHaveValue("starts_at");
    // Only date and date-time columns can start or end an event, only yes/no ones make it all-day.
    expect(within(screen.getByLabelText("Ends (calendar 1)")).queryByRole("option", { name: "place" })).toBeNull();
    expect(within(screen.getByLabelText("All day when (calendar 1)")).getAllByRole("option").map((o) => o.textContent)).toEqual(["None", "private"]);
    await user.selectOptions(screen.getByLabelText("Ends (calendar 1)"), "ends_at");
    await user.selectOptions(screen.getByLabelText("Location (calendar 1)"), "place");
    await user.click(screen.getByRole("button", { name: "Filter" }));
    await user.selectOptions(screen.getByLabelText("Filter 1 column (calendar 1)"), "private");
    await user.selectOptions(screen.getByLabelText("Filter 1 condition (calendar 1)"), "neq");
    await user.type(screen.getByLabelText("Filter 1 value (calendar 1)"), "true");
    await user.click(screen.getAllByRole("button", { name: "Add app" }).at(-1)!);

    await waitFor(() => expect(api.createApp).toHaveBeenCalledTimes(1));
    expect(api.createApp).toHaveBeenCalledWith("My calendar", [
      {
        name: "Termine",
        table: "appointments",
        title: "title",
        start: "starts_at",
        end: "ends_at",
        allDay: null,
        description: null,
        location: "place",
        color: "#3B82F6",
        filters: [{ column: "private", op: "neq", value: "true" }],
      },
    ]);
  });

  it("adds a device: its password and addresses are shown once; a device can be taken back", async () => {
    api.listApps.mockResolvedValue([app]);
    api.createAppPassword.mockResolvedValue({
      password: { id: 10, name: "iPhone", prefix: "bento_app_XyZw", createdAt: "2026-10-21T10:00:00Z", lastUsedAt: null },
      secret: "bento_app_XyZwSecret",
      links: [{ calendar: "Termine", kind: "ical", url: "https://bento.example/feeds/bento_app_XyZwSecret/termine.ics" }],
    });
    api.revokeAppPassword.mockResolvedValue(undefined);
    vi.spyOn(window, "confirm").mockReturnValue(true);
    const user = userEvent.setup();
    render(<AppsPage />);

    expect(await screen.findByText("My calendar")).toBeInTheDocument();
    expect(screen.getByText("Mac")).toBeInTheDocument();
    await user.type(screen.getByLabelText("Device name"), "iPhone");
    await user.click(screen.getByRole("button", { name: "Add device" }));

    const secret = await screen.findByRole("region", { name: "New device password" });
    expect(within(secret).getByText("https://bento.example/feeds/bento_app_XyZwSecret/termine.ics")).toBeInTheDocument();
    expect(api.createAppPassword).toHaveBeenCalledWith(4, "iPhone");
    await user.click(within(secret).getByRole("button", { name: "Done" }));
    expect(screen.queryByRole("region", { name: "New device password" })).toBeNull();

    await user.click(screen.getByRole("button", { name: "Take back the password of Mac" }));
    await waitFor(() => expect(api.revokeAppPassword).toHaveBeenCalledWith(4, 9));
  });

  it("tells others that an administrator sets up apps", () => {
    useAuthStore.setState({ user: { id: 2, username: "viewer", role: "viewer" } });
    render(<AppsPage />);
    expect(screen.getByText("Apps are set up by an administrator.")).toBeInTheDocument();
    expect(api.listApps).not.toHaveBeenCalled();
  });
});
