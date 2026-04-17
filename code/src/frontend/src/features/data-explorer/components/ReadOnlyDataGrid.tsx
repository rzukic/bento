import { useEffect, useLayoutEffect, useRef, useState, type MouseEvent as ReactMouseEvent } from "react";
import { createPortal } from "react-dom";
import { Icon } from "../../../components/common/Icon";
import type { ModelColumn, ModelTable } from "../../../types/api";
import { renderCellValue } from "./cellValue";

type ReadOnlyDataGridProps = {
  table: ModelTable;
  rows: Record<string, unknown>[];
  page: number;
  perPage: number;
  onRequestFilterColumn?: (column: ModelColumn) => void;
  onRequestSortColumn?: (column: ModelColumn) => void;
  onRequestHideColumn?: (column: ModelColumn) => void;
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
};

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
    case "string":
      return "type" as const;
    case "double":
    case "int64":
    case "integer":
    case "float":
    case "number":
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
    case "double":
    case "int64":
    case "integer":
    case "float":
    case "number":
      return "Number";
    default:
      return "Text";
  }
}


export function ReadOnlyDataGrid({
  table,
  rows,
  page: _page,
  perPage: _perPage,
  onRequestFilterColumn,
  onRequestSortColumn,
  onRequestHideColumn,
  onNavigateReference,
  selectedRowIds = [],
  onToggleRowSelection,
  onToggleSelectAllRows,
  onSetSelectedRowIds,
  onClearSelection,
  selectionMenuItems = [],
}: ReadOnlyDataGridProps) {
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
        value !== null && value !== undefined && value !== "",
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

    const rowId = String(row.id);

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

  return (
    <div className="data-grid">
      <div className="data-grid__scroll">
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
                      <span className="data-grid__header-pk-label">PK</span>
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
              {rows.map((row, index) => (
                <tr
                  key={`${table.name}-row-${index}`}
                  className={
                    selectedRowIdSet.has(String(row.id))
                      ? "data-grid__row data-grid__row--selected"
                      : "data-grid__row"
                  }
                  onContextMenu={(event) => {
                    if (
                      !selectedRowIdSet.has(String(row.id)) ||
                      selectionMenuItems.length === 0
                    ) {
                      return;
                    }

                    event.preventDefault();
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
                  {table.columns.map((column) => (
                  <td
                    key={`${table.name}-${index}-${column.name}`}
                    className={[
                      "data-grid__column",
                      column.primaryKey ? "data-grid__column--primary" : "",
                      typeof column.references !== "undefined" || column.type === "relation"
                        ? "data-grid__column--reference"
                        : ""
                    ]
                      .filter(Boolean)
                      .join(" ")}
                    onClick={
                      column.primaryKey && onToggleRowSelection
                        ? (event) => handlePrimarySelectionClick(event, row, index)
                        : undefined
                    }
                    onMouseDown={
                      column.primaryKey && onSetSelectedRowIds
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
                      column.primaryKey && onSetSelectedRowIds
                        ? (event) => {
                            if ((event.buttons & 1) !== 1) return;
                            setDraggedRowRange(index);
                          }
                        : undefined
                    }
                  >
                    <div
                      className={
                        column.primaryKey && onToggleRowSelection
                          ? "data-grid__cell data-grid__cell--interactive"
                          : "data-grid__cell"
                      }
                    >
                      {column.primaryKey ? (
                        <span className="data-grid__primary-cell">
                          <span className="data-grid__primary-value">
                            {renderCellValue(column, row[column.name])}
                          </span>
                          {onToggleRowSelection ? (
                            <button
                              type="button"
                              className={[
                                "data-grid__select-toggle",
                                selectedRowIdSet.has(String(row.id))
                                  ? "data-grid__select-toggle--selected"
                                  : "",
                              ]
                                .filter(Boolean)
                                .join(" ")}
                              aria-label={
                                selectedRowIdSet.has(String(row.id))
                                  ? `Deselect row ${String(row[column.name])}`
                                  : `Select row ${String(row[column.name])}`
                              }
                              title={
                                selectedRowIdSet.has(String(row.id))
                                  ? `Deselect row ${String(row[column.name])}`
                                  : `Select row ${String(row[column.name])}`
                              }
                              onClick={(event) => {
                                event.stopPropagation();
                                handlePrimarySelectionClick(event, row, index);
                              }}
                            >
                              <span className="data-grid__select-box" aria-hidden="true">
                                {selectedRowIdSet.has(String(row.id)) ? (
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
                        <button
                          type="button"
                          className="data-grid__reference data-grid__reference--interactive"
                          aria-label={`Open related ${String(row[column.name])}`}
                          title={`Open related ${String(row[column.name])}`}
                          onClick={() => onNavigateReference(column, row[column.name], row)}
                        >
                          {String(row[column.name])}
                        </button>
                      ) : (
                        renderCellValue(column, row[column.name])
                      )}
                    </div>
                  </td>
                ))}
              </tr>
            ))}
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
