import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  BooleanCellEditor,
  CurrencyCellEditor,
  DateCellEditor,
  DateTimeCellEditor,
  EnumCellEditor,
  FloatCellEditor,
  IntegerCellEditor,
  JsonCellEditor,
  ReferenceCellEditor,
  TextCellEditor,
} from "../features/data-explorer/components/editors";
import { resetSchemaStoreForTest, useSchemaStore } from "../stores/schemaStore";
import type { ModelColumn, ModelSnapshot } from "../types/api";

afterEach(() => cleanup());

function col(overrides: Partial<ModelColumn> = {}): ModelColumn {
  return { name: "value", type: "string", nullable: true, ...overrides };
}

function makeHandlers() {
  return { onCommit: vi.fn(), onCancel: vi.fn() };
}

// ── TextCellEditor ──────────────────────────────────────────────────────────

describe("TextCellEditor", () => {
  it("commits the new value on Enter", () => {
    const { onCommit, onCancel } = makeHandlers();
    render(<TextCellEditor value="hi" column={col()} onCommit={onCommit} onCancel={onCancel} />);
    const input = screen.getByRole("textbox");
    fireEvent.change(input, { target: { value: "world" } });
    fireEvent.keyDown(input, { key: "Enter" });
    expect(onCommit).toHaveBeenCalledWith("world");
  });

  it("cancels on Escape", () => {
    const { onCommit, onCancel } = makeHandlers();
    render(<TextCellEditor value="hi" column={col()} onCommit={onCommit} onCancel={onCancel} />);
    fireEvent.keyDown(screen.getByRole("textbox"), { key: "Escape" });
    expect(onCancel).toHaveBeenCalled();
    expect(onCommit).not.toHaveBeenCalled();
  });

  it("displays the error message", () => {
    const { onCommit, onCancel } = makeHandlers();
    render(
      <TextCellEditor
        value=""
        column={col()}
        onCommit={onCommit}
        onCancel={onCancel}
        error="too long"
      />,
    );
    expect(screen.getByRole("alert")).toHaveTextContent("too long");
  });
});

// ── IntegerCellEditor ───────────────────────────────────────────────────────

describe("IntegerCellEditor", () => {
  it("commits a parsed integer on Enter", () => {
    const { onCommit, onCancel } = makeHandlers();
    render(<IntegerCellEditor value={1} column={col({ type: "int64" })} onCommit={onCommit} onCancel={onCancel} />);
    const input = screen.getByRole("spinbutton");
    fireEvent.change(input, { target: { value: "42" } });
    fireEvent.keyDown(input, { key: "Enter" });
    expect(onCommit).toHaveBeenCalledWith(42);
  });

  it("cancels rather than committing a non-integer", () => {
    const { onCommit, onCancel } = makeHandlers();
    render(<IntegerCellEditor value={1} column={col({ type: "int64" })} onCommit={onCommit} onCancel={onCancel} />);
    const input = screen.getByRole("spinbutton");
    fireEvent.change(input, { target: { value: "1.5" } });
    fireEvent.keyDown(input, { key: "Enter" });
    expect(onCommit).not.toHaveBeenCalled();
    expect(onCancel).toHaveBeenCalled();
  });

  it("renders error state", () => {
    const { onCommit, onCancel } = makeHandlers();
    render(
      <IntegerCellEditor
        value={1}
        column={col({ type: "int64" })}
        onCommit={onCommit}
        onCancel={onCancel}
        error="bad"
      />,
    );
    expect(screen.getByRole("alert")).toHaveTextContent("bad");
  });
});

// ── FloatCellEditor ─────────────────────────────────────────────────────────

describe("FloatCellEditor", () => {
  it("commits a parsed float on Enter", () => {
    const { onCommit, onCancel } = makeHandlers();
    render(<FloatCellEditor value={1} column={col({ type: "double" })} onCommit={onCommit} onCancel={onCancel} />);
    const input = screen.getByRole("spinbutton");
    fireEvent.change(input, { target: { value: "3.14" } });
    fireEvent.keyDown(input, { key: "Enter" });
    expect(onCommit).toHaveBeenCalledWith(3.14);
  });

  it("cancels on Escape and surfaces error", () => {
    const { onCommit, onCancel } = makeHandlers();
    render(
      <FloatCellEditor
        value={1}
        column={col({ type: "double" })}
        onCommit={onCommit}
        onCancel={onCancel}
        error="x"
      />,
    );
    fireEvent.keyDown(screen.getByRole("spinbutton"), { key: "Escape" });
    expect(onCancel).toHaveBeenCalled();
    expect(screen.getByRole("alert")).toHaveTextContent("x");
  });
});

// ── CurrencyCellEditor ──────────────────────────────────────────────────────

describe("CurrencyCellEditor", () => {
  it("commits a numeric value parsed from a formatted string", () => {
    const { onCommit, onCancel } = makeHandlers();
    render(
      <CurrencyCellEditor
        value={null}
        column={col({ type: "currency" })}
        onCommit={onCommit}
        onCancel={onCancel}
      />,
    );
    const input = screen.getByRole("textbox");
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: "$1,200.50" } });
    fireEvent.keyDown(input, { key: "Enter" });
    expect(onCommit).toHaveBeenCalledWith(1200.5);
  });

  it("cancels on Escape and shows error", () => {
    const { onCommit, onCancel } = makeHandlers();
    render(
      <CurrencyCellEditor
        value={1}
        column={col({ type: "currency" })}
        onCommit={onCommit}
        onCancel={onCancel}
        error="oops"
      />,
    );
    fireEvent.keyDown(screen.getByRole("textbox"), { key: "Escape" });
    expect(onCancel).toHaveBeenCalled();
    expect(screen.getByRole("alert")).toHaveTextContent("oops");
  });
});

// ── BooleanCellEditor ───────────────────────────────────────────────────────

describe("BooleanCellEditor", () => {
  it("commits the chosen value from the listbox", async () => {
    const { onCommit, onCancel } = makeHandlers();
    render(
      <BooleanCellEditor
        value={false}
        column={col({ type: "boolean" })}
        onCommit={onCommit}
        onCancel={onCancel}
      />,
    );
    const input = screen.getByRole("combobox");
    fireEvent.focus(input);
    const listbox = await screen.findByRole("listbox");
    fireEvent.mouseDown(within(listbox).getByText("True"));
    expect(onCommit).toHaveBeenCalledWith(true);
  });

  it("cancels on Escape and shows error", () => {
    const { onCommit, onCancel } = makeHandlers();
    render(
      <BooleanCellEditor
        value={false}
        column={col({ type: "boolean" })}
        onCommit={onCommit}
        onCancel={onCancel}
        error="bad"
      />,
    );
    fireEvent.keyDown(screen.getByRole("combobox"), { key: "Escape" });
    expect(onCancel).toHaveBeenCalled();
    expect(screen.getByRole("alert")).toHaveTextContent("bad");
  });
});

// ── DateCellEditor ──────────────────────────────────────────────────────────

describe("DateCellEditor", () => {
  it("commits the date string on Enter", () => {
    const { onCommit, onCancel } = makeHandlers();
    const { container } = render(
      <DateCellEditor
        value="2026-01-01"
        column={col({ type: "date" })}
        onCommit={onCommit}
        onCancel={onCancel}
      />,
    );
    const input = container.querySelector("input")!;
    fireEvent.change(input, { target: { value: "2026-04-17" } });
    fireEvent.keyDown(input, { key: "Enter" });
    expect(onCommit).toHaveBeenCalledWith("2026-04-17");
  });

  it("cancels on Escape and shows error", () => {
    const { onCommit, onCancel } = makeHandlers();
    const { container } = render(
      <DateCellEditor
        value=""
        column={col({ type: "date" })}
        onCommit={onCommit}
        onCancel={onCancel}
        error="bad"
      />,
    );
    fireEvent.keyDown(container.querySelector("input")!, { key: "Escape" });
    expect(onCancel).toHaveBeenCalled();
    expect(screen.getByRole("alert")).toHaveTextContent("bad");
  });
});

// ── DateTimeCellEditor ──────────────────────────────────────────────────────

describe("DateTimeCellEditor", () => {
  it("commits the datetime-local string on Enter", () => {
    const { onCommit, onCancel } = makeHandlers();
    const { container } = render(
      <DateTimeCellEditor
        value="2026-04-17T10:00:00Z"
        column={col({ type: "datetime" })}
        onCommit={onCommit}
        onCancel={onCancel}
      />,
    );
    const input = container.querySelector("input")!;
    fireEvent.change(input, { target: { value: "2026-04-17T11:30" } });
    fireEvent.keyDown(input, { key: "Enter" });
    expect(onCommit).toHaveBeenCalledWith("2026-04-17T11:30");
  });

  it("cancels on Escape and shows error", () => {
    const { onCommit, onCancel } = makeHandlers();
    const { container } = render(
      <DateTimeCellEditor
        value=""
        column={col({ type: "datetime" })}
        onCommit={onCommit}
        onCancel={onCancel}
        error="bad"
      />,
    );
    fireEvent.keyDown(container.querySelector("input")!, { key: "Escape" });
    expect(onCancel).toHaveBeenCalled();
    expect(screen.getByRole("alert")).toHaveTextContent("bad");
  });
});

// ── EnumCellEditor ──────────────────────────────────────────────────────────

describe("EnumCellEditor", () => {
  const enumColumn = col({ type: "enum", values: ["pending", "shipped", "delivered"] });

  it("commits the chosen option from the listbox", async () => {
    const { onCommit, onCancel } = makeHandlers();
    render(
      <EnumCellEditor
        value="pending"
        column={enumColumn}
        onCommit={onCommit}
        onCancel={onCancel}
      />,
    );
    const input = screen.getByRole("combobox");
    fireEvent.focus(input);
    const listbox = await screen.findByRole("listbox");
    fireEvent.mouseDown(within(listbox).getByText("shipped"));
    expect(onCommit).toHaveBeenCalledWith("shipped");
  });

  it("cancels on Escape and shows error", () => {
    const { onCommit, onCancel } = makeHandlers();
    render(
      <EnumCellEditor
        value="pending"
        column={enumColumn}
        onCommit={onCommit}
        onCancel={onCancel}
        error="bad"
      />,
    );
    fireEvent.keyDown(screen.getByRole("combobox"), { key: "Escape" });
    expect(onCancel).toHaveBeenCalled();
    expect(screen.getByRole("alert")).toHaveTextContent("bad");
  });
});

// ── JsonCellEditor ──────────────────────────────────────────────────────────

describe("JsonCellEditor", () => {
  it("commits parsed JSON on blur", () => {
    const { onCommit, onCancel } = makeHandlers();
    render(
      <JsonCellEditor
        value={{ a: 1 }}
        column={col({ type: "json" })}
        onCommit={onCommit}
        onCancel={onCancel}
      />,
    );
    const input = screen.getByRole("textbox");
    fireEvent.change(input, { target: { value: '{"b":2}' } });
    fireEvent.blur(input);
    expect(onCommit).toHaveBeenCalledWith({ b: 2 });
  });

  it("cancels on Escape", () => {
    const { onCommit, onCancel } = makeHandlers();
    render(
      <JsonCellEditor
        value={null}
        column={col({ type: "json" })}
        onCommit={onCommit}
        onCancel={onCancel}
      />,
    );
    fireEvent.keyDown(screen.getByRole("textbox"), { key: "Escape" });
    expect(onCancel).toHaveBeenCalled();
    expect(onCommit).not.toHaveBeenCalled();
  });

  it("surfaces a parse error and does not commit", () => {
    const { onCommit, onCancel } = makeHandlers();
    render(
      <JsonCellEditor
        value={null}
        column={col({ type: "json" })}
        onCommit={onCommit}
        onCancel={onCancel}
      />,
    );
    const input = screen.getByRole("textbox");
    fireEvent.change(input, { target: { value: "{not json" } });
    fireEvent.blur(input);
    expect(onCommit).not.toHaveBeenCalled();
    expect(screen.getByRole("alert")).toBeInTheDocument();
  });
});

// ── ReferenceCellEditor ─────────────────────────────────────────────────────

describe("ReferenceCellEditor", () => {
  const fkColumn: ModelColumn = {
    name: "customer_id",
    type: "relation",
    nullable: true,
    references: { table: "customers", column: "id" },
  };

  const snapshot: ModelSnapshot = {
    version: 1,
    tables: [
      {
        name: "customers",
        columns: [
          { name: "id", type: "int64", primaryKey: true },
          { name: "name", type: "string" },
        ],
      },
    ],
    relations: [],
  };

  const candidates = [
    { id: 7, name: "Acme Corp" },
    { id: 8, name: "Beta Industries" },
  ];

  beforeEach(() => {
    resetSchemaStoreForTest();
    useSchemaStore.setState({
      status: "ready",
      snapshot,
      error: null,
      lastLoadedAt: Date.now(),
      inFlight: null,
    });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function stubFetch(rows: typeof candidates = candidates) {
    const mock = vi.fn(async () =>
      new Response(
        JSON.stringify({
          data: rows,
          meta: { page: 1, per_page: 20, total: rows.length, total_pages: 1 },
        }),
        { status: 200, headers: { "Content-Type": "application/json" } },
      ),
    );
    vi.stubGlobal("fetch", mock);
    return mock;
  }

  it("opens the listbox, fetches candidates after the debounce, and commits the chosen row's PK", async () => {
    const fetchMock = stubFetch();
    const { onCommit, onCancel } = makeHandlers();
    render(
      <ReferenceCellEditor
        value={null}
        column={fkColumn}
        onCommit={onCommit}
        onCancel={onCancel}
      />,
    );
    const input = screen.getByRole("combobox");
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: "ac" } });

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalled();
    });
    const lastCall = fetchMock.mock.calls.at(-1) as unknown[] | undefined;
    const lastUrl = String(lastCall?.[0] ?? "");
    expect(lastUrl).toContain("/data/customers");
    expect(lastUrl).toContain("search=ac");

    const listbox = await screen.findByRole("listbox");
    fireEvent.mouseDown(within(listbox).getByText("Acme Corp"));
    expect(onCommit).toHaveBeenCalledWith(7);
  });

  it("clears the value with Backspace on an empty query when the column is nullable", () => {
    stubFetch();
    const { onCommit, onCancel } = makeHandlers();
    render(
      <ReferenceCellEditor
        value={7}
        column={fkColumn}
        onCommit={onCommit}
        onCancel={onCancel}
      />,
    );
    const input = screen.getByRole("combobox");
    fireEvent.focus(input);
    fireEvent.keyDown(input, { key: "Backspace" });
    expect(onCommit).toHaveBeenCalledWith(null);
  });

  it("cancels on Escape and surfaces error state", () => {
    stubFetch();
    const { onCommit, onCancel } = makeHandlers();
    render(
      <ReferenceCellEditor
        value={null}
        column={fkColumn}
        onCommit={onCommit}
        onCancel={onCancel}
        error="bad fk"
      />,
    );
    fireEvent.keyDown(screen.getByRole("combobox"), { key: "Escape" });
    expect(onCancel).toHaveBeenCalled();
    expect(screen.getByRole("alert")).toHaveTextContent("bad fk");
  });

  it("commits the highlighted row when the user presses Enter", async () => {
    const fetchMock = stubFetch();
    const { onCommit, onCancel } = makeHandlers();
    render(
      <ReferenceCellEditor
        value={null}
        column={fkColumn}
        onCommit={onCommit}
        onCancel={onCancel}
      />,
    );
    const input = screen.getByRole("combobox");
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: "b" } });
    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    await waitFor(() =>
      expect(screen.getAllByRole("option")).toHaveLength(candidates.length),
    );

    // Highlight the second option, then commit.
    fireEvent.keyDown(input, { key: "ArrowDown" });
    fireEvent.keyDown(input, { key: "ArrowDown" });
    fireEvent.keyDown(input, { key: "Enter" });
    expect(onCommit).toHaveBeenCalledWith(8);
  });
});
