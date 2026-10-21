import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { apiClient, setShareToken } from "../api/client";
import type { Dashboard } from "../api/dashboards";
import { ShareDialog } from "../features/dashboards/ShareDialog";
import { SharedPage } from "../features/sharing/SharedPage";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";

const api = vi.hoisted(() => ({ listShares: vi.fn(), createShare: vi.fn(), revokeShare: vi.fn(), getShareInfo: vi.fn() }));
vi.mock("../api/shares", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/shares")>()), ...api }));

const dashboard: Dashboard = {
  id: 7,
  name: "Spending",
  isShared: false,
  isOwner: true,
  dashboard: { widgets: [], filters: [{ column: "category" }] },
  createdAt: "",
  updatedAt: "",
};

beforeEach(() => {
  resetAuthStoreForTest();
  api.listShares.mockResolvedValue([]);
});

afterEach(() => {
  cleanup();
  setShareToken(null);
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe("share links", () => {
  it("sends a link's visitor's requests with the link, never with a session", async () => {
    useAuthStore.setState({ status: "authenticated", user: { id: 1, username: "a", role: "admin" }, accessToken: "session-token", expiresAt: Date.now() + 60_000, loginInFlight: false, error: null });
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(new Response("{}", { headers: { "Content-Type": "application/json" } })));
    vi.stubGlobal("fetch", fetchMock);
    setShareToken("bento_s_abc");
    await apiClient.get("/share");
    const headers = fetchMock.mock.calls[0][1].headers as Headers;
    expect(headers.get("X-Bento-Share")).toBe("bento_s_abc");
    expect(headers.has("Authorization")).toBe(false);
  });

  it("makes a link with a fixed filter and shows its address once", async () => {
    api.createShare.mockResolvedValue({
      link: { id: 1, name: "Advisor", prefix: "bento_s_abc", status: "active", createdBy: "a", createdAt: "", expiresAt: null, lastUsedAt: null, views: 0, filters: { category: ["food"] }, embed: true },
      url: "https://bento.example/s/bento_s_abcdef",
      embed: '<iframe src="https://bento.example/s/bento_s_abcdef?embed=1"></iframe>',
    });
    render(<ShareDialog dashboard={dashboard} onClose={() => undefined} />);
    expect(await screen.findByText("No links yet.")).toBeInTheDocument();
    await userEvent.type(screen.getByPlaceholderText("e.g. For the tax advisor"), "Advisor");
    await userEvent.type(screen.getByLabelText("Only category"), "food, rent");
    await userEvent.click(screen.getByLabelText("May be embedded in other sites"));
    await userEvent.click(screen.getByRole("button", { name: "Make link" }));
    await waitFor(() =>
      expect(api.createShare).toHaveBeenCalledWith(7, { name: "Advisor", expiresInDays: 30, filters: { category: ["food", "rent"] }, embed: true }),
    );
    expect(await screen.findByLabelText("Share link")).toHaveTextContent("https://bento.example/s/bento_s_abcdef");
    expect(screen.getByLabelText("Embed code")).toHaveTextContent("iframe");
  });

  it("tells a visitor when the link is gone", async () => {
    api.getShareInfo.mockRejectedValue(new Error("404"));
    render(
      <MemoryRouter initialEntries={["/s/bento_s_gone"]}>
        <Routes>
          <Route path="/s/:token" element={<SharedPage />} />
        </Routes>
      </MemoryRouter>,
    );
    expect(await screen.findByText(/does not exist, has expired or was revoked/)).toBeInTheDocument();
  });
});
