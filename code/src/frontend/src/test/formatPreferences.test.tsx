import { cleanup, render, screen, waitFor } from "@testing-library/react";
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

    expect(settingsApi.saveWorkspaceSettings).toHaveBeenCalledWith({ locale: "de-AT", currency: "EUR" });
    await waitFor(() => expect(effectiveLocale()).toBe("de-AT"));
    expect(useSetupStore.getState().info?.locale).toBe("de-AT");
  });
});
