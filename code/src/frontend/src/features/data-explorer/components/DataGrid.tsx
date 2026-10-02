import {
  Fragment,
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
  type KeyboardEvent as ReactKeyboardEvent,
  type MouseEvent as ReactMouseEvent,
} from "react";
import { createPortal } from "react-dom";
import { Icon } from "../../../components/common/Icon";
import type { ModelColumn, ModelTable } from "../../../types/api";
import { renderCellValue } from "./cellValue";
import {
  BooleanCellEditor,
  DateCellEditor,
  DateTimeCellEditor,
  EnumCellEditor,
  FloatCellEditor,
  IntegerCellEditor,
  JsonCellEditor,
  ReferenceCellEditor,
  TextCellEditor,
} from "./editors";
import { lookupReferenceLabel, useReferenceLabels } from "./useReferenceLabels";

type EditingCell = { rowId: string; columnName: string };

type DataGridProps = {
  table: ModelTable;
  rows: Record<string, unknown>[];
  page: number;
  perPage: number;
  pendingNewRowIds?: string[];
  rowErrors?: Record<string, string>;
  onRequestFilterColumn?: (column: ModelColumn) => void;
  onRequestSortColumn?: (column: ModelColumn) => void;
  onRequestHideColumn?: (column: ModelColumn) => void;
  onAddRow?: () => void;
  onDeleteRowsShortcut?: (focusedRowId: string | null) => void;
  onOpenRecord?: (row: Record<string, unknown>) => void;
  onNavigateReference?: (
    column: ModelColumn,
    value: unknown,
    row: Record<string, unknown>
  ) => void;
  selectedRowIds?: string[];
  onToggleRowSelection?: (row: Record<string, unknown>) => void;
  onToggleSelectAllRows?: () => void;
  onSetSelectedRowIds?: (rowIds: string[]) => void;
  onClearSelection?: () => void;
  selectionMenuItems?: Array<{
    id: string;
    label: string;
    disabled?: boolean;
    onSelect: () => void;
  }>;
  // ── Inline edit mode (TASK-092) ──
  editingCell?: EditingCell | null;
  cellErrors?: Record<string, Record<string, string>>;
  onStartEdit?: (row: Record<string, unknown>, column: ModelColumn) => void;
  onCancelEdit?: () => void;
  onCommitEdit?: (
    row: Record<string, unknown>,
    column: ModelColumn,
    nextValue: unknown
  ) => void;
};

function isColumnEditable(column: ModelColumn): boolean {
  if (column.type === "computed") return false;
  if (column.references || column.type === "relation") return true;
  return !column.primaryKey;
}

function isPrimarySelectionColumn(column: ModelColumn): boolean {
  return Boolean(column.primaryKey) && !column.references && column.type !== "relation";
}

function pickEditorForColumn(column: ModelColumn) {
  if (column.references || column.type === "relation") return ReferenceCellEditor;
  switch (column.type) {
    case "boolean":
      return BooleanCellEditor;
    case "date":
      return DateCellEditor;
    case "datetime":
      return DateTimeCellEditor;
    case "enum":
      return EnumCellEditor;
    case "json":
      return JsonCellEditor;
    case "double":
    case "float":
      return FloatCellEditor;
    case "int64":
    case "integer":
    case "number":
      return IntegerCellEditor;
    default:
      return TextCellEditor;
  }
}

function getColumnTypeIconName(column: ModelColumn) {
  if (column.primaryKey) return "number" as const;
  if (column.references || column.type === "relation") return "link" as const;

  switch (column.type) {
    case "boolean":
      return "toggle-left" as const;
    case "date":
      return "calendar" as const;
    case "datetime":
      return "clock" as const;
    case "json":
      return "braces" as const;
    case "enum":
      return "list" as const;
    case "string":
    case "text":
      return "type" as const;
    case "double":
    case "int64":
    case "integer":
    case "int":
    case "bigint":
    case "float":
    case "number":
    case "decimal":
    case "numeric":
    case "currency":
      return "number" as const;
    default:
      return "type" as const;
  }
}

function getColumnTypeLabel(column: ModelColumn) {
  if (column.references || column.type === "relation") {
    return "Reference";
  }

  switch (column.type) {
    case "boolean":
      return "Boolean";
    case "date":
      return "Date";
    case "datetime":
      return "Date and time";
    case "json":
      return "JSON";
    case "enum":
      return "Choice";
    case "currency":
      return "Currency";
    case "double":
    case "int64":
    case "integer":
    case "int":
    case "bigint":
    case "float":
    case "number":
    case "decimal":
    case "numeric":
      return "Number";
    default:
      return "Text";
  }
}


export function DataGrid({
  table,
  rows,
  page: _page,
  perPage: _perPage,
  pendingNewRowIds = [],
  rowErrors = {},
  onRequestFilterColumn,
  onRequestSortColumn,
  onRequestHideColumn,
  onAddRow,
  onDeleteRowsShortcut,
  onOpenRecord,
  onNavigateReference,
  selectedRowIds = [],
  onToggleRowSelection,
  onToggleSelectAllRows,
  onSetSelectedRowIds,
  onClearSelection,
  selectionMenuItems = [],
  editingCell = null,
  cellErrors = {},
  onStartEdit,
  onCancelEdit,
  onCommitEdit,
}: DataGridProps) {
  const referenceLabels = useReferenceLabels(table, rows);
  const pendingNewRowIdSet = new Set(pendingNewRowIds);
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const [xOverflow, setXOverflow] = useState(false);

  useEffect(() => {
    const el = scrollRef.current;
    if (!el) return;
    const check = () => setXOverflow(el.scrollWidth > el.clientWidth);
    check();
    if (typeof ResizeObserver === "undefined") return;
    const ro = new ResizeObserver(check);
    ro.observe(el);
    const tbl = el.querySelector("table");
    if (tbl) ro.observe(tbl);
    return () => ro.disconnect();
  }, [rows.length, table.columns.length]);
  const editableColumnIndexes = table.columns
    .map((column, index) => ({ column, index }))
    .filter(({ column }) => isColumnEditable(column));
  const [focusedRowId, setFocusedRowId] = useState<string | null>(null);

  function findNextEditableCell(
    rowIndex: number,
    columnIndex: number,
    direction: 1 | -1,
  ): { rowIndex: number; columnIndex: number } | null {
    if (editableColumnIndexes.length === 0 || rows.length === 0) return null;
    const positions = editableColumnIndexes.map(({ index }) => index);
    const currentSlot = positions.indexOf(columnIndex);
    if (currentSlot === -1) return null;
    let nextSlot = currentSlot + direction;
    let nextRow = rowIndex;
    if (nextSlot >= positions.length) {
      nextSlot = 0;
      nextRow = rowIndex + 1;
      if (nextRow >= rows.length) nextRow = 0;
    } else if (nextSlot < 0) {
      nextSlot = positions.length - 1;
      nextRow = rowIndex - 1;
      if (nextRow < 0) nextRow = rows.length - 1;
    }
    return { rowIndex: nextRow, columnIndex: positions[nextSlot]! };
  }

  function handleCellKeyDown(
    event: ReactKeyboardEvent<HTMLDivElement>,
    row: Record<string, unknown>,
    column: ModelColumn,
  ) {
    if (!onStartEdit || !isColumnEditable(column)) return;
    if (event.key === "Enter" || event.key === "F2") {
      event.preventDefault();
      onStartEdit(row, column);
    }
  }

  function handleEditorWrapperKeyDown(
    event: ReactKeyboardEvent<HTMLDivElement>,
    rowIndex: number,
    columnIndex: number,
  ) {
    if (event.key !== "Tab" || !onStartEdit) return;
    const direction: 1 | -1 = event.shiftKey ? -1 : 1;
    const next = findNextEditableCell(rowIndex, columnIndex, direction);
    if (!next) return;
    event.preventDefault();
    // Commit current editor by blurring its focused input — its onBlur
    // handler will fire onCommit and dispatch cellEditCommitted upstream.
    if (
      typeof document !== "undefined" &&
      document.activeElement instanceof HTMLElement
    ) {
      document.activeElement.blur();
    }
    onStartEdit(rows[next.rowIndex]!, table.columns[next.columnIndex]!);
  }
  const [menuColumn, setMenuColumn] = useState<string | null>(null);
  const [selectionMenu, setSelectionMenu] = useState<{
    x: number;
    y: number;
  } | null>(null);
  const [selectionMenuPosition, setSelectionMenuPosition] = useState<{
    x: number;
    y: number;
  } | null>(null);
  const menuRef = useRef<HTMLDivElement | null>(null);
  const selectionMenuRef = useRef<HTMLDivElement | null>(null);
  const dragSelectionRef = useRef<{
    anchorIndex: number;
    baseSelectionRowIds: string[];
    dragged: boolean;
  } | null>(null);
  const suppressClickRef = useRef(false);
  const selectionAnchorIndexRef = useRef<number | null>(null);
  const selectedRowIdSet = new Set(selectedRowIds);
  const visibleRowIds = rows
    .map((row) => row.id)
    .filter(
      (value): value is string | number =>
        value !== null &&
        value !== undefined &&
        value !== "" &&
        !pendingNewRowIdSet.has(String(value)),
    )
    .map((value) => String(value));
  const allVisibleSelected =
    visibleRowIds.length > 0 &&
    visibleRowIds.every((rowId) => selectedRowIdSet.has(rowId));
  const someVisibleSelected =
    visibleRowIds.some((rowId) => selectedRowIdSet.has(rowId)) &&
    !allVisibleSelected;

  useEffect(() => {
    if (!menuColumn && !selectionMenu && selectedRowIds.length === 0) return;

    function handlePointerDown(event: MouseEvent) {
      if (
        !menuRef.current?.contains(event.target as Node) &&
        !selectionMenuRef.current?.contains(event.target as Node)
      ) {
        setMenuColumn(null);
        setSelectionMenu(null);
      }
    }

    function handleEscape(event: KeyboardEvent) {
      if (event.key === "Escape") {
        setMenuColumn(null);
        setSelectionMenu(null);
        setSelectionMenuPosition(null);
        if (selectedRowIds.length > 0) {
          onClearSelection?.();
        }
      }
    }

    document.addEventListener("mousedown", handlePointerDown);
    document.addEventListener("keydown", handleEscape);
    return () => {
      document.removeEventListener("mousedown", handlePointerDown);
      document.removeEventListener("keydown", handleEscape);
    };
  }, [menuColumn, onClearSelection, selectedRowIds.length, selectionMenu]);

  useLayoutEffect(() => {
    if (!selectionMenu || !selectionMenuRef.current) {
      setSelectionMenuPosition(selectionMenu);
      return;
    }

    const padding = 8;
    const menuRect = selectionMenuRef.current.getBoundingClientRect();
    const nextX = Math.min(
      Math.max(selectionMenu.x, padding),
      Math.max(padding, window.innerWidth - menuRect.width - padding)
    );
    const nextY = Math.min(
      Math.max(selectionMenu.y, padding),
      Math.max(padding, window.innerHeight - menuRect.height - padding)
    );

    setSelectionMenuPosition((current) => {
      if (current?.x === nextX && current?.y === nextY) {
        return current;
      }
      return { x: nextX, y: nextY };
    });
  }, [selectionMenu]);

  useEffect(() => {
    function handleMouseUp() {
      if (!dragSelectionRef.current) return;
      suppressClickRef.current = dragSelectionRef.current.dragged;
      dragSelectionRef.current = null;
    }

    document.addEventListener("mouseup", handleMouseUp);
    return () => {
      document.removeEventListener("mouseup", handleMouseUp);
    };
  }, []);

  function setDraggedRowRange(targetIndex: number) {
    if (!dragSelectionRef.current || !onSetSelectedRowIds) return;

    const dragState = dragSelectionRef.current;
    const startIndex = Math.min(dragState.anchorIndex, targetIndex);
    const endIndex = Math.max(dragState.anchorIndex, targetIndex);
    const rangeRowIds = visibleRowIds.slice(startIndex, endIndex + 1);
    if (rangeRowIds.length === 0) return;

    dragState.dragged = true;
    onSetSelectedRowIds(Array.from(new Set([...dragState.baseSelectionRowIds, ...rangeRowIds])));
  }

  function handlePrimarySelectionClick(
    event: ReactMouseEvent,
    row: Record<string, unknown>,
    rowIndex: number
  ) {
    if (suppressClickRef.current) {
      suppressClickRef.current = false;
      return;
    }

    if (event.shiftKey && onSetSelectedRowIds && selectionAnchorIndexRef.current !== null) {
      const startIndex = Math.min(selectionAnchorIndexRef.current, rowIndex);
      const endIndex = Math.max(selectionAnchorIndexRef.current, rowIndex);
      const rangeRowIds = visibleRowIds.slice(startIndex, endIndex + 1);
      onSetSelectedRowIds(Array.from(new Set([...selectedRowIds, ...rangeRowIds])));
      selectionAnchorIndexRef.current = rowIndex;
      return;
    }

    selectionAnchorIndexRef.current = rowIndex;
    onToggleRowSelection?.(row);
  }

  function isEditableElement(target: EventTarget | null): boolean {
    return (
      target instanceof HTMLElement &&
      (target instanceof HTMLInputElement ||
        target instanceof HTMLTextAreaElement ||
        target instanceof HTMLSelectElement ||
        target.isContentEditable)
    );
  }

  function handleGridKeyDown(event: ReactKeyboardEvent<HTMLDivElement>) {
    if (isEditableElement(event.target)) return;

    if ((event.metaKey || event.ctrlKey) && event.key === "Enter" && onAddRow) {
      event.preventDefault();
      onAddRow();
      return;
    }

    if ((event.metaKey || event.ctrlKey) && event.key === "Delete") {
      event.preventDefault();
      onDeleteRowsShortcut?.(focusedRowId);
      return;
    }

    if (event.key === " " && onOpenRecord && focusedRowId) {
      const row = rows.find((candidate) => String(candidate.id ?? "") === focusedRowId);
      if (!row) return;
      if (pendingNewRowIdSet.has(focusedRowId)) return;
      event.preventDefault();
      onOpenRecord(row);
    }
  }

  return (
    <div
      className={[
        "data-grid",
        xOverflow ? "data-grid--x-overflow" : "",
      ]
        .filter(Boolean)
        .join(" ")}
      onKeyDown={handleGridKeyDown}
    >
      <div className="data-grid__scroll" ref={scrollRef}>
        <table>
          <thead>
            <tr>
              {table.columns.map((column) => (
                <th
                  key={column.name}
                  scope="col"
                  className={[
                    "data-grid__column",
                    column.primaryKey ? "data-grid__column--primary" : "",
                    typeof column.references !== "undefined" || column.type === "relation"
                      ? "data-grid__column--reference"
                      : ""
                  ]
                    .filter(Boolean)
                    .join(" ")}
                >
                  <div
                    className={
                      column.primaryKey
                        ? "data-grid__header-cell data-grid__header-cell--primary"
                        : "data-grid__header-cell"
                    }
                    ref={menuColumn === column.name ? menuRef : undefined}
                  >
                    {column.primaryKey ? (
                      <span className="data-grid__header-selection-actions">
                        <button
                          type="button"
                          className={[
                            "data-grid__select-toggle",
                            "data-grid__select-toggle--header",
                            allVisibleSelected
                              ? "data-grid__select-toggle--selected"
                              : "",
                            someVisibleSelected
                              ? "data-grid__select-toggle--partial"
                              : "",
                          ]
                            .filter(Boolean)
                            .join(" ")}
                          aria-label={
                            allVisibleSelected
                              ? "Clear selected rows"
                              : "Select all visible rows"
                          }
                          title={
                            allVisibleSelected
                              ? "Clear selected rows"
                              : "Select all visible rows"
                          }
                          onClick={(event) => {
                            event.preventDefault();
                            event.stopPropagation();
                            onToggleSelectAllRows?.();
                          }}
                        >
                          <span className="data-grid__select-box" aria-hidden="true">
                            {allVisibleSelected ? (
                              <Icon name="check" size={11} />
                            ) : someVisibleSelected ? (
                              <span className="data-grid__select-box-mark" />
                            ) : null}
                          </span>
                        </button>
                      </span>
                    ) : null}
                    {column.primaryKey ? (
                      <span className="data-grid__header-content data-grid__header-content--primary">
                        <span className="data-grid__header-name-row">
                          <span className="data-grid__header-name">
                            {column.displayName ?? column.name}
                          </span>
                        </span>
                        <span className="data-grid__header-primary-label-group">
                          <span
                            className="data-grid__header-pk-label"
                            title="Primary key"
                          >
                            <Icon name="key" size={11} />
                            <span className="data-grid__header-pk-text">PK</span>
                          </span>
                        </span>
                      </span>
                    ) : (
                      <button
                        type="button"
                        className="data-grid__header-trigger"
                        aria-haspopup="menu"
                        aria-expanded={menuColumn === column.name}
                        aria-label={`Column actions for ${column.displayName ?? column.name}`}
                        onClick={() =>
                          setMenuColumn((current) => (current === column.name ? null : column.name))
                        }
                      >
                        <span className="data-grid__header-content">
                          <span className="data-grid__header-name-row">
                            <span
                              className="data-grid__header-type-icon"
                              title={getColumnTypeLabel(column)}
                            >
                              <Icon name={getColumnTypeIconName(column)} size={13} />
                            </span>
                            <span className="data-grid__header-name">
                              {column.displayName ?? column.name}
                            </span>
                            {column.references || column.type === "relation" ? (
                              <span className="data-grid__header-key-badge data-grid__header-key-badge--inline">FK</span>
                            ) : null}
                          </span>
                        </span>
                        <span className="data-grid__header-icon">
                          <Icon name="more-horizontal" size={14} />
                        </span>
                      </button>
                    )}

                    {!column.primaryKey && menuColumn === column.name ? (
                      <div className="data-grid__header-menu" role="menu">
                        <button
                          type="button"
                          role="menuitem"
                          className="data-grid__header-menu-item"
                          onClick={() => {
                            onRequestSortColumn?.(column);
                            setMenuColumn(null);
                          }}
                        >
                          <Icon name="arrow-up-down" size={14} />
                          Sort by column
                        </button>
                        <button
                          type="button"
                          role="menuitem"
                          className="data-grid__header-menu-item"
                          onClick={() => {
                            onRequestFilterColumn?.(column);
                            setMenuColumn(null);
                          }}
                        >
                          <Icon name="funnel" size={14} />
                          Filter column
                        </button>
                        <button
                          type="button"
                          role="menuitem"
                          className="data-grid__header-menu-item"
                          onClick={() => {
                            onRequestHideColumn?.(column);
                            setMenuColumn(null);
                          }}
                        >
                          <Icon name="columns" size={14} />
                          Hide column
                        </button>
                      </div>
                    ) : null}
                  </div>
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {rows.map((row, index) => {
              const rowIdString = String(row.id ?? "");
              const pendingNew = pendingNewRowIdSet.has(rowIdString);
              const rowError = rowErrors[rowIdString];

              return (
                <Fragment key={`${table.name}-row-fragment-${rowIdString || index}`}>
                  <tr
                    key={`${table.name}-row-${rowIdString || index}`}
                    className={[
                      "data-grid__row",
                      selectedRowIdSet.has(rowIdString) ? "data-grid__row--selected" : "",
                      pendingNew ? "data-grid__row--pending-new" : "",
                      rowError ? "data-grid__row--has-error" : "",
                    ]
                      .filter(Boolean)
                      .join(" ")}
                    onContextMenu={(event) => {
                      if (pendingNew) {
                        return;
                      }

                      event.preventDefault();
                      if (!selectedRowIdSet.has(rowIdString)) {
                        onSetSelectedRowIds?.([rowIdString]);
                      }
                      setFocusedRowId(rowIdString);
                      setSelectionMenu({
                        x: event.clientX,
                        y: event.clientY,
                      });
                      setSelectionMenuPosition({
                        x: event.clientX,
                        y: event.clientY,
                      });
                      setMenuColumn(null);
                    }}
                  >
                    {table.columns.map((column, columnIndex) => {
                      const isEditing =
                        editingCell?.rowId === rowIdString &&
                        editingCell?.columnName === column.name;
                      const cellError = cellErrors[rowIdString]?.[column.name];
                      const editable = isColumnEditable(column) && Boolean(onStartEdit);
                      return (
                        <td
                          key={`${table.name}-${rowIdString || index}-${column.name}`}
                          className={[
                            "data-grid__column",
                            column.primaryKey ? "data-grid__column--primary" : "",
                            typeof column.references !== "undefined" || column.type === "relation"
                              ? "data-grid__column--reference"
                              : "",
                            cellError && !isEditing ? "data-grid__column--has-error" : "",
                          ]
                            .filter(Boolean)
                            .join(" ")}
                          onClick={
                            isPrimarySelectionColumn(column) && onToggleRowSelection && !pendingNew
                              ? (event) => handlePrimarySelectionClick(event, row, index)
                              : editable && !isEditing
                                ? () => onStartEdit?.(row, column)
                                : undefined
                          }
                          onMouseDown={
                            isPrimarySelectionColumn(column) && onSetSelectedRowIds && !pendingNew
                              ? (event) => {
                                  if (event.button !== 0) return;
                                  if (!event.shiftKey) {
                                    selectionAnchorIndexRef.current = index;
                                  }
                                  dragSelectionRef.current = {
                                    anchorIndex: index,
                                    baseSelectionRowIds: selectedRowIds,
                                    dragged: false
                                  };
                                }
                              : undefined
                          }
                          onMouseEnter={
                            isPrimarySelectionColumn(column) && onSetSelectedRowIds && !pendingNew
                              ? (event) => {
                                  if ((event.buttons & 1) !== 1) return;
                                  setDraggedRowRange(index);
                                }
                              : undefined
                          }
                        >
                          <div
                            className={[
                              "data-grid__cell",
                              isPrimarySelectionColumn(column) && onToggleRowSelection && !pendingNew
                                ? "data-grid__cell--interactive"
                                : "",
                              editable && !isEditing
                                ? "data-grid__cell--editable"
                                : "",
                              isEditing ? "data-grid__cell--editing" : "",
                            ]
                              .filter(Boolean)
                              .join(" ")}
                            tabIndex={editable && !isEditing ? 0 : undefined}
                            onFocus={() => setFocusedRowId(rowIdString)}
                            onKeyDown={
                              editable && !isEditing
                                ? (event) => handleCellKeyDown(event, row, column)
                                : undefined
                            }
                          >
                            {isEditing ? (() => {
                              const Editor = pickEditorForColumn(column);
                              const isReference =
                                column.type === "relation" || typeof column.references !== "undefined";
                              const rawValue = row[column.name];
                              // Invisible copy of the static cell content. It keeps the
                              // column at its current width while the editor is open:
                              // without it, editing the widest value in a column swaps
                              // that value for an intrinsically narrow <input>, the auto
                              // table layout shrinks the column, and the editor text is
                              // clipped.
                              const ghost =
                                isReference && rawValue !== null && rawValue !== undefined && rawValue !== "" ? (
                                  <span className="data-grid__reference">
                                    {lookupReferenceLabel(referenceLabels, column, rawValue) ?? String(rawValue)}
                                  </span>
                                ) : (
                                  renderCellValue(column, rawValue)
                                );
                              return (
                                <>
                                  <span className="data-grid__cell-ghost" aria-hidden="true">
                                    {ghost}
                                  </span>
                                  <div
                                    className="data-grid__cell-editor"
                                    onKeyDown={(event) =>
                                      handleEditorWrapperKeyDown(event, index, columnIndex)
                                    }
                                  >
                                    <Editor
                                      value={rawValue as never}
                                      column={column}
                                      autoFocus
                                      error={cellError ?? null}
                                      onCommit={(next) =>
                                        onCommitEdit?.(row, column, next)
                                      }
                                      onCancel={() => onCancelEdit?.()}
                                    />
                                  </div>
                                </>
                              );
                            })() : isPrimarySelectionColumn(column) ? (
                              <span className="data-grid__primary-cell">
                                <span className="data-grid__primary-value">
                                  {pendingNew ? (
                                    <span className="data-grid__new-row-pill">new</span>
                                  ) : (
                                    renderCellValue(column, row[column.name])
                                  )}
                                </span>
                                {onToggleRowSelection && !pendingNew ? (
                                  <button
                                    type="button"
                                    className={[
                                      "data-grid__select-toggle",
                                      selectedRowIdSet.has(rowIdString)
                                        ? "data-grid__select-toggle--selected"
                                        : "",
                                    ]
                                      .filter(Boolean)
                                      .join(" ")}
                                    aria-label={
                                      selectedRowIdSet.has(rowIdString)
                                        ? `Deselect row ${String(row[column.name])}`
                                        : `Select row ${String(row[column.name])}`
                                    }
                                    title={
                                      selectedRowIdSet.has(rowIdString)
                                        ? `Deselect row ${String(row[column.name])}`
                                        : `Select row ${String(row[column.name])}`
                                    }
                                    onClick={(event) => {
                                      event.stopPropagation();
                                      setFocusedRowId(rowIdString);
                                      handlePrimarySelectionClick(event, row, index);
                                    }}
                                  >
                                    <span className="data-grid__select-box" aria-hidden="true">
                                      {selectedRowIdSet.has(rowIdString) ? (
                                        <Icon name="check" size={11} />
                                      ) : null}
                                    </span>
                                  </button>
                                ) : null}
                              </span>
                            ) : (column.type === "relation" || column.references) &&
                              row[column.name] !== null &&
                              row[column.name] !== undefined &&
                              row[column.name] !== "" &&
                              onNavigateReference ? (
                              (() => {
                                const rawId = String(row[column.name]);
                                const label =
                                  lookupReferenceLabel(referenceLabels, column, row[column.name]) ?? rawId;
                                return (
                                  <button
                                    type="button"
                                    className="data-grid__reference data-grid__reference--interactive"
                                    aria-label={`Open related ${label}`}
                                    title={`Open related ${label}`}
                                    onClick={() => onNavigateReference(column, row[column.name], row)}
                                  >
                                    {label}
                                  </button>
                                );
                              })()
                            ) : (
                              renderCellValue(column, row[column.name])
                            )}
                          </div>
                        </td>
                      );
                    })}
                    {onOpenRecord ? (
                      <td
                        className="data-grid__column data-grid__column--actions"
                        onClick={(event) => event.stopPropagation()}
                      >
                        <div className="data-grid__cell data-grid__cell--actions">
                          {!pendingNew ? (
                            <button
                              type="button"
                              className="data-grid__row-expand"
                              aria-label={`Open record ${rowIdString}`}
                              title="Open record"
                              onClick={(event) => {
                                event.stopPropagation();
                                onOpenRecord(row);
                              }}
                            >
                              <Icon name="expand" size={13} />
                            </button>
                          ) : null}
                        </div>
                      </td>
                    ) : null}
                  </tr>
                  {rowError ? (
                    <tr
                      key={`${table.name}-row-error-${rowIdString || index}`}
                      className="data-grid__row-error-row"
                    >
                      <td colSpan={table.columns.length}>
                        <div className="data-grid__row-error-banner">{rowError}</div>
                      </td>
                    </tr>
                  ) : null}
                </Fragment>
              );
            })}
            {onAddRow ? (
              <tr className="data-grid__add-row">
                <td className="data-grid__column data-grid__column--primary">
                  <button
                    type="button"
                    className="data-grid__add-row-trigger"
                    aria-label="Add row"
                    onClick={() => onAddRow()}
                  >
                    <Icon name="plus" size={14} />
                  </button>
                </td>
                {table.columns.length > 1 ? (
                  <td colSpan={table.columns.length - 1} className="data-grid__add-row-fill">
                    <button
                      type="button"
                      className="data-grid__add-row-copy"
                      onClick={() => onAddRow()}
                    >
                      Add row
                    </button>
                  </td>
                ) : null}
              </tr>
            ) : null}
          </tbody>
        </table>
      </div>
      {selectionMenu && typeof document !== "undefined"
        ? createPortal(
            <div
              ref={selectionMenuRef}
              className="data-grid__selection-menu"
              style={{
                left: selectionMenuPosition?.x ?? selectionMenu.x,
                top: selectionMenuPosition?.y ?? selectionMenu.y
              }}
              role="menu"
            >
              {selectionMenuItems.map((item) => (
                <button
                  key={item.id}
                  type="button"
                  role="menuitem"
                  className="data-grid__selection-menu-item"
                  disabled={item.disabled}
                  onClick={() => {
                    item.onSelect();
                    setSelectionMenu(null);
                    setSelectionMenuPosition(null);
                  }}
                >
                  {item.label}
                </button>
              ))}
            </div>,
            document.body
          )
        : null}
    </div>
  );
}
