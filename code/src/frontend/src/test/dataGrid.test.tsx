import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { DataGrid } from "../features/data-explorer/components/DataGrid";
import type { ModelTable } from "../types/api";

afterEach(() => cleanup());

const sampleTable: ModelTable = {
  name: "items",
  displayName: "Items",
  columns: [
    { name: "id", type: "int64", primaryKey: true },
    { name: "title", type: "string", nullable: true },
    { name: "qty", type: "int64", nullable: true },
  ],
};

const sampleRows = [
  { id: 1, title: "Alpha", qty: 5 },
  { id: 2, title: "Beta", qty: 8 },
];

const junctionTable: ModelTable = {
  name: "_jt_items_tags",
  columns: [
    { name: "item_id", type: "int64", primaryKey: true, references: "items" },
    { name: "tag_id", type: "int64", primaryKey: true, references: "tags" },
  ],
};

const junctionRows = [{ item_id: 1, tag_id: 2 }];

function getCellByText(text: string): HTMLElement {
  const node = screen.getByText(text);
  // climb to the td
  let el: HTMLElement | null = node;
  while (el && el.tagName !== "TD") el = el.parentElement;
  if (!el) throw new Error(`Could not find td for text "${text}"`);
  return el;
}

function getCellInner(text: string): HTMLElement {
  return getCellByText(text).querySelector(".data-grid__cell") as HTMLElement;
}

describe("DataGrid — inline edit wiring (TASK-092)", () => {
  it("shows the PK column name and PK badge in the table header", () => {
    render(<DataGrid table={sampleTable} rows={sampleRows} page={1} perPage={25} />);

    expect(screen.getByText("id")).toBeInTheDocument();
    expect(screen.getByText("PK")).toBeInTheDocument();
  });

  it("prefers the display name for PK column headers", () => {
    render(
      <DataGrid
        table={{
          ...sampleTable,
          columns: [
            { ...sampleTable.columns[0], displayName: "Order ID" },
            ...sampleTable.columns.slice(1),
          ],
        }}
        rows={sampleRows}
        page={1}
        perPage={25}
      />,
    );

    expect(screen.getByText("Order ID")).toBeInTheDocument();
    expect(screen.getByText("PK")).toBeInTheDocument();
  });

  it("clicking a writable cell calls onStartEdit with that row + column", () => {
    const onStartEdit = vi.fn();
    render(
      <DataGrid
        table={sampleTable}
        rows={sampleRows}
        page={1}
        perPage={25}
        onStartEdit={onStartEdit}
      />,
    );
    fireEvent.click(getCellByText("Alpha"));
    expect(onStartEdit).toHaveBeenCalledTimes(1);
    expect(onStartEdit.mock.calls[0]![0]).toMatchObject({ id: 1 });
    expect(onStartEdit.mock.calls[0]![1].name).toBe("title");
  });

  it("Enter on a focused writable cell starts edit; F2 also works", () => {
    const onStartEdit = vi.fn();
    render(
      <DataGrid
        table={sampleTable}
        rows={sampleRows}
        page={1}
        perPage={25}
        onStartEdit={onStartEdit}
      />,
    );
    const cell = getCellInner("Alpha");
    fireEvent.keyDown(cell, { key: "Enter" });
    expect(onStartEdit).toHaveBeenCalledTimes(1);
    fireEvent.keyDown(cell, { key: "F2" });
    expect(onStartEdit).toHaveBeenCalledTimes(2);
  });

  it("PK columns are not editable: no tabIndex, no onStartEdit on click", () => {
    const onStartEdit = vi.fn();
    render(
      <DataGrid
        table={sampleTable}
        rows={sampleRows}
        page={1}
        perPage={25}
        onStartEdit={onStartEdit}
      />,
    );
    const pkCell = getCellInner("1");
    expect(pkCell.getAttribute("tabindex")).toBeNull();
    fireEvent.click(getCellByText("1"));
    expect(onStartEdit).not.toHaveBeenCalled();
  });

  it("PK reference columns remain editable for junction-table foreign keys", () => {
    const onStartEdit = vi.fn();
    render(
      <DataGrid
        table={junctionTable}
        rows={junctionRows}
        page={1}
        perPage={25}
        onStartEdit={onStartEdit}
      />,
    );

    const fkCell = getCellInner("1");
    expect(fkCell.getAttribute("tabindex")).toBe("0");
    fireEvent.click(getCellByText("1"));
    expect(onStartEdit).toHaveBeenCalledTimes(1);
    expect(onStartEdit.mock.calls[0]![1].name).toBe("item_id");
  });

  it("renders the matching editor when editingCell points at a cell", () => {
    render(
      <DataGrid
        table={sampleTable}
        rows={sampleRows}
        page={1}
        perPage={25}
        editingCell={{ rowId: "1", columnName: "title" }}
        onStartEdit={vi.fn()}
        onCancelEdit={vi.fn()}
        onCommitEdit={vi.fn()}
      />,
    );
    const input = screen.getByRole("textbox", { name: "title" });
    expect(input).toBeInTheDocument();
    expect((input as HTMLInputElement).value).toBe("Alpha");
  });

  it("Escape inside the editor calls onCancelEdit", () => {
    const onCancelEdit = vi.fn();
    render(
      <DataGrid
        table={sampleTable}
        rows={sampleRows}
        page={1}
        perPage={25}
        editingCell={{ rowId: "1", columnName: "title" }}
        onStartEdit={vi.fn()}
        onCancelEdit={onCancelEdit}
        onCommitEdit={vi.fn()}
      />,
    );
    fireEvent.keyDown(screen.getByRole("textbox", { name: "title" }), {
      key: "Escape",
    });
    expect(onCancelEdit).toHaveBeenCalled();
  });

  it("Tab inside the editor advances to the next editable cell", () => {
    const onStartEdit = vi.fn();
    render(
      <DataGrid
        table={sampleTable}
        rows={sampleRows}
        page={1}
        perPage={25}
        editingCell={{ rowId: "1", columnName: "title" }}
        onStartEdit={onStartEdit}
        onCancelEdit={vi.fn()}
        onCommitEdit={vi.fn()}
      />,
    );
    const input = screen.getByRole("textbox", { name: "title" });
    fireEvent.keyDown(input, { key: "Tab" });
    expect(onStartEdit).toHaveBeenCalledTimes(1);
    const [nextRow, nextCol] = onStartEdit.mock.calls[0]!;
    expect(nextRow).toMatchObject({ id: 1 });
    expect(nextCol.name).toBe("qty");
  });

  it("Shift+Tab from the first editable cell wraps to the last editable cell of the previous row", () => {
    const onStartEdit = vi.fn();
    render(
      <DataGrid
        table={sampleTable}
        rows={sampleRows}
        page={1}
        perPage={25}
        editingCell={{ rowId: "2", columnName: "title" }}
        onStartEdit={onStartEdit}
        onCancelEdit={vi.fn()}
        onCommitEdit={vi.fn()}
      />,
    );
    const input = screen.getByRole("textbox", { name: "title" });
    fireEvent.keyDown(input, { key: "Tab", shiftKey: true });
    expect(onStartEdit).toHaveBeenCalledTimes(1);
    const [prevRow, prevCol] = onStartEdit.mock.calls[0]!;
    expect(prevRow).toMatchObject({ id: 1 });
    expect(prevCol.name).toBe("qty");
  });

  it("renders a has-error class on cells with a pending cellError when not editing", () => {
    render(
      <DataGrid
        table={sampleTable}
        rows={sampleRows}
        page={1}
        perPage={25}
        cellErrors={{ "1": { title: "required" } }}
        onStartEdit={vi.fn()}
      />,
    );
    const cell = getCellByText("Alpha");
    expect(cell.className).toContain("data-grid__column--has-error");
  });

  it("renders the bottom add-row affordance and calls onAddRow from button + Ctrl/Cmd+Enter", () => {
    const onAddRow = vi.fn();
    render(
      <DataGrid
        table={sampleTable}
        rows={sampleRows}
        page={1}
        perPage={25}
        onAddRow={onAddRow}
        onStartEdit={vi.fn()}
      />,
    );

    fireEvent.click(screen.getAllByRole("button", { name: /add row/i })[0]!);
    expect(onAddRow).toHaveBeenCalledTimes(1);

    const cell = getCellInner("Alpha");
    cell.focus();
    fireEvent.keyDown(cell, { key: "Enter", ctrlKey: true });
    expect(onAddRow).toHaveBeenCalledTimes(2);
  });

  it("Ctrl/Cmd+Delete delegates row deletion for the focused row", () => {
    const onDeleteRowsShortcut = vi.fn();
    render(
      <DataGrid
        table={sampleTable}
        rows={sampleRows}
        page={1}
        perPage={25}
        onDeleteRowsShortcut={onDeleteRowsShortcut}
        onStartEdit={vi.fn()}
      />,
    );

    const cell = getCellInner("Beta");
    fireEvent.focus(cell);
    fireEvent.keyDown(cell, { key: "Delete", ctrlKey: true });
    expect(onDeleteRowsShortcut).toHaveBeenCalledWith("2");
  });
});
