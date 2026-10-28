import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { QueryTableBanner, QueryTableDialog } from "../features/data-explorer/QueryTable";

const api = vi.hoisted(() => ({ listSavedQueries: vi.fn(), saveQueryAsTable: vi.fn(), getQueryTable: vi.fn(), refreshQueryTable: vi.fn() }));
vi.mock("../api/query", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/query")>()), ...api }));

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("saved queries as tables", () => {
  it("makes a table from a saved query with a name from it", async () => {
    api.listSavedQueries.mockResolvedValue([{ id: 3, name: "Monthly spending", source: "transactions", isShared: false, isOwner: true, query: { source: "transactions" }, createdAt: "", updatedAt: "" }]);
    api.saveQueryAsTable.mockResolvedValue({ table: "monthly_spending", queryId: 3, refreshedAt: null, rows: 2, current: true, error: null });
    render(
      <MemoryRouter>
        <QueryTableDialog onClose={() => undefined} />
      </MemoryRouter>,
    );
    await userEvent.selectOptions(await screen.findByLabelText("Saved query"), "3");
    expect(screen.getByLabelText("Table name")).toHaveValue("monthly_spending");
    await userEvent.click(screen.getByRole("button", { name: "Make table" }));
    await waitFor(() => expect(api.saveQueryAsTable).toHaveBeenCalledWith(3, "monthly_spending"));
  });

  it("tells where a table's rows come from and fills it on request", async () => {
    api.getQueryTable.mockResolvedValue({ table: "monthly_spending", queryId: 3, refreshedAt: "2026-10-21T10:00:00Z", rows: 2, current: true, error: null });
    api.refreshQueryTable.mockResolvedValue({ table: "monthly_spending", queryId: 3, refreshedAt: "2026-10-21T11:00:00Z", rows: 3, current: true, error: null });
    render(<QueryTableBanner table={{ name: "monthly_spending", columns: [], fromQuery: 3 }} />);
    expect(await screen.findByText(/2 rows, filled/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Refresh now" }));
    expect(await screen.findByText(/3 rows, filled/)).toBeInTheDocument();
  });
});
