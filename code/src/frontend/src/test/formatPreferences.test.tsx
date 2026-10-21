import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { periodStart, quarterName, setWorkspaceCalendar, todayIso, workspaceCalendar } from "../lib/calendar";
import { resolveRange } from "../features/dashboards/dateRange";
import { userEvent } from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { WorkspaceFormatPanel } from "../features/settings/FormatSettings";
import { useSetupStore } from "../features/setup/setupStore";
import { formatNumber } from "../lib/format";
import { browserLocale, effectiveLocale, resetFormatForTest, setUserLocale, setWorkspaceFormat, useFormatStore } from "../lib/preferences";

const settingsApi = vi.hoisted(() => ({ saveWorkspaceSettings: vi.fn() }));
vi.mock("../api/settings", () => settingsApi);

afterEach(() => {
  cleanup();
  setUserLocale(null);
  resetFormatForTest();
  vi.clearAllMocks();
});

const plain = (text: string) => text.replace(/[\u00a0\u202f]/g, " ");

describe("number format", () => {
  it("this browser's choice wins, then the workspace's, then the browser's language", () => {
    expect(effectiveLocale()).toBe(browserLocale());
    setWorkspaceFormat("de-DE", "EUR");
    expect(effectiveLocale()).toBe("de-DE");
    expect(plain(formatNumber(-1250, "currency"))).toBe("-1.250,00 €");
    setUserLocale("en-US");
    expect(effectiveLocale()).toBe("en-US");
    expect(formatNumber(-1250, "currency")).toBe("-€1,250.00");
  });

  it("writes amounts in the workspace's currency, and renders the page again on a change", () => {
    const before = useFormatStore.getState().version;
    setWorkspaceFormat("de-CH", "CHF");
    expect(useFormatStore.getState().version).toBe(before + 1);
    expect(plain(formatNumber(1250, "currency"))).toMatch(/CHF.?1.250\.00/);
    // The same again changes nothing.
    setWorkspaceFormat("de-CH", "CHF");
    expect(useFormatStore.getState().version).toBe(before + 1);
  });

  it("admins set the workspace's format", async () => {
    useSetupStore.setState({ info: { initialized: true, auth_mode: "standard", auth_required: true, registration_bootstrap_open: false, database_provider: "sqlite", schema_version: 1, sample_data_available: false, version: "1", locale: null, currency: "EUR" } });
    settingsApi.saveWorkspaceSettings.mockResolvedValue({ locale: "de-AT", currency: "EUR" });
    const user = userEvent.setup();
    render(<WorkspaceFormatPanel />);

    await user.selectOptions(screen.getByLabelText("Workspace numbers and dates"), "de-AT");
    // Austria writes the euro sign in front.
    expect(plain(screen.getByText(/Example:/).textContent ?? "")).toContain("-€ 1.250,00");
    await user.click(screen.getByRole("button", { name: "Save" }));

    expect(settingsApi.saveWorkspaceSettings).toHaveBeenCalledWith({ locale: "de-AT", currency: "EUR", timeZone: "", weekStart: "monday", yearStart: 1 });
    await waitFor(() => expect(effectiveLocale()).toBe("de-AT"));
    expect(useSetupStore.getState().info?.locale).toBe("de-AT");
  });

  it("admins set the workspace's calendar: time zone, week start, fiscal year", async () => {
    useSetupStore.setState({ info: { initialized: true, auth_mode: "standard", auth_required: true, registration_bootstrap_open: false, database_provider: "sqlite", schema_version: 1, sample_data_available: false, version: "1", locale: null, currency: "EUR", time_zone: "UTC", time_zone_set: false, week_start: "monday", year_start: 1 } });
    settingsApi.saveWorkspaceSettings.mockResolvedValue({ locale: null, currency: "EUR", timeZone: "America/New_York", weekStart: "sunday", yearStart: 4 });
    const user = userEvent.setup();
    render(<WorkspaceFormatPanel />);

    expect(screen.getByLabelText("Workspace time zone")).toHaveDisplayValue("The server's (UTC)");
    await user.selectOptions(screen.getByLabelText("Workspace time zone"), "America/New_York");
    await user.selectOptions(screen.getByLabelText("Weeks start on"), "sunday");
    await user.selectOptions(screen.getByLabelText("The year starts in"), "4");
    await user.click(screen.getByRole("button", { name: "Save" }));

    expect(settingsApi.saveWorkspaceSettings).toHaveBeenCalledWith({ locale: null, currency: "EUR", timeZone: "America/New_York", weekStart: "sunday", yearStart: 4 });
    await waitFor(() => expect(workspaceCalendar()).toEqual({ timeZone: "America/New_York", weekStart: "sunday", yearStart: 4 }));
    // Periods follow at once: a Saturday belongs to the week from the Sunday before; April starts the year.
    expect(periodStart("2026-10-10", "week")).toBe("2026-10-04");
    expect(periodStart("2026-03-31", "year")).toBe("2025-04-01");
    expect(quarterName("2026-04-01")).toBe("Q1 2026/27");
    expect(resolveRange({ preset: "this-year" }, new Date("2026-10-11T12:00:00Z"))).toEqual({ from: "2026-04-01", to: "2027-04-01" });
    expect(resolveRange({ preset: "last-week" }, new Date("2026-10-11T12:00:00Z"))).toEqual({ from: "2026-10-04", to: "2026-10-11" });
    setWorkspaceCalendar(null);
  });

  it("today is the workspace's day, not the browser's", () => {
    setWorkspaceCalendar({ timeZone: "Pacific/Kiritimati" });
    // 11 Oct 2026, 12:00 UTC is already 12 Oct in Kiritimati (UTC+14).
    expect(todayIso(new Date("2026-10-11T12:00:00Z"))).toBe("2026-10-12");
    expect(resolveRange({ preset: "last-7-days" }, new Date("2026-10-11T12:00:00Z"))).toEqual({ from: "2026-10-06", to: "2026-10-13" });
    setWorkspaceCalendar(null);
  });
});
