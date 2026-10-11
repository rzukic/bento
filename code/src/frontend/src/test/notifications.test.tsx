import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { NotificationChannel } from "../api/notifications";
import { NotificationsPanel } from "../features/notifications/NotificationsPanel";

const api = vi.hoisted(() => ({
  listChannels: vi.fn(),
  createChannel: vi.fn(),
  updateChannel: vi.fn(),
  deleteChannel: vi.fn(),
  testChannel: vi.fn(),
  listDeliveries: vi.fn(),
}));
vi.mock("../api/notifications", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/notifications")>()), ...api }));

const email: NotificationChannel = {
  id: 1,
  name: "My email",
  kind: "email",
  config: { to: ["me@example.com"], smtpHost: "smtp.example.com", smtpSecurity: "starttls", smtpUser: "bento@example.com" },
  target: "me@example.com",
  hasPassword: true,
  hasUrl: false,
  hasSecret: false,
  enabled: true,
  lastDelivery: null,
  createdAt: "",
  updatedAt: "",
};

beforeEach(() => {
  api.listChannels.mockResolvedValue([]);
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("notification channels", () => {
  it("adds an email channel with its SMTP account", async () => {
    api.createChannel.mockResolvedValue(email);
    const user = userEvent.setup();
    render(<NotificationsPanel />);

    expect(await screen.findByText(/No channels yet/)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Add channel" }));
    await user.type(screen.getByLabelText("Name"), "My email");
    await user.type(screen.getByLabelText("Send to"), "me@example.com, partner@example.com");
    await user.type(screen.getByLabelText("SMTP server"), "smtp.example.com");
    await user.selectOptions(screen.getByLabelText("Security"), "ssl");
    expect(screen.getByLabelText("Port")).toHaveAttribute("placeholder", "465");
    await user.type(screen.getByLabelText("User"), "bento@example.com");
    await user.type(screen.getByLabelText("Password"), "app-password");
    await user.click(screen.getAllByRole("button", { name: "Add channel" }).at(-1)!);

    await waitFor(() => expect(api.createChannel).toHaveBeenCalledTimes(1));
    expect(api.createChannel).toHaveBeenCalledWith("My email", "email", {
      to: ["me@example.com", "partner@example.com"],
      from: null,
      smtpHost: "smtp.example.com",
      smtpPort: null,
      smtpSecurity: "ssl",
      smtpUser: "bento@example.com",
      smtpPassword: "app-password",
    });
  });

  it("sends a test and shows how it went; a stored password is kept when left empty", async () => {
    api.listChannels.mockResolvedValue([email]);
    api.testChannel.mockResolvedValue({ id: 5, event: "test", title: "Test from Bento", status: "failed", error: "smtp.example.com did not accept the user name or password: 535", durationMs: 120, createdAt: "" });
    api.updateChannel.mockResolvedValue(email);
    const user = userEvent.setup();
    render(<NotificationsPanel />);

    await user.click(await screen.findByRole("button", { name: "Send test" }));
    expect(await screen.findByText(/Test failed: smtp.example.com did not accept/)).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Edit" }));
    expect(screen.getByLabelText("Password")).toHaveValue("");
    expect(screen.getByLabelText("Password")).toHaveAttribute("placeholder", "Stored — leave empty to keep");
    await user.click(screen.getByRole("button", { name: "Save channel" }));
    await waitFor(() => expect(api.updateChannel).toHaveBeenCalledTimes(1));
    expect(api.updateChannel.mock.calls[0][1].config).not.toHaveProperty("smtpPassword");
  });
});
