import React, {
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type FormEvent,
  type ReactNode,
} from "react";
import { Icon } from "../../../components/common/Icon";
import { Button } from "../../../components/ui/Button";
import { SelectField } from "../../../components/ui/SelectField";
import { TextField } from "../../../components/ui/TextField";
import type { ModelColumn, ModelTable } from "../../../types/api";
import type {
  ExplorerQuery,
  ExplorerViewConfig,
  ExplorerViewType,
  FilterClause,
  FilterOp,
  SortClause,
  SortDir,
} from "../../../types/explorer";
import { DEFAULT_EXPLORER_VIEW_CONFIG } from "../../../types/explorer";
import type { SavedView } from "../../../types/views";

type DataQueryControlsProps = {
  table: ModelTable;
  query: ExplorerQuery;
  disabled?: boolean;
  toolbarActions?: ReactNode;
  /**
   * True while a popover rendered inside `toolbarActions` (e.g. the Export
   * menu) is open. Opening it closes any active foldout panel.
   */
  externalPopoverOpen?: boolean;
  /** Fires whenever the active foldout panel changes, so the parent can close its own popovers. */
  onActivePanelChange?: (panel: string | null) => void;
  hiddenColumnNames?: string[];
  selectionPanel?: { content: ReactNode; count?: number } | null;
  suppressAutoPanel?: boolean;
  panelResetKey?: string;
  filterShortcut?: { columnName: string; nonce: number } | null;
  sortShortcut?: { columnName: string; nonce: number } | null;
  /** Saved views to show in the view selector. */
  savedViews?: SavedView[];
  /** The currently active saved view id, or null for "All Records". */
  activeViewId?: number | null;
  /** Called when the user selects a view (null = All Records). */
  onSwitchView?: (viewId: number | null) => void;
  /** Save the current config as a brand-new named view. */
  onSaveCurrentAsView?: (name: string, type: ExplorerViewType) => Promise<void>;
  /** Persist config changes back to the currently active saved view. */
  onUpdateActiveViewConfig?: () => Promise<void>;
  /** Rename a saved view. */
  onRenameView?: (id: number, name: string) => Promise<void>;
  /** Toggle the is_shared flag on a saved view. */
  onToggleViewShared?: (id: number) => Promise<void>;
  /** Delete a saved view. */
  onDeleteView?: (id: number) => Promise<void>;
  /** Change the view type (table ↔ kanban). */
  onChangeViewType?: (id: number, type: ExplorerViewType) => Promise<void>;
  /** The id of the view marked as default for this table, if any. */
  defaultViewId?: number | null;
  /** Mark a saved view (or null for "All Records") as the default. */
  onSetDefaultView?: (id: number | null) => Promise<void>;
  /** Create an owned copy of a shared view owned by someone else. */
  onDuplicateView?: (id: number, name: string) => Promise<void>;
  /** The current user's id — used to gate owner-only actions in the UI. */
  currentUserId?: number | null;
  /** Whether auto-save is on for the active view. */
  autoSave?: boolean;
  /** Toggle auto-save on the active view. */
  onSetAutoSave?: (enabled: boolean) => void;
  /** The full current persisted view config for dirty-state comparison. */
  currentViewConfig?: ExplorerViewConfig | null;
  onSearch: (search: string) => void;
  onSortChange: (sorts: SortClause[]) => void;
  onPerPageChange: (perPage: number) => void;
  onPageChange: (page: number) => void;
  onFiltersChange: (filters: FilterClause[]) => void;
  onHiddenColumnsChange: (columnNames: string[]) => void;
  onResetView: () => void;
  /** The current active view type — used to show the configure button for kanban. */
  viewType?: ExplorerViewType;
  /** Open the kanban configuration panel. */
  onConfigureKanban?: () => void;
};

type DataPaginationBarProps = {
  query: ExplorerQuery;
  total: number;
  totalPages: number;
  disabled?: boolean;
  placement?: "top" | "bottom";
  onPerPageChange: (perPage: number) => void;
  onPageChange: (page: number) => void;
};

type UtilityPanel =
  | "search"
  | "sort"
  | "filter"
  | "columns"
  | "selection"
  | null;
type SortDraftClause = { column: string; dir: SortDir };

type FilterOperatorOption = { label: string; value: FilterOp };

const defaultFilterOperatorOptions: FilterOperatorOption[] = [
  { label: "Equals", value: "eq" },
  { label: "Does not equal", value: "neq" },
  { label: "Is one of", value: "in" },
  { label: "Contains", value: "like" },
  { label: "Greater than", value: "gt" },
  { label: "Greater or equal", value: "gte" },
  { label: "Less than", value: "lt" },
  { label: "Less or equal", value: "lte" },
  { label: "Is empty", value: "null" },
  { label: "Is not empty", value: "notnull" },
];

const nullaryFilterOps: FilterOp[] = ["null", "notnull"];
const textLikeColumnTypes = new Set(["string", "text", "json", "relation", "uuid"]);
const numericColumnTypes = new Set(["integer", "float", "currency"]);
const temporalColumnTypes = new Set(["date", "datetime"]);

function normalizeColumnType(type: string | undefined): string {
  switch ((type ?? "").toLowerCase()) {
    case "int64":
    case "integer":
      return "integer";
    case "double":
    case "float":
    case "currency":
      return "float";
    case "relation":
    case "reference":
      return "relation";
    case "string":
    case "text":
      return "text";
    case "uuid":
      return "uuid";
    case "datetime":
      return "datetime";
    case "date":
      return "date";
    case "enum":
      return "enum";
    case "boolean":
      return "boolean";
    case "json":
      return "json";
    default:
      return (type ?? "").toLowerCase();
  }
}

function getColumnFilterOptions(column: ModelColumn | undefined) {
  if (!column) return defaultFilterOperatorOptions;
  const normalizedType = normalizeColumnType(column.type);
  const supportsOneOf =
    normalizedType === "enum" ||
    normalizedType === "relation" ||
    !!column.references ||
    !!column.primaryKey;

  if (normalizedType === "enum") {
    return [
      { label: "Equals", value: "eq" },
      { label: "Does not equal", value: "neq" },
      { label: "Is one of", value: "in" },
      { label: "Is empty", value: "null" },
      { label: "Is not empty", value: "notnull" },
    ];
  }

  if (normalizedType === "boolean") {
    return defaultFilterOperatorOptions.filter((option) =>
      ["eq", "neq", "null", "notnull"].includes(option.value),
    );
  }

  if (numericColumnTypes.has(normalizedType)) {
    return defaultFilterOperatorOptions.filter((option) =>
      [
        "eq",
        "neq",
        "gt",
        "gte",
        "lt",
        "lte",
        ...(supportsOneOf ? ["in"] : []),
        "null",
        "notnull",
      ].includes(option.value),
    );
  }

  if (temporalColumnTypes.has(normalizedType)) {
    const temporalNoun = normalizedType === "datetime" ? "date/time" : "date";
    return [
      { label: `On ${temporalNoun}`, value: "eq" },
      { label: `Not on ${temporalNoun}`, value: "neq" },
      { label: `After ${temporalNoun}`, value: "gt" },
      { label: `On or after ${temporalNoun}`, value: "gte" },
      { label: `Before ${temporalNoun}`, value: "lt" },
      { label: `On or before ${temporalNoun}`, value: "lte" },
      { label: "Is empty", value: "null" },
      { label: "Is not empty", value: "notnull" },
    ];
  }

  if (textLikeColumnTypes.has(normalizedType)) {
    return defaultFilterOperatorOptions.filter((option) =>
      [
        "eq",
        "neq",
        ...(supportsOneOf ? ["in"] : []),
        "like",
        "null",
        "notnull",
      ].includes(option.value),
    );
  }

  return defaultFilterOperatorOptions.filter((option) =>
    ["eq", "neq", ...(supportsOneOf ? ["in"] : []), "null", "notnull"].includes(
      option.value,
    ),
  );
}

function getFilterValueLabel(column: ModelColumn | undefined, op: FilterOp) {
  if (op === "in") return "Values";
  const normalizedType = normalizeColumnType(column?.type);
  if (normalizedType === "enum") return "Select value";
  if (normalizedType === "boolean") return "Choose value";
  if (normalizedType === "date") return "Choose date";
  if (normalizedType === "datetime") return "Choose date and time";
  return "Value";
}

function getFilterValuePlaceholder(
  column: ModelColumn | undefined,
  op: FilterOp,
) {
  if (op === "in") return "Comma-separated values";
  const normalizedType = normalizeColumnType(column?.type);
  if (normalizedType === "enum") return undefined;
  if (normalizedType === "boolean") return undefined;
  if (normalizedType === "date") return undefined;
  if (normalizedType === "datetime") return undefined;
  return "Value";
}

function normalizeViewConfigForComparison(config: ExplorerViewConfig) {
  const base = {
    type: config.type,
    search: config.search,
    sort: config.sort.map((sort) => ({ ...sort })),
    filters: config.filters.map((filter) => ({ ...filter })),
    hiddenColumnNames: [...config.hiddenColumnNames].sort(),
    columnOrder: [...config.columnOrder],
  };

  if (config.type === "kanban") {
    return {
      ...base,
      kanbanColumn: config.kanbanColumn,
      cardFieldNames: [...config.cardFieldNames],
      hiddenLaneValues: [...(config.hiddenLaneValues ?? [])].sort(),
    };
  }

  return base;
}

function areViewConfigsEquivalent(
  left: ExplorerViewConfig,
  right: ExplorerViewConfig,
): boolean {
  return (
    JSON.stringify(normalizeViewConfigForComparison(left)) ===
    JSON.stringify(normalizeViewConfigForComparison(right))
  );
}

function getSortDirectionOptions(
  column: ModelColumn | undefined,
): Array<{ label: string; value: SortDir }> {
  if (!column) {
    return [
      { label: "Ascending", value: "asc" },
      { label: "Descending", value: "desc" },
    ];
  }

  const normalizedType = normalizeColumnType(column.type);

  if (normalizedType === "date" || normalizedType === "datetime") {
    const noun = normalizedType === "datetime" ? "time" : "date";
    return [
      { label: `Earliest ${noun} first`, value: "asc" },
      { label: `Latest ${noun} first`, value: "desc" },
    ];
  }

  if (numericColumnTypes.has(normalizedType) || normalizedType === "relation") {
    return [
      { label: "Lowest first", value: "asc" },
      { label: "Highest first", value: "desc" },
    ];
  }

  if (textLikeColumnTypes.has(normalizedType) || normalizedType === "enum") {
    return [
      { label: "A → Z", value: "asc" },
      { label: "Z → A", value: "desc" },
    ];
  }

  if (normalizedType === "boolean") {
    return [
      { label: "No first", value: "asc" },
      { label: "Yes first", value: "desc" },
    ];
  }

  return [
    { label: "Ascending", value: "asc" },
    { label: "Descending", value: "desc" },
  ];
}

function createEmptySortDraft(): SortDraftClause {
  return { column: "", dir: "asc" };
}

function formatFilterChipValue(
  column: ModelColumn | undefined,
  value: string | null | undefined,
): string {
  const trimmed = (value ?? "").trim();
  if (!trimmed) return "—";
  if (normalizeColumnType(column?.type) === "boolean") {
    if (trimmed.toLowerCase() === "true") return "Yes";
    if (trimmed.toLowerCase() === "false") return "No";
  }
  return trimmed;
}

function formatFilterChip(
  filter: FilterClause,
  columns: ModelColumn[],
): string {
  const column = columns.find((item) => item.name === filter.column);
  const name = column?.displayName ?? filter.column;
  const value = formatFilterChipValue(column, filter.value);
  const isTemporal = temporalColumnTypes.has(normalizeColumnType(column?.type));

  switch (filter.op) {
    case "eq":
      return `${name} ${isTemporal ? "is on" : "is"} ${value}`;
    case "neq":
      return `${name} ${isTemporal ? "is not on" : "is not"} ${value}`;
    case "like":
      return `${name} contains ${value}`;
    case "gt":
      return `${name} ${isTemporal ? "is after" : "is greater than"} ${value}`;
    case "gte":
      return `${name} ${isTemporal ? "is on or after" : "is at least"} ${value}`;
    case "lt":
      return `${name} ${isTemporal ? "is before" : "is less than"} ${value}`;
    case "lte":
      return `${name} ${isTemporal ? "is on or before" : "is at most"} ${value}`;
    case "null":
      return `${name} is empty`;
    case "notnull":
      return `${name} is not empty`;
    case "in":
      return `${name} is one of ${
        (filter.value ?? "")
          .split(",")
          .map((item) => formatFilterChipValue(column, item))
          .filter((item) => item !== "—")
          .join(", ") || "—"
      }`;
    default:
      return `${name} ${filter.op} ${value}`;
  }
}

function formatSortChip(sort: SortClause, columns: ModelColumn[]): string {
  const column = columns.find((item) => item.name === sort.column);
  const name = column?.displayName ?? sort.column;
  const direction =
    getSortDirectionOptions(column).find((option) => option.value === sort.dir)
      ?.label ?? sort.dir;
  return `${name} (${direction})`;
}

function getInitialPanel(query: ExplorerQuery): UtilityPanel {
  if (query.filters.length > 0) return "filter";
  if (query.sort.length > 0) return "sort";
  if (query.search) return "search";
  return null;
}

type EnumValueDropdownProps = {
  label: string;
  placeholder: string;
  options: string[];
  values: string[];
  multiple?: boolean;
  disabled?: boolean;
  onChange: (values: string[]) => void;
};

function EnumValueDropdown({
  label,
  placeholder,
  options,
  values,
  multiple = false,
  disabled = false,
  onChange,
}: EnumValueDropdownProps) {
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!open) return;

    function handlePointerDown(event: PointerEvent) {
      if (!rootRef.current?.contains(event.target as Node)) {
        setOpen(false);
      }
    }

    function handleKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") {
        setOpen(false);
      }
    }

    window.addEventListener("pointerdown", handlePointerDown);
    window.addEventListener("keydown", handleKeyDown);
    return () => {
      window.removeEventListener("pointerdown", handlePointerDown);
      window.removeEventListener("keydown", handleKeyDown);
    };
  }, [open]);

  useEffect(() => {
    if (!disabled) return;
    setOpen(false);
  }, [disabled]);

  const displayValue = useMemo(() => {
    if (values.length === 0) return placeholder;
    if (!multiple) return values[0] ?? placeholder;
    if (values.length <= 2) return values.join(", ");
    return `${values.length} selected`;
  }, [multiple, placeholder, values]);

  function toggleValue(option: string) {
    if (multiple) {
      const nextValues = values.includes(option)
        ? values.filter((value) => value !== option)
        : [...values, option];
      onChange(nextValues);
      return;
    }

    onChange([option]);
    setOpen(false);
  }

  return (
    <label className="field">
      <span className="field__label-row">
        <span className="field__label">{label}</span>
      </span>
      <div className="data-controls__enum-dropdown" ref={rootRef}>
        <button
          type="button"
          className={`field__control data-controls__enum-trigger ${open ? "data-controls__enum-trigger--open" : ""}`}
          aria-haspopup="listbox"
          aria-expanded={open}
          aria-label={label}
          disabled={disabled}
          onClick={() => setOpen((current) => !current)}
        >
          <span
            className={
              values.length === 0
                ? "data-controls__enum-trigger-value data-controls__enum-trigger-value--placeholder"
                : "data-controls__enum-trigger-value"
            }
          >
            {displayValue}
          </span>
          <Icon name="chevron-down" size={14} />
        </button>
        {open ? (
          <div className="data-controls__enum-menu">
            <ul
              className="data-controls__enum-list"
              role="listbox"
              aria-label={label}
              aria-multiselectable={multiple || undefined}
            >
              {options.map((option) => {
                const selected = values.includes(option);
                return (
                  <li key={option}>
                    <button
                      type="button"
                      role="option"
                      aria-selected={selected}
                      className={`data-controls__enum-option ${selected ? "data-controls__enum-option--selected" : ""}`}
                      onClick={() => toggleValue(option)}
                    >
                      {multiple ? (
                        <span
                          className="data-controls__enum-check"
                          aria-hidden="true"
                        >
                          <input
                            type="checkbox"
                            checked={selected}
                            readOnly
                            tabIndex={-1}
                          />
                        </span>
                      ) : (
                        <span
                          className="data-controls__enum-check data-controls__enum-check--icon"
                          aria-hidden="true"
                        >
                          {selected ? <Icon name="check" size={12} /> : null}
                        </span>
                      )}
                      <span>{option}</span>
                    </button>
                  </li>
                );
              })}
            </ul>
            {multiple ? (
              <div className="data-controls__enum-footer">
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  onClick={() => setOpen(false)}
                >
                  Done
                </Button>
              </div>
            ) : null}
          </div>
        ) : null}
      </div>
    </label>
  );
}

export function DataPaginationBar({
  query,
  total,
  totalPages,
  disabled = false,
  placement = "top",
  onPerPageChange,
  onPageChange,
}: DataPaginationBarProps) {
  const rangeStart = total === 0 ? 0 : (query.page - 1) * query.perPage + 1;
  const rangeEnd =
    total === 0 ? 0 : Math.min(query.page * query.perPage, total);

  return (
    <div className={`data-pagination data-pagination--${placement}`}>
      <span className="data-pagination__range">
        Showing {rangeStart}-{rangeEnd} of {total}
      </span>
      <div className="data-pagination__controls">
        <SelectField
          label="Rows per page"
          hideLabel
          compact
          name={`per-page-${placement}`}
          value={String(query.perPage)}
          onChange={(event) => onPerPageChange(Number(event.target.value))}
          options={[
            { label: "10 rows", value: "10" },
            { label: "25 rows", value: "25" },
            { label: "50 rows", value: "50" },
            { label: "100 rows", value: "100" },
          ]}
          disabled={disabled}
        />
        <div className="data-pagination__switcher">
          <Button
            type="button"
            variant="ghost"
            size="sm"
            disabled={disabled || query.page <= 1}
            onClick={() => onPageChange(query.page - 1)}
            leadingIcon={<Icon name="chevron-left" size={14} />}
          >
            Previous
          </Button>
          <span className="data-pagination__page">
            Page {query.page} of {Math.max(totalPages, 1)}
          </span>
          <Button
            type="button"
            variant="ghost"
            size="sm"
            disabled={disabled || query.page >= Math.max(totalPages, 1)}
            onClick={() => onPageChange(query.page + 1)}
            trailingIcon={<Icon name="chevron-right" size={14} />}
          >
            Next
          </Button>
        </div>
      </div>
    </div>
  );
}

export function DataQueryControls({
  table,
  query,
  disabled = false,
  toolbarActions = null,
  externalPopoverOpen = false,
  onActivePanelChange,
  hiddenColumnNames = [],
  selectionPanel = null,
  suppressAutoPanel = false,
  panelResetKey,
  filterShortcut = null,
  sortShortcut = null,
  savedViews = [],
  activeViewId = null,
  onSwitchView,
  onSaveCurrentAsView,
  onUpdateActiveViewConfig,
  onRenameView,
  onToggleViewShared,
  onDeleteView,
  onChangeViewType,
  defaultViewId = null,
  onSetDefaultView,
  onDuplicateView,
  currentUserId = null,
  autoSave = false,
  onSetAutoSave,
  currentViewConfig = null,
  onSearch,
  onSortChange,
  onFiltersChange,
  onHiddenColumnsChange,
  onResetView,
  viewType = "table",
  onConfigureKanban,
}: DataQueryControlsProps) {
  const rootRef = useRef<HTMLDivElement | null>(null);
  const viewDropdownRef = useRef<HTMLDivElement | null>(null);
  const overlayAnchorRef = useRef<HTMLDivElement | null>(null);
  const menuRef = useRef<HTMLDivElement | null>(null);

  // ── Query controls state ──
  const [searchDraft, setSearchDraft] = useState(query.search);
  const [sortDrafts, setSortDrafts] = useState<SortDraftClause[]>(
    query.sort.length > 0
      ? query.sort.map((sort) => ({ ...sort }))
      : [createEmptySortDraft()],
  );
  const [filterColumn, setFilterColumn] = useState(
    table.columns[0]?.name ?? "",
  );
  const [filterOp, setFilterOp] = useState<FilterOp>("eq");
  const [filterValue, setFilterValue] = useState("");
  const [editingFilterIndex, setEditingFilterIndex] = useState<number | null>(
    null,
  );
  const [activePanel, setActivePanel] = useState<UtilityPanel>(() =>
    suppressAutoPanel ? null : getInitialPanel(query),
  );

  // The floating foldout panel is anchored to the right edge of the bar. For
  // narrow panels (Search) its left edge would land inside the active tab, so
  // the tab visually hangs over the panel corner. Stretch the panel's minimum
  // width so it always reaches the active tab's left edge and the two merge
  // into one shape.
  useLayoutEffect(() => {
    const anchor = overlayAnchorRef.current;
    if (!anchor) return;

    function syncPanelMinWidth() {
      if (!anchor) return;

      // How far the right-anchored panel overhangs the last (right-most) tab.
      // Panels that start at a tab's left edge get the same overhang on the
      // left so both sides of the toolbar look symmetric.
      const anchorRect = anchor.getBoundingClientRect();
      const rightOffset =
        parseFloat(
          getComputedStyle(anchor).getPropertyValue("--foldout-right-offset"),
        ) || 0;
      const menuButtons = menuRef.current?.querySelectorAll<HTMLElement>(
        ".data-controls__menu-button",
      );
      const lastButton = menuButtons?.[menuButtons.length - 1];
      const edgeInset = lastButton
        ? Math.max(
            0,
            anchorRect.right - rightOffset - lastButton.getBoundingClientRect().right,
          )
        : 0;
      anchor.style.setProperty("--foldout-edge-inset", `${edgeInset}px`);

      const activeButton = menuRef.current?.querySelector<HTMLElement>(
        '[aria-expanded="true"]',
      );
      const panel = anchor.querySelector<HTMLElement>(
        ".data-controls__panel--floating",
      );
      if (!activeButton || !panel) {
        anchor.style.removeProperty("--foldout-min-width");
        return;
      }

      const minWidth =
        panel.getBoundingClientRect().right -
        activeButton.getBoundingClientRect().left +
        edgeInset;
      // Fractional widths are intentional so the panel edge lands exactly
      // where the layout says, not on the nearest whole pixel.
      anchor.style.setProperty(
        "--foldout-min-width",
        `${Math.max(0, minWidth)}px`,
      );
    }

    syncPanelMinWidth();
    window.addEventListener("resize", syncPanelMinWidth);
    return () => {
      window.removeEventListener("resize", syncPanelMinWidth);
    };
  }, [activePanel, externalPopoverOpen]);

  // ── View selector state ──
  const [viewDropdownOpen, setViewDropdownOpen] = useState(false);
  const [createViewOpen, setCreateViewOpen] = useState(false);
  const [createViewName, setCreateViewName] = useState("");
  const [createViewTypeDraft, setCreateViewTypeDraft] = useState<ExplorerViewType>("table");
  const [createViewSaving, setCreateViewSaving] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [renameDraft, setRenameDraft] = useState("");
  const [renameSaving, setRenameSaving] = useState(false);
  const [viewTypeDraft, setViewTypeDraft] = useState<ExplorerViewType>("table");
  const [deletingView, setDeletingView] = useState(false);
  const [duplicatingView, setDuplicatingView] = useState(false);

  useEffect(() => {
    if (panelResetKey === undefined) return;
    setActivePanel(null);
  }, [panelResetKey]);

  useEffect(() => {
    if (selectionPanel || activePanel !== "selection") return;
    setActivePanel(null);
  }, [activePanel, selectionPanel]);

  // Only one toolbar popover may be open at a time: an external popover
  // (Export menu) closes the active foldout, and the parent is told about
  // foldout changes so it can close its popover in return.
  useEffect(() => {
    if (!externalPopoverOpen) return;
    setActivePanel(null);
  }, [externalPopoverOpen]);

  const onActivePanelChangeRef = useRef(onActivePanelChange);
  onActivePanelChangeRef.current = onActivePanelChange;
  useEffect(() => {
    onActivePanelChangeRef.current?.(activePanel);
  }, [activePanel]);

  useEffect(() => {
    if (!activePanel) return;

    function handlePointerDown(event: PointerEvent) {
      if (!rootRef.current?.contains(event.target as Node)) {
        setActivePanel(null);
      }
    }

    window.addEventListener("pointerdown", handlePointerDown);
    return () => {
      window.removeEventListener("pointerdown", handlePointerDown);
    };
  }, [activePanel]);

  // Close view dropdown on outside click
  useEffect(() => {
    if (!viewDropdownOpen) return;

    function handlePointerDown(event: PointerEvent) {
      if (!viewDropdownRef.current?.contains(event.target as Node)) {
        setViewDropdownOpen(false);
      }
    }

    window.addEventListener("pointerdown", handlePointerDown);
    return () => {
      window.removeEventListener("pointerdown", handlePointerDown);
    };
  }, [viewDropdownOpen]);

  useEffect(() => {
    setSearchDraft(query.search);
  }, [query.search]);

  useEffect(() => {
    setSortDrafts(
      query.sort.length > 0
        ? query.sort.map((sort) => ({ ...sort }))
        : [createEmptySortDraft()],
    );
  }, [query.sort]);

  useEffect(() => {
    const nextSearch = searchDraft.trim();
    if (disabled || nextSearch === query.search) return;

    const timeoutId = window.setTimeout(() => {
      onSearch(nextSearch);
    }, 250);

    return () => {
      window.clearTimeout(timeoutId);
    };
  }, [disabled, onSearch, query.search, searchDraft]);

  useEffect(() => {
    if (!table.columns.some((column) => column.name === filterColumn)) {
      setFilterColumn(table.columns[0]?.name ?? "");
    }
  }, [table, filterColumn]);

  const selectedFilterColumn = useMemo(
    () => table.columns.find((column) => column.name === filterColumn),
    [filterColumn, table.columns],
  );

  const filterOperatorOptions = useMemo(
    () => getColumnFilterOptions(selectedFilterColumn),
    [selectedFilterColumn],
  );

  useEffect(() => {
    if (!filterShortcut) return;
    if (
      !table.columns.some((column) => column.name === filterShortcut.columnName)
    )
      return;

    setFilterColumn(filterShortcut.columnName);
    setFilterOp("eq");
    setFilterValue("");
    setEditingFilterIndex(null);
    setActivePanel("filter");
  }, [filterShortcut, table.columns]);

  useEffect(() => {
    if (!sortShortcut) return;
    if (
      !table.columns.some((column) => column.name === sortShortcut.columnName)
    )
      return;

    setEditingFilterIndex(null);
    setSortDrafts((current) => {
      if (current.length === 0) {
        return [{ column: sortShortcut.columnName, dir: "asc" }];
      }

      const next = current.map((draft) => ({ ...draft }));
      if (next[0]?.column) {
        next[0] = { ...next[0], column: sortShortcut.columnName };
        return next;
      }

      next[0] = { column: sortShortcut.columnName, dir: "asc" };
      return next;
    });
    setActivePanel("sort");
  }, [sortShortcut, table.columns]);

  useEffect(() => {
    if (filterOperatorOptions.some((option) => option.value === filterOp))
      return;
    setFilterOp((filterOperatorOptions[0]?.value ?? "eq") as FilterOp);
  }, [filterOp, filterOperatorOptions]);

  useEffect(() => {
    if (editingFilterIndex === null) return;
    if (editingFilterIndex < query.filters.length) return;
    setEditingFilterIndex(null);
  }, [editingFilterIndex, query.filters.length]);

  const filterColumnOptions = useMemo(
    () =>
      table.columns.map((column) => ({
        label: column.displayName ?? column.name,
        value: column.name,
      })),
    [table.columns],
  );

  const searchLabel = query.search ? `Search: ${query.search}` : null;
  const sortLabel =
    query.sort.length > 0
      ? `Sort: ${query.sort.map((sort) => formatSortChip(sort, table.columns)).join(", ")}`
      : null;

  const canAddFilter =
    filterColumn.length > 0 &&
    (nullaryFilterOps.includes(filterOp) || filterValue.trim().length > 0);

  const selectedEnumValues = useMemo(
    () =>
      filterValue
        .split(",")
        .map((value) => value.trim())
        .filter(Boolean),
    [filterValue],
  );
  const selectedFilterColumnType = normalizeColumnType(
    selectedFilterColumn?.type,
  );
  const sortFieldOptions = useMemo(
    () => [
      { label: "Choose field", value: "" },
      ...table.columns.map((column) => ({
        label: column.displayName ?? column.name,
        value: column.name,
      })),
    ],
    [table.columns],
  );
  const hidableColumns = useMemo(
    () => table.columns.filter((column) => !column.primaryKey),
    [table.columns],
  );
  const hiddenColumnSet = useMemo(
    () => new Set(hiddenColumnNames),
    [hiddenColumnNames],
  );

  function handleSearchSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    onSearch(searchDraft.trim());
  }

  function handleSortDraftChange(
    index: number,
    patch: Partial<SortDraftClause>,
  ) {
    setSortDrafts((current) =>
      current.map((draft, draftIndex) =>
        draftIndex === index
          ? {
              ...draft,
              ...patch,
              dir: patch.column === "" ? "asc" : (patch.dir ?? draft.dir),
            }
          : draft,
      ),
    );
  }

  function handleAddSortLevel() {
    setSortDrafts((current) => [...current, createEmptySortDraft()]);
  }

  function handleRemoveSortLevel(index: number) {
    setSortDrafts((current) => {
      const next = current.filter((_, currentIndex) => currentIndex !== index);
      return next.length > 0 ? next : [createEmptySortDraft()];
    });
  }

  function handleApplySort() {
    const seen = new Set<string>();
    const nextSorts: SortClause[] = [];

    for (const draft of sortDrafts) {
      if (!draft.column) continue;
      if (seen.has(draft.column)) continue;
      seen.add(draft.column);
      nextSorts.push({ column: draft.column, dir: draft.dir });
    }

    onSortChange(nextSorts);
  }

  function handleClearSortDrafts() {
    setSortDrafts([createEmptySortDraft()]);
    onSortChange([]);
  }

  function handleAddFilter() {
    if (!canAddFilter) return;

    const nextFilter: FilterClause = nullaryFilterOps.includes(filterOp)
      ? { column: filterColumn, op: filterOp }
      : { column: filterColumn, op: filterOp, value: filterValue.trim() };

    if (editingFilterIndex !== null) {
      onFiltersChange(
        query.filters.map((filter, index) =>
          index === editingFilterIndex ? nextFilter : filter,
        ),
      );
      setEditingFilterIndex(null);
    } else {
      onFiltersChange([...query.filters, nextFilter]);
    }
    setFilterValue("");
  }

  function handleStartEditingFilter(index: number) {
    const filter = query.filters[index];
    if (!filter) return;

    setFilterColumn(filter.column);
    setFilterOp(filter.op);
    setFilterValue(filter.value ?? "");
    setEditingFilterIndex(index);
    setActivePanel("filter");
  }

  function handleRemoveFilter(index: number) {
    onFiltersChange(
      query.filters.filter((_, currentIndex) => currentIndex !== index),
    );
    if (editingFilterIndex === null) return;
    if (editingFilterIndex === index) {
      setEditingFilterIndex(null);
      setFilterValue("");
      return;
    }
    if (editingFilterIndex > index) {
      setEditingFilterIndex(editingFilterIndex - 1);
    }
  }

  function handleCancelEditingFilter() {
    setEditingFilterIndex(null);
    setFilterValue("");
  }

  function handleStartEditingSearch() {
    setEditingFilterIndex(null);
    setActivePanel("search");
  }

  function handleClearSearch() {
    setEditingFilterIndex(null);
    setSearchDraft("");
    onSearch("");
  }

  function handleStartEditingSort() {
    setEditingFilterIndex(null);
    setActivePanel("sort");
  }

  function handleClearSort() {
    setEditingFilterIndex(null);
    handleClearSortDrafts();
  }

  function handleToggleColumnVisibility(columnName: string) {
    if (!columnName) return;

    if (hiddenColumnSet.has(columnName)) {
      onHiddenColumnsChange(
        hiddenColumnNames.filter((name) => name !== columnName),
      );
      return;
    }

    onHiddenColumnsChange([...hiddenColumnNames, columnName]);
  }

  function handleShowAllColumns() {
    if (hiddenColumnNames.length === 0) return;
    onHiddenColumnsChange([]);
  }

  const showActiveChips =
    query.search ||
    query.sort.length > 0 ||
    query.filters.length > 0 ||
    query.perPage !== 25;

  const activeView = useMemo(
    () =>
      activeViewId !== null
        ? savedViews.find((sv) => sv.id === activeViewId) ?? null
        : null,
    [savedViews, activeViewId],
  );

  const hasViewModifications = useMemo(() => {
    if (activeView) {
      return currentViewConfig
        ? !areViewConfigsEquivalent(currentViewConfig, activeView.config)
        : false;
    }

    const hasConfigChanges = currentViewConfig
      ? !areViewConfigsEquivalent(
          currentViewConfig,
          DEFAULT_EXPLORER_VIEW_CONFIG,
        )
      : !!query.search || query.sort.length > 0 || query.filters.length > 0;

    return hasConfigChanges || query.perPage !== 25;
  }, [activeView, currentViewConfig, query.perPage, query.search, query.sort, query.filters]);

  const currentViewName = activeView?.name ?? "All Records";

  /** Does the current user own the active saved view? (false for All Records or others' shared views) */
  const ownsActiveView = useMemo(() => {
    if (!activeView || currentUserId === null) return false;
    return activeView.owner_user_id === currentUserId;
  }, [activeView, currentUserId]);

  /** Saved views sorted: own views first, then shared views from other users. */
  const sortedSavedViews = useMemo(() => {
    if (currentUserId === null) return savedViews;
    const own: SavedView[] = [];
    const shared: SavedView[] = [];
    for (const sv of savedViews) {
      if (sv.owner_user_id === currentUserId) own.push(sv);
      else shared.push(sv);
    }
    return [...own, ...shared];
  }, [savedViews, currentUserId]);

  const hasEnumColumns = useMemo(
    () =>
      table.columns.some(
        (c) => (c.type ?? "").toLowerCase() === "enum",
      ),
    [table.columns],
  );

  async function handleSaveChanges() {
    await onUpdateActiveViewConfig?.();
  }

  async function handleCreateView() {
    const name = createViewName.trim();
    if (!name) return;
    setCreateViewSaving(true);
    try {
      await onSaveCurrentAsView?.(name, createViewTypeDraft);
      setCreateViewOpen(false);
      setCreateViewName("");
    } finally {
      setCreateViewSaving(false);
    }
  }

  async function handleRenameView() {
    const name = renameDraft.trim();
    if (!name || activeViewId === null) return;
    setRenameSaving(true);
    try {
      await onRenameView?.(activeViewId, name);
      if (viewTypeDraft !== activeView?.type) {
        await onChangeViewType?.(activeViewId, viewTypeDraft);
      }
      setSettingsOpen(false);
    } finally {
      setRenameSaving(false);
    }
  }

  async function handleDeleteView() {
    if (activeViewId === null) return;
    if (
      typeof window !== "undefined" &&
      !window.confirm(
        `Delete view "${activeView?.name ?? ""}"? This cannot be undone.`,
      )
    ) {
      return;
    }
    setDeletingView(true);
    try {
      await onDeleteView?.(activeViewId);
      setSettingsOpen(false);
    } finally {
      setDeletingView(false);
    }
  }

  async function handleDuplicateView() {
    if (activeViewId === null || !activeView) return;
    const suggested = `${activeView.name} (copy)`;
    const name =
      typeof window !== "undefined"
        ? (window.prompt("Name for the duplicated view:", suggested) ?? "").trim()
        : suggested;
    if (!name) return;
    setDuplicatingView(true);
    try {
      await onDuplicateView?.(activeViewId, name);
      setSettingsOpen(false);
    } finally {
      setDuplicatingView(false);
    }
  }

  function handleCreateViewKeyDown(event: React.KeyboardEvent<HTMLInputElement>) {
    if (event.key === "Enter" && createViewName.trim()) {
      void handleCreateView();
    } else if (event.key === "Escape") {
      setCreateViewOpen(false);
    }
  }

  function handleRenameKeyDown(event: React.KeyboardEvent<HTMLInputElement>) {
    if (event.key === "Enter" && renameDraft.trim()) {
      void handleRenameView();
    } else if (event.key === "Escape") {
      setSettingsOpen(false);
    }
  }

  return (
    <div className="data-controls" ref={rootRef}>
      <div className="data-controls__overlay-anchor" ref={overlayAnchorRef}>
        <div className="data-controls__bar">
          {/* LEFT — view selector dropdown, settings, save actions */}
          <div className="data-controls__view-selector">
            {/* Dropdown */}
            <div className="data-controls__view-dropdown" ref={viewDropdownRef}>
              <button
                type="button"
                className="data-controls__view-current"
                aria-haspopup="listbox"
                aria-expanded={viewDropdownOpen}
                aria-label={`Current view: ${currentViewName}`}
                onClick={() => setViewDropdownOpen((open) => !open)}
              >
                <span className="data-controls__view-current-label">
                  {currentViewName}
                </span>
                <Icon name="chevron-down" size={11} />
              </button>

              {viewDropdownOpen ? (
                <div
                  className="data-controls__view-menu"
                  role="listbox"
                  aria-label="Available views"
                >
                  <button
                    type="button"
                    role="option"
                    aria-selected={activeViewId === null}
                    className={
                      activeViewId === null
                        ? "data-controls__view-option data-controls__view-option--active"
                        : "data-controls__view-option"
                    }
                    onClick={() => {
                      onSwitchView?.(null);
                      setViewDropdownOpen(false);
                    }}
                  >
                    <span className="data-controls__view-option-check">
                      {activeViewId === null ? (
                        <Icon name="check" size={12} />
                      ) : null}
                    </span>
                    <span className="data-controls__view-option-label">
                      All Records
                    </span>
                  </button>

                  {sortedSavedViews.map((sv) => {
                    const isOwn =
                      currentUserId !== null &&
                      sv.owner_user_id === currentUserId;
                    const isShared = !isOwn;
                    return (
                      <button
                        key={sv.id}
                        type="button"
                        role="option"
                        aria-selected={activeViewId === sv.id}
                        className={
                          activeViewId === sv.id
                            ? "data-controls__view-option data-controls__view-option--active"
                            : "data-controls__view-option"
                        }
                        onClick={() => {
                          onSwitchView?.(sv.id);
                          setViewDropdownOpen(false);
                        }}
                      >
                        <span className="data-controls__view-option-check">
                          {activeViewId === sv.id ? (
                            <Icon name="check" size={12} />
                          ) : null}
                        </span>
                        <span className="data-controls__view-option-label">
                          {sv.name}
                        </span>
                        {isShared ? (
                          <span
                            className="data-controls__view-option-shared"
                            title="Shared by another user"
                            aria-label="Shared view"
                          >
                            <Icon name="share" size={11} />
                          </span>
                        ) : null}
                        {defaultViewId === sv.id ? (
                          <span
                            className="data-controls__view-option-default"
                            title="Default view"
                            aria-label="Default view"
                          >
                            <Icon name="star" size={11} />
                          </span>
                        ) : null}
                      </button>
                    );
                  })}

                  <div
                    className="data-controls__view-separator"
                    role="separator"
                  />

                  <button
                    type="button"
                    role="option"
                    className="data-controls__view-option data-controls__view-option--new"
                    onClick={() => {
                      setViewDropdownOpen(false);
                      setCreateViewName("");
                      setCreateViewTypeDraft(
                        viewType === "kanban" && hasEnumColumns ? "kanban" : "table",
                      );
                      setCreateViewOpen(true);
                    }}
                  >
                    <span className="data-controls__view-option-check">
                      <Icon name="plus" size={12} />
                    </span>
                    <span className="data-controls__view-option-label">
                      New view
                    </span>
                  </button>
                </div>
              ) : null}
            </div>

            {/* Settings icon — always visible; disabled on "All Records" */}
            <button
              type="button"
              className="data-controls__view-settings"
              title={
                activeViewId !== null
                  ? "View settings"
                  : "Select a saved view to edit settings"
              }
              aria-label="View settings"
              disabled={activeViewId === null}
              onClick={() => {
                if (!activeView) return;
                setRenameDraft(activeView.name);
                setViewTypeDraft(activeView.type);
                setSettingsOpen(true);
              }}
            >
              <Icon name="settings" size={13} />
            </button>

            {/* Save changes — only when on a saved view with unsaved modifications */}
            {hasViewModifications && activeViewId !== null ? (
              <Button
                type="button"
                variant="ghost"
                size="sm"
                onClick={() => void handleSaveChanges()}
              >
                Save changes
              </Button>
            ) : null}

            {/* Save to new view — whenever there are unsaved modifications */}
            {hasViewModifications ? (
              <Button
                type="button"
                variant="ghost"
                size="sm"
                onClick={() => {
                  setCreateViewName("");
                  setCreateViewTypeDraft(
                    viewType === "kanban" && hasEnumColumns ? "kanban" : "table",
                  );
                  setCreateViewOpen(true);
                }}
              >
                Save to new view
              </Button>
            ) : null}
          </div>
          <div className="data-controls__menu" ref={menuRef}>
            {toolbarActions}
            {selectionPanel ? (
              <Button
                type="button"
                variant="ghost"
                className={
                  activePanel === "selection"
                    ? "data-controls__menu-button data-controls__menu-button--active"
                    : "data-controls__menu-button"
                }
                aria-label="Selection operations"
                aria-expanded={activePanel === "selection"}
                aria-controls="data-controls-selection-panel"
                onClick={() =>
                  setActivePanel((current) =>
                    current === "selection" ? null : "selection",
                  )
                }
              >
                <span className="data-controls__menu-button-content">
                  <Icon name="check" size={14} />
                  <Icon
                    name={
                      activePanel === "selection"
                        ? "chevron-down"
                        : "chevron-right"
                    }
                    size={15}
                  />
                </span>
                {(selectionPanel.count ?? 0) > 0 ? (
                  <span
                    className="data-controls__menu-count"
                    aria-hidden="true"
                  >
                    {selectionPanel.count}
                  </span>
                ) : null}
              </Button>
            ) : null}
            <Button
              type="button"
              variant="ghost"
              className={
                activePanel === "search"
                  ? "data-controls__menu-button data-controls__menu-button--active"
                  : "data-controls__menu-button"
              }
              aria-label="Search"
              aria-expanded={activePanel === "search"}
              aria-controls="data-controls-search-panel"
              onClick={() =>
                setActivePanel((current) =>
                  current === "search" ? null : "search",
                )
              }
            >
              <span className="data-controls__menu-button-content">
                <Icon name="search" size={14} />
                <span className="data-controls__menu-button-label">Search</span>
              </span>
              {query.search ? (
                <span
                  className="data-controls__menu-count"
                  aria-hidden="true"
                ></span>
              ) : null}
            </Button>
            <Button
              type="button"
              variant="ghost"
              className={
                activePanel === "sort"
                  ? "data-controls__menu-button data-controls__menu-button--active"
                  : "data-controls__menu-button"
              }
              aria-label="Sort"
              aria-expanded={activePanel === "sort"}
              aria-controls="data-controls-sort-panel"
              onClick={() =>
                setActivePanel((current) =>
                  current === "sort" ? null : "sort",
                )
              }
            >
              <span className="data-controls__menu-button-content">
                <Icon name="arrow-up-down" size={14} />
                <span className="data-controls__menu-button-label">Sort</span>
              </span>
              {query.sort.length > 0 ? (
                <span className="data-controls__menu-count" aria-hidden="true">
                  {query.sort.length}
                </span>
              ) : null}
            </Button>
            {viewType === "kanban" ? (
              <Button
                type="button"
                variant="ghost"
                className="data-controls__menu-button"
                aria-label="Configure kanban"
                onClick={() => onConfigureKanban?.()}
              >
                <span className="data-controls__menu-button-content">
                  <Icon name="columns" size={14} />
                  <span className="data-controls__menu-button-label">Configure</span>
                </span>
              </Button>
            ) : (
              <Button
                type="button"
                variant="ghost"
                className={
                  activePanel === "columns"
                    ? "data-controls__menu-button data-controls__menu-button--active"
                    : "data-controls__menu-button"
                }
                aria-label="Columns"
                aria-expanded={activePanel === "columns"}
                aria-controls="data-controls-columns-panel"
                onClick={() =>
                  setActivePanel((current) =>
                    current === "columns" ? null : "columns",
                  )
                }
              >
                <span className="data-controls__menu-button-content">
                  <Icon name="columns" size={14} />
                  <span className="data-controls__menu-button-label">Columns</span>
                </span>
                {hiddenColumnNames.length > 0 ? (
                  <span className="data-controls__menu-count" aria-hidden="true">
                    {hiddenColumnNames.length}
                  </span>
                ) : null}
              </Button>
            )}
            <Button
              type="button"
              variant="ghost"
              className={
                activePanel === "filter"
                  ? "data-controls__menu-button data-controls__menu-button--active"
                  : "data-controls__menu-button"
              }
              aria-label="Filter"
              aria-expanded={activePanel === "filter"}
              aria-controls="data-controls-filter-panel"
              onClick={() =>
                setActivePanel((current) =>
                  current === "filter" ? null : "filter",
                )
              }
            >
              <span className="data-controls__menu-button-content">
                <Icon name="funnel" size={14} />
                <span className="data-controls__menu-button-label">Filter</span>
              </span>
              {query.filters.length > 0 ? (
                <span className="data-controls__menu-count" aria-hidden="true">
                  {query.filters.length}
                </span>
              ) : null}
            </Button>
          </div>
        </div>

        {activePanel === "search" ? (
          <div
            className="data-controls__panel data-controls__panel--floating"
            id="data-controls-search-panel"
          >
            <form
              className="data-controls__search"
              onSubmit={handleSearchSubmit}
            >
              <TextField
                label="Search rows"
                name="search"
                placeholder="Search this table"
                value={searchDraft}
                onChange={(event) => setSearchDraft(event.target.value)}
                disabled={disabled}
              />
              <div className="data-controls__search-actions">
                <Button type="submit" variant="primary" disabled={disabled}>
                  Apply search
                </Button>
                {query.search ? (
                  <Button
                    type="button"
                    variant="ghost"
                    disabled={disabled}
                    onClick={() => {
                      setSearchDraft("");
                      onSearch("");
                    }}
                  >
                    Clear search
                  </Button>
                ) : null}
              </div>
            </form>
          </div>
        ) : null}

        {activePanel === "sort" ? (
          <div
            className="data-controls__panel data-controls__panel--floating"
            id="data-controls-sort-panel"
          >
            <div className="data-controls__sort-builder">
              {sortDrafts.map((draft, index) => {
                const selectedSortColumn = table.columns.find(
                  (column) => column.name === draft.column,
                );
                const directionOptions =
                  getSortDirectionOptions(selectedSortColumn);

                return (
                  <div
                    key={`sort-line-${index}`}
                    className="data-controls__sort-row"
                  >
                    <SelectField
                      label={
                        index === 0 ? "Sort field" : `Then by ${index + 1}`
                      }
                      name={`sort-field-${index}`}
                      value={draft.column}
                      onChange={(event) =>
                        handleSortDraftChange(index, {
                          column: event.target.value,
                        })
                      }
                      options={sortFieldOptions}
                      disabled={disabled}
                    />
                    <SelectField
                      label="Direction"
                      name={`sort-direction-${index}`}
                      value={draft.dir}
                      onChange={(event) =>
                        handleSortDraftChange(index, {
                          dir: event.target.value as SortDir,
                        })
                      }
                      options={directionOptions}
                      disabled={disabled || !draft.column}
                    />
                    <div className="data-controls__sort-row-actions">
                      {sortDrafts.length > 1 ? (
                        <Button
                          type="button"
                          variant="ghost"
                          size="icon"
                          aria-label={`Remove sort level ${index + 1}`}
                          onClick={() => handleRemoveSortLevel(index)}
                          disabled={disabled}
                        >
                          <Icon name="x" size={14} />
                        </Button>
                      ) : (
                        <span
                          className="data-controls__sort-row-spacer"
                          aria-hidden="true"
                        />
                      )}
                    </div>
                  </div>
                );
              })}

              <div className="data-controls__sort-actions">
                <Button
                  type="button"
                  variant="ghost"
                  onClick={handleAddSortLevel}
                  disabled={disabled}
                  leadingIcon={<Icon name="plus" size={14} />}
                >
                  Add sort level
                </Button>
                <div className="data-controls__sort-actions-group">
                  <Button
                    type="button"
                    variant="primary"
                    onClick={handleApplySort}
                    disabled={disabled}
                  >
                    Apply sort
                  </Button>
                  {query.sort.length > 0 ||
                  sortDrafts.some((draft) => draft.column) ? (
                    <Button
                      type="button"
                      variant="ghost"
                      onClick={handleClearSortDrafts}
                      disabled={disabled}
                    >
                      Clear sort
                    </Button>
                  ) : null}
                </div>
              </div>
            </div>
          </div>
        ) : null}

        {activePanel === "filter" ? (
          <div
            className="data-controls__panel data-controls__panel--floating"
            id="data-controls-filter-panel"
          >
            <div className="data-controls__filters-builder">
              <SelectField
                label="Filter column"
                name="filter-column"
                value={filterColumn}
                onChange={(event) => {
                  setFilterColumn(event.target.value);
                  setFilterValue("");
                }}
                options={filterColumnOptions}
                disabled={disabled}
              />
              <SelectField
                label="Operator"
                name="filter-op"
                value={filterOp}
                onChange={(event) =>
                  setFilterOp(event.target.value as FilterOp)
                }
                options={filterOperatorOptions}
                disabled={disabled}
              />
              {!nullaryFilterOps.includes(filterOp) ? (
                selectedFilterColumnType === "enum" &&
                selectedFilterColumn?.values?.length ? (
                  <EnumValueDropdown
                    label={
                      filterOp === "in"
                        ? "Choose values"
                        : getFilterValueLabel(selectedFilterColumn, filterOp)
                    }
                    placeholder={
                      filterOp === "in"
                        ? "Choose values"
                        : "Choose a value"
                    }
                    options={selectedFilterColumn.values}
                    values={
                      filterOp === "in"
                        ? selectedEnumValues
                        : filterValue
                          ? [filterValue]
                          : []
                    }
                    multiple={filterOp === "in"}
                    disabled={disabled}
                    onChange={(values) =>
                      setFilterValue(
                        filterOp === "in"
                          ? values.join(",")
                          : (values[0] ?? ""),
                      )
                    }
                  />
                ) : selectedFilterColumnType === "boolean" ? (
                  <SelectField
                    label={getFilterValueLabel(selectedFilterColumn, filterOp)}
                    name="filter-value"
                    value={filterValue}
                    onChange={(event) => setFilterValue(event.target.value)}
                    options={[
                      { label: "Choose yes or no", value: "" },
                      { label: "Yes", value: "true" },
                      { label: "No", value: "false" },
                    ]}
                    disabled={disabled}
                  />
                ) : (
                  <TextField
                    label={getFilterValueLabel(selectedFilterColumn, filterOp)}
                    hint={filterOp === "in" ? "use , to separate" : undefined}
                    name="filter-value"
                    type={
                      selectedFilterColumnType === "date"
                        ? "date"
                        : selectedFilterColumnType === "datetime"
                          ? "datetime-local"
                          : "text"
                    }
                    step={
                      selectedFilterColumnType === "datetime" ? 1 : undefined
                    }
                    placeholder={getFilterValuePlaceholder(
                      selectedFilterColumn,
                      filterOp,
                    )}
                    value={filterValue}
                    onChange={(event) => setFilterValue(event.target.value)}
                    onKeyDown={(event) => {
                      if (event.key !== "Enter") return;
                      event.preventDefault();
                      handleAddFilter();
                    }}
                    disabled={disabled}
                  />
                )
              ) : (
                <div
                  className="data-controls__filter-spacer"
                  aria-hidden="true"
                />
              )}
              <Button
                type="button"
                variant="default"
                disabled={disabled || !canAddFilter}
                onClick={handleAddFilter}
              >
                {editingFilterIndex !== null ? "Save filter" : "Add filter"}
              </Button>
              {editingFilterIndex !== null ? (
                <Button
                  type="button"
                  variant="ghost"
                  disabled={disabled}
                  onClick={handleCancelEditingFilter}
                >
                  Cancel edit
                </Button>
              ) : null}
            </div>
          </div>
        ) : null}

        {activePanel === "columns" ? (
          <div
            className="data-controls__panel data-controls__panel--floating"
            id="data-controls-columns-panel"
          >
            <div className="data-controls__columns-builder">
              <div
                className="data-controls__columns-list"
                role="group"
                aria-label="Visible columns"
              >
                {table.columns.map((column) => {
                  const checked =
                    column.primaryKey || !hiddenColumnSet.has(column.name);
                  return (
                    <label
                      key={column.name}
                      className={
                        column.primaryKey
                          ? "data-controls__column-toggle data-controls__column-toggle--locked"
                          : "data-controls__column-toggle"
                      }
                    >
                      <input
                        type="checkbox"
                        checked={checked}
                        disabled={column.primaryKey || disabled}
                        onChange={() =>
                          handleToggleColumnVisibility(column.name)
                        }
                      />
                      <span className="data-controls__column-toggle-copy">
                        <span className="data-controls__column-toggle-name">
                          {column.displayName ?? column.name}
                        </span>
                      </span>
                    </label>
                  );
                })}
              </div>
              <div className="data-controls__columns-actions">
                <Button
                  type="button"
                  variant="ghost"
                  onClick={handleShowAllColumns}
                  disabled={disabled || hiddenColumnNames.length === 0}
                >
                  Show all columns
                </Button>
                <span className="data-controls__columns-summary">
                  {hidableColumns.length - hiddenColumnNames.length} visible
                  {" · "}
                  {hiddenColumnNames.length} hidden
                </span>
              </div>
            </div>
          </div>
        ) : null}

        {activePanel === "selection" && selectionPanel ? (
          <div
            className="data-controls__panel data-controls__panel--floating"
            id="data-controls-selection-panel"
          >
            {selectionPanel.content}
          </div>
        ) : null}
      </div>

      {/* ── Active-query chips bar ── */}
      {showActiveChips ? (
        <div className="data-controls__active">
          <div className="data-controls__chips">
            {searchLabel ? (
              <span className="data-controls__chip data-controls__chip--editable">
                <span className="data-controls__chip-label">{searchLabel}</span>
                <span className="data-controls__chip-actions">
                  <button
                    type="button"
                    className="data-controls__chip-action"
                    onClick={handleStartEditingSearch}
                    aria-label={`Edit ${searchLabel}`}
                    title="Edit search"
                  >
                    <Icon name="pencil" size={12} />
                  </button>
                  <button
                    type="button"
                    className="data-controls__chip-action"
                    onClick={handleClearSearch}
                    aria-label={`Remove ${searchLabel}`}
                    title="Remove search"
                  >
                    <Icon name="x" size={12} />
                  </button>
                </span>
              </span>
            ) : null}
            {sortLabel ? (
              <span className="data-controls__chip data-controls__chip--editable">
                <span className="data-controls__chip-label">{sortLabel}</span>
                <span className="data-controls__chip-actions">
                  <button
                    type="button"
                    className="data-controls__chip-action"
                    onClick={handleStartEditingSort}
                    aria-label={`Edit ${sortLabel}`}
                    title="Edit sort"
                  >
                    <Icon name="pencil" size={12} />
                  </button>
                  <button
                    type="button"
                    className="data-controls__chip-action"
                    onClick={handleClearSort}
                    aria-label={`Remove ${sortLabel}`}
                    title="Remove sort"
                  >
                    <Icon name="x" size={12} />
                  </button>
                </span>
              </span>
            ) : null}
            {query.filters.map((filter, index) => (
              <span
                key={`${filter.column}-${filter.op}-${filter.value ?? "null"}-${index}`}
                className="data-controls__chip data-controls__chip--filter data-controls__chip--editable"
              >
                <span className="data-controls__chip-label">
                  {formatFilterChip(filter, table.columns)}
                </span>
                <span className="data-controls__chip-actions">
                  <button
                    type="button"
                    className="data-controls__chip-action"
                    onClick={() => handleStartEditingFilter(index)}
                    aria-label={`Edit filter ${formatFilterChip(filter, table.columns)}`}
                    title="Edit filter"
                  >
                    <Icon name="pencil" size={12} />
                  </button>
                  <button
                    type="button"
                    className="data-controls__chip-action"
                    onClick={() => handleRemoveFilter(index)}
                    aria-label={`Remove filter ${formatFilterChip(filter, table.columns)}`}
                    title="Remove filter"
                  >
                    <Icon name="x" size={12} />
                  </button>
                </span>
              </span>
            ))}
            <Button
              type="button"
              variant="ghost"
              size="sm"
              onClick={onResetView}
              disabled={disabled}
            >
              Reset view
            </Button>
          </div>
        </div>
      ) : null}

      {/* ── Create / save-as-new view modal ── */}
      {createViewOpen ? (
        <div
          className="view-modal-overlay"
          role="presentation"
          onPointerDown={(e) => {
            if (e.target === e.currentTarget) setCreateViewOpen(false);
          }}
        >
          <div
            className="view-modal"
            role="dialog"
            aria-modal="true"
            aria-labelledby="create-view-title"
          >
            <div className="view-modal__header">
              <h2 className="view-modal__title" id="create-view-title">
                New view
              </h2>
              <button
                type="button"
                className="view-modal__close"
                aria-label="Close"
                onClick={() => setCreateViewOpen(false)}
              >
                <Icon name="x" size={16} />
              </button>
            </div>
            <div className="view-modal__body">
              <TextField
                label="View name"
                name="create-view-name"
                value={createViewName}
                onChange={(e) => setCreateViewName(e.target.value)}
                onKeyDown={handleCreateViewKeyDown}
                placeholder="e.g. Open orders"
                autoFocus
              />

              <div className="view-modal__field">
                <span className="view-modal__field-label">View type</span>
                <div
                  className="view-modal__segmented"
                  role="radiogroup"
                  aria-label="View type"
                >
                  <button
                    type="button"
                    role="radio"
                    aria-checked={createViewTypeDraft === "table"}
                    className={
                      createViewTypeDraft === "table"
                        ? "view-modal__segmented-btn view-modal__segmented-btn--on"
                        : "view-modal__segmented-btn"
                    }
                    onClick={() => setCreateViewTypeDraft("table")}
                  >
                    Table
                  </button>
                  <button
                    type="button"
                    role="radio"
                    aria-checked={createViewTypeDraft === "kanban"}
                    disabled={!hasEnumColumns && createViewTypeDraft !== "kanban"}
                    title={
                      !hasEnumColumns ? "Kanban needs a choice column to group by." : undefined
                    }
                    className={
                      createViewTypeDraft === "kanban"
                        ? "view-modal__segmented-btn view-modal__segmented-btn--on"
                        : "view-modal__segmented-btn"
                    }
                    onClick={() => {
                      if (hasEnumColumns) setCreateViewTypeDraft("kanban");
                    }}
                  >
                    Kanban
                  </button>
                </div>
              </div>
            </div>
            <div className="view-modal__footer">
              <Button
                type="button"
                variant="ghost"
                onClick={() => setCreateViewOpen(false)}
              >
                Cancel
              </Button>
              <Button
                type="button"
                variant="primary"
                disabled={!createViewName.trim() || createViewSaving}
                onClick={() => void handleCreateView()}
              >
                {createViewSaving ? "Saving…" : "Create view"}
              </Button>
            </div>
          </div>
        </div>
      ) : null}

      {/* ── View settings modal ── */}
      {settingsOpen && activeViewId !== null && activeView ? (
        <div
          className="view-modal-overlay"
          role="presentation"
          onPointerDown={(e) => {
            if (e.target === e.currentTarget) setSettingsOpen(false);
          }}
        >
          <div
            className="view-modal"
            role="dialog"
            aria-modal="true"
            aria-labelledby="settings-view-title"
          >
            <div className="view-modal__header">
              <h2 className="view-modal__title" id="settings-view-title">
                View settings
              </h2>
              <button
                type="button"
                className="view-modal__close"
                aria-label="Close"
                onClick={() => setSettingsOpen(false)}
              >
                <Icon name="x" size={16} />
              </button>
            </div>
            <div className="view-modal__body">
              {ownsActiveView ? (
                <>
                  <TextField
                    label="Name"
                    name="view-rename"
                    value={renameDraft}
                    onChange={(e) => setRenameDraft(e.target.value)}
                    onKeyDown={handleRenameKeyDown}
                    autoFocus
                  />

                  {/* View type segmented control */}
                  <div className="view-modal__field">
                    <span className="view-modal__field-label">View type</span>
                    <div
                      className="view-modal__segmented"
                      role="radiogroup"
                      aria-label="View type"
                    >
                      <button
                        type="button"
                        role="radio"
                        aria-checked={viewTypeDraft === "table"}
                        className={
                          viewTypeDraft === "table"
                            ? "view-modal__segmented-btn view-modal__segmented-btn--on"
                            : "view-modal__segmented-btn"
                        }
                        onClick={() => setViewTypeDraft("table")}
                      >
                        Table
                      </button>
                      <button
                        type="button"
                        role="radio"
                        aria-checked={viewTypeDraft === "kanban"}
                        disabled={
                          !hasEnumColumns && viewTypeDraft !== "kanban"
                        }
                        title={
                          !hasEnumColumns
                            ? "Kanban needs a choice column to group by."
                            : undefined
                        }
                        className={
                          viewTypeDraft === "kanban"
                            ? "view-modal__segmented-btn view-modal__segmented-btn--on"
                            : "view-modal__segmented-btn"
                        }
                        onClick={() => {
                          if (hasEnumColumns) setViewTypeDraft("kanban");
                        }}
                      >
                        Kanban
                      </button>
                    </div>
                  </div>

                  <div className="view-modal__toggle-row">
                    <div className="view-modal__toggle-info">
                      <span className="view-modal__toggle-name">
                        Share with everyone
                      </span>
                      <span className="view-modal__toggle-hint">
                        Other users can see and use this view
                      </span>
                    </div>
                    <button
                      type="button"
                      className={
                        activeView.is_shared
                          ? "view-modal__toggle-btn view-modal__toggle-btn--on"
                          : "view-modal__toggle-btn"
                      }
                      role="switch"
                      aria-checked={activeView.is_shared}
                      aria-label="Share with everyone"
                      onClick={() => {
                        if (activeViewId !== null) {
                          void onToggleViewShared?.(activeViewId);
                        }
                      }}
                    >
                      <Icon
                        name={
                          activeView.is_shared ? "toggle-right" : "toggle-left"
                        }
                        size={28}
                      />
                    </button>
                  </div>

                  <div className="view-modal__toggle-row">
                    <div className="view-modal__toggle-info">
                      <span className="view-modal__toggle-name">Auto-save</span>
                      <span className="view-modal__toggle-hint">
                        Save changes to this view automatically
                      </span>
                    </div>
                    <button
                      type="button"
                      className={
                        autoSave
                          ? "view-modal__toggle-btn view-modal__toggle-btn--on"
                          : "view-modal__toggle-btn"
                      }
                      role="switch"
                      aria-checked={autoSave}
                      aria-label="Auto-save"
                      onClick={() => onSetAutoSave?.(!autoSave)}
                    >
                      <Icon
                        name={autoSave ? "toggle-right" : "toggle-left"}
                        size={28}
                      />
                    </button>
                  </div>

                  <div className="view-modal__toggle-row">
                    <div className="view-modal__toggle-info">
                      <span className="view-modal__toggle-name">
                        Default view
                      </span>
                      <span className="view-modal__toggle-hint">
                        Open this view automatically when the table loads
                      </span>
                    </div>
                    <button
                      type="button"
                      className={
                        defaultViewId === activeViewId
                          ? "view-modal__toggle-btn view-modal__toggle-btn--on"
                          : "view-modal__toggle-btn"
                      }
                      role="switch"
                      aria-checked={defaultViewId === activeViewId}
                      aria-label="Default view"
                      onClick={() => {
                        if (activeViewId === null) return;
                        void onSetDefaultView?.(
                          defaultViewId === activeViewId ? null : activeViewId,
                        );
                      }}
                    >
                      <Icon
                        name={
                          defaultViewId === activeViewId
                            ? "toggle-right"
                            : "toggle-left"
                        }
                        size={28}
                      />
                    </button>
                  </div>
                </>
              ) : (
                <>
                  <div className="view-modal__field">
                    <span className="view-modal__field-label">Name</span>
                    <span className="view-modal__readonly-value">
                      {activeView.name}
                    </span>
                  </div>
                  <p className="view-modal__note">
                    This view is shared by another user. You can't edit it
                    directly — duplicate it to get your own editable copy.
                  </p>
                </>
              )}
            </div>
            <div className="view-modal__footer view-modal__footer--split">
              {ownsActiveView ? (
                <Button
                  type="button"
                  variant="danger"
                  size="sm"
                  onClick={() => void handleDeleteView()}
                  disabled={deletingView}
                >
                  {deletingView ? "Deleting…" : "Delete view"}
                </Button>
              ) : (
                <span />
              )}
              <div className="view-modal__footer-actions">
                <Button
                  type="button"
                  variant="ghost"
                  onClick={() => setSettingsOpen(false)}
                >
                  Cancel
                </Button>
                {ownsActiveView ? (
                  <Button
                    type="button"
                    variant="primary"
                    disabled={!renameDraft.trim() || renameSaving}
                    onClick={() => void handleRenameView()}
                  >
                    {renameSaving ? "Saving…" : "Save"}
                  </Button>
                ) : (
                  <Button
                    type="button"
                    variant="primary"
                    disabled={duplicatingView}
                    onClick={() => void handleDuplicateView()}
                  >
                    <Icon name="copy" size={14} />
                    {duplicatingView ? "Duplicating…" : "Duplicate as my view"}
                  </Button>
                )}
              </div>
            </div>
          </div>
        </div>
      ) : null}
    </div>
  );
}
