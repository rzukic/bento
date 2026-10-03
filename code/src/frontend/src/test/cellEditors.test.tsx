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
import { parseLenientNumber } from "../features/data-explorer/components/editors/numericParsing";
import {
  formatDateDraft,
  formatTimeDraft,
  getDateTimeSeparator,
  parseDateDraft,
  parseTimeDraft,
} from "../features/data-explorer/components/editors/temporalEditing";
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
    const input = screen.getByRole("textbox");
    fireEvent.change(input, { target: { value: "42" } });
    fireEvent.keyDown(input, { key: "Enter" });
    expect(onCommit).toHaveBeenCalledWith(42);
  });

  it("accepts grouped integer input without growing the editor into a native spinner", () => {
    const { onCommit, onCancel } = makeHandlers();
    render(<IntegerCellEditor value={1} column={col({ type: "int64" })} onCommit={onCommit} onCancel={onCancel} />);
    const input = screen.getByRole("textbox");
    expect(input).toHaveAttribute("type", "text");
    fireEvent.change(input, { target: { value: "100,000" } });
    fireEvent.keyDown(input, { key: "Enter" });
    expect(onCommit).toHaveBeenCalledWith(100000);
    expect(onCancel).not.toHaveBeenCalled();
  });

  it("cancels rather than committing a non-integer", () => {
    const { onCommit, onCancel } = makeHandlers();
    render(<IntegerCellEditor value={1} column={col({ type: "int64" })} onCommit={onCommit} onCancel={onCancel} />);
    const input = screen.getByRole("textbox");
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
    const input = screen.getByRole("textbox");
    fireEvent.change(input, { target: { value: "3.14" } });
    fireEvent.keyDown(input, { key: "Enter" });
    expect(onCommit).toHaveBeenCalledWith(3.14);
  });

  it("accepts locale-style decimals with mixed separators", () => {
    const { onCommit, onCancel } = makeHandlers();
    render(<FloatCellEditor value={1} column={col({ type: "double" })} onCommit={onCommit} onCancel={onCancel} />);
    const input = screen.getByRole("textbox");
    fireEvent.change(input, { target: { value: "100.000,99" } });
    fireEvent.keyDown(input, { key: "Enter" });
    expect(onCommit).toHaveBeenCalledWith(100000.99);
    expect(onCancel).not.toHaveBeenCalled();
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
    fireEvent.keyDown(screen.getByRole("textbox"), { key: "Escape" });
    expect(onCancel).toHaveBeenCalled();
    expect(screen.getByRole("alert")).toHaveTextContent("x");
  });
});

describe("parseLenientNumber", () => {
  it("parses German-style input", () => {
    expect(parseLenientNumber("100.000,99", { locale: "de-DE" })).toBe(100000.99);
  });

  it("parses US-style input", () => {
    expect(parseLenientNumber("100,000.99", { locale: "en-US" })).toBe(100000.99);
  });

  it("ignores repeated stray separators", () => {
    expect(parseLenientNumber("1...0...0..0..0..0,99", { locale: "de-DE" })).toBe(100000.99);
  });

  it("uses locale to resolve a single ambiguous separator", () => {
    expect(parseLenientNumber("1,234", { locale: "de-DE" })).toBe(1.234);
    expect(parseLenientNumber("1,234", { locale: "en-US" })).toBe(1234);
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
    fireEvent.mouseDown(within(listbox).getByText("Yes"));
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
  it("commits the locale-text date as an ISO date on Enter", () => {
    const { onCommit, onCancel } = makeHandlers();
    render(
      <DateCellEditor
        value="2026-01-01"
        column={col({ type: "date" })}
        onCommit={onCommit}
        onCancel={onCancel}
      />,
    );
    const input = screen.getByRole("textbox", { name: "value" });
    fireEvent.change(input, { target: { value: formatDateDraft("2026-04-17") } });
    fireEvent.keyDown(input, { key: "Enter" });
    expect(onCommit).toHaveBeenCalledWith("2026-04-17");
  });

  it("syncs the visible text field when a date is chosen from the custom picker", () => {
    const { onCommit, onCancel } = makeHandlers();
    render(
      <DateCellEditor
        value="2026-04-01"
        column={col({ type: "date" })}
        onCommit={onCommit}
        onCancel={onCancel}
      />,
    );
    fireEvent.focus(screen.getByRole("textbox", { name: "value" }));
    expect(screen.getByRole("dialog", { name: "Date picker" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Pick 2026-04-17" }));
    expect(screen.getByRole("textbox", { name: "value" })).toHaveValue(
      formatDateDraft("2026-04-17"),
    );
  });

  it("lets the month header open a month picker and switch months", () => {
    const { onCommit, onCancel } = makeHandlers();
    render(
      <DateCellEditor
        value="2026-04-17"
        column={col({ type: "date" })}
        onCommit={onCommit}
        onCancel={onCancel}
      />,
    );
    fireEvent.focus(screen.getByRole("textbox", { name: "value" }));
    fireEvent.click(screen.getByRole("button", { name: "Choose month" }));
    fireEvent.click(screen.getByRole("button", { name: "Choose January" }));
    expect(screen.getByRole("button", { name: "Choose month" })).toHaveTextContent(/jan/i);
  });

  it("lets the year header open a year picker and switch years", () => {
    const { onCommit, onCancel } = makeHandlers();
    render(
      <DateCellEditor
        value="2026-04-17"
        column={col({ type: "date" })}
        onCommit={onCommit}
        onCancel={onCancel}
      />,
    );
    fireEvent.focus(screen.getByRole("textbox", { name: "value" }));
    fireEvent.click(screen.getByRole("button", { name: "Choose year" }));
    fireEvent.click(screen.getByRole("button", { name: "Choose 2027" }));
    expect(screen.getByRole("button", { name: "Choose year" })).toHaveTextContent("2027");
  });

  it("cancels on Escape and shows error", () => {
    const { onCommit, onCancel } = makeHandlers();
    render(
      <DateCellEditor
        value=""
        column={col({ type: "date" })}
        onCommit={onCommit}
        onCancel={onCancel}
        error="bad"
      />,
    );
    fireEvent.keyDown(screen.getByRole("textbox", { name: "value" }), {
      key: "Escape",
    });
    expect(onCancel).toHaveBeenCalled();
    expect(screen.getByRole("alert")).toHaveTextContent("bad");
  });
});

// ── DateTimeCellEditor ──────────────────────────────────────────────────────

describe("DateTimeCellEditor", () => {
  it("commits the visible date and time segments as a datetime string on Enter", () => {
    const { onCommit, onCancel } = makeHandlers();
    render(
      <DateTimeCellEditor
        value="2026-04-17T10:00:00Z"
        column={col({ type: "datetime" })}
        onCommit={onCommit}
        onCancel={onCancel}
      />,
    );
    const dateInput = screen.getByRole("textbox", { name: "value date" });
    const timeInput = screen.getByRole("textbox", { name: "value time" });
    fireEvent.change(dateInput, { target: { value: formatDateDraft("2026-04-17") } });
    fireEvent.change(timeInput, { target: { value: "11:30" } });
    fireEvent.keyDown(timeInput, { key: "Enter" });
    expect(onCommit).toHaveBeenCalledWith("2026-04-17T11:30");
  });

  it("updates the time segment from the custom time picker", () => {
    const { onCommit, onCancel } = makeHandlers();
    render(
      <DateTimeCellEditor
        value="2026-04-17T10:00:00Z"
        column={col({ type: "datetime" })}
        onCommit={onCommit}
        onCancel={onCancel}
      />,
    );
    fireEvent.focus(screen.getByRole("textbox", { name: "value time" }));
    expect(screen.getByRole("dialog", { name: "Time picker" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Pick 09:45" }));
    // The time reads like the grid shows it in the current locale.
    expect(screen.getByRole("textbox", { name: "value time" })).toHaveValue(formatTimeDraft("09:45"));
  });

  it("cancels on Escape and shows error", () => {
    const { onCommit, onCancel } = makeHandlers();
    render(
      <DateTimeCellEditor
        value=""
        column={col({ type: "datetime" })}
        onCommit={onCommit}
        onCancel={onCancel}
        error="bad"
      />,
    );
    fireEvent.keyDown(screen.getByRole("textbox", { name: "value date" }), {
      key: "Escape",
    });
    expect(onCancel).toHaveBeenCalled();
    expect(screen.getByRole("alert")).toHaveTextContent("bad");
  });
});

describe("temporalEditing helpers", () => {
  it("parses locale date text back into ISO", () => {
    expect(parseDateDraft(formatDateDraft("2026-04-17"))).toBe("2026-04-17");
  });

  it("drafts dates and times the way the grid displays them", () => {
    expect(formatDateDraft("2026-09-08", "de-DE")).toBe("08. Sept. 2026");
    expect(formatDateDraft("2026-09-08", "en-US")).toBe("Sep 08, 2026");
    expect(formatTimeDraft("2026-09-08T14:40", "de-DE")).toBe("14:40");
    expect(formatTimeDraft("2026-09-08T14:40", "en-US")).toBe("02:40 PM");
    expect(getDateTimeSeparator("de-DE")).toBe(", ");
  });

  it("parses written months and numeric dates", () => {
    expect(parseDateDraft("08. Sept. 2026", "de-DE")).toBe("2026-09-08");
    expect(parseDateDraft("8 Sep 2026", "de-DE")).toBe("2026-09-08");
    expect(parseDateDraft("Sep 08, 2026", "en-US")).toBe("2026-09-08");
    expect(parseDateDraft("08.09.2026", "de-DE")).toBe("2026-09-08");
    expect(parseDateDraft("08. Foo 2026", "de-DE")).toBeNull();
  });

  it("parses 12-hour times", () => {
    expect(parseTimeDraft("02:40 PM")).toBe("14:40");
    expect(parseTimeDraft("12:05 am")).toBe("00:05");
    expect(parseTimeDraft("12:05 PM")).toBe("12:05");
  });

  it("parses compact and separated time drafts", () => {
    expect(parseTimeDraft("11:30")).toBe("11:30");
    expect(parseTimeDraft("1130")).toBe("11:30");
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
