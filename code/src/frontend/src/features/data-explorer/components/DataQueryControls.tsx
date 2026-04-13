import {
  useEffect,
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
  FilterClause,
  FilterOp,
  SortClause,
  SortDir,
} from "../../../types/explorer";

type DataQueryControlsProps = {
  table: ModelTable;
  query: ExplorerQuery;
  total: number;
  totalPages: number;
  disabled?: boolean;
  hiddenColumnNames?: string[];
  selectionPanel?: { content: ReactNode; count?: number } | null;
  suppressAutoPanel?: boolean;
  panelResetKey?: string;
  filterShortcut?: { columnName: string; nonce: number } | null;
  sortShortcut?: { columnName: string; nonce: number } | null;
  onSearch: (search: string) => void;
  onSortChange: (sorts: SortClause[]) => void;
  onPerPageChange: (perPage: number) => void;
  onPageChange: (page: number) => void;
  onFiltersChange: (filters: FilterClause[]) => void;
  onHiddenColumnsChange: (columnNames: string[]) => void;
  onResetView: () => void;
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
  { label: "Equals (=)", value: "eq" },
  { label: "Does not equal (!=)", value: "neq" },
  { label: "Contains (~)", value: "like" },
  { label: "Greater than (>)", value: "gt" },
  { label: "Greater or equal (>=)", value: "gte" },
  { label: "Less than (<)", value: "lt" },
  { label: "Less or equal (<=)", value: "lte" },
  { label: "Is empty (null)", value: "null" },
  { label: "Is not empty (!null)", value: "notnull" },
];

const nullaryFilterOps: FilterOp[] = ["null", "notnull"];
const textLikeColumnTypes = new Set(["string", "text", "json", "relation"]);
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

  if (normalizedType === "enum") {
    return [
      { label: "Equals (=)", value: "eq" },
      { label: "Does not equal (!=)", value: "neq" },
      { label: "One of (in)", value: "in" },
      { label: "Is empty (null)", value: "null" },
      { label: "Is not empty (!null)", value: "notnull" },
    ];
  }

  if (normalizedType === "boolean") {
    return defaultFilterOperatorOptions.filter((option) =>
      ["eq", "neq", "null", "notnull"].includes(option.value),
    );
  }

  if (numericColumnTypes.has(normalizedType)) {
    return defaultFilterOperatorOptions.filter((option) =>
      ["eq", "neq", "gt", "gte", "lt", "lte", "null", "notnull"].includes(
        option.value,
      ),
    );
  }

  if (temporalColumnTypes.has(normalizedType)) {
    const temporalNoun = normalizedType === "datetime" ? "date/time" : "date";
    return [
      { label: `On ${temporalNoun} (=)`, value: "eq" },
      { label: `Not on ${temporalNoun} (!=)`, value: "neq" },
      { label: `After ${temporalNoun} (>)`, value: "gt" },
      { label: `On or after ${temporalNoun} (>=)`, value: "gte" },
      { label: `Before ${temporalNoun} (<)`, value: "lt" },
      { label: `On or before ${temporalNoun} (<=)`, value: "lte" },
      { label: "Is empty (null)", value: "null" },
      { label: "Is not empty (!null)", value: "notnull" },
    ];
  }

  if (textLikeColumnTypes.has(normalizedType)) {
    return defaultFilterOperatorOptions.filter((option) =>
      ["eq", "neq", "like", "null", "notnull"].includes(option.value),
    );
  }

  return defaultFilterOperatorOptions.filter((option) =>
    ["eq", "neq", "null", "notnull"].includes(option.value),
  );
}

function getFilterValueLabel(column: ModelColumn | undefined) {
  const normalizedType = normalizeColumnType(column?.type);
  if (normalizedType === "enum") return "Select value";
  if (normalizedType === "boolean") return "Choose value";
  if (normalizedType === "date") return "Choose date";
  if (normalizedType === "datetime") return "Choose date and time";
  return "Value";
}

function getFilterValuePlaceholder(column: ModelColumn | undefined) {
  const normalizedType = normalizeColumnType(column?.type);
  if (normalizedType === "enum") return undefined;
  if (normalizedType === "boolean") return undefined;
  if (normalizedType === "date") return undefined;
  if (normalizedType === "datetime") return undefined;
  return "Value";
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
      { label: "False first", value: "asc" },
      { label: "True first", value: "desc" },
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

function formatFilterChip(filter: FilterClause): string {
  switch (filter.op) {
    case "eq":
      return `${filter.column} = ${filter.value ?? "—"}`;
    case "neq":
      return `${filter.column} != ${filter.value ?? "—"}`;
    case "like":
      return `${filter.column} contains ${filter.value ?? "—"}`;
    case "gt":
      return `${filter.column} > ${filter.value ?? "—"}`;
    case "gte":
      return `${filter.column} >= ${filter.value ?? "—"}`;
    case "lt":
      return `${filter.column} < ${filter.value ?? "—"}`;
    case "lte":
      return `${filter.column} <= ${filter.value ?? "—"}`;
    case "null":
      return `${filter.column} is empty`;
    case "notnull":
      return `${filter.column} is not empty`;
    case "in":
      return `${filter.column} one of ${
        (filter.value ?? "—")
          .split(",")
          .map((value) => value.trim())
          .filter(Boolean)
          .join(", ") || "—"
      }`;
    default:
      return `${filter.column} ${filter.op} ${filter.value ?? "—"}`;
  }
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
  total,
  totalPages,
  disabled = false,
  hiddenColumnNames = [],
  selectionPanel = null,
  suppressAutoPanel = false,
  panelResetKey,
  filterShortcut = null,
  sortShortcut = null,
  onSearch,
  onSortChange,
  onPerPageChange,
  onPageChange,
  onFiltersChange,
  onHiddenColumnsChange,
  onResetView,
}: DataQueryControlsProps) {
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

  useEffect(() => {
    if (panelResetKey === undefined) return;
    setActivePanel(suppressAutoPanel ? null : getInitialPanel(query));
  }, [panelResetKey, suppressAutoPanel]);

  useEffect(() => {
    if (selectionPanel || activePanel !== "selection") return;
    setActivePanel(null);
  }, [activePanel, selectionPanel]);

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
      ? `Sort: ${query.sort.map((sort) => `${sort.column} ${sort.dir}`).join(", ")}`
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

  return (
    <div className="data-controls">
      <div className="data-controls__overlay-anchor">
        <div className="data-controls__bar">
          <div className="data-controls__menu">
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
                <Icon
                  name={
                    activePanel === "search" ? "chevron-down" : "chevron-right"
                  }
                  size={15}
                />
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
                <Icon
                  name={
                    activePanel === "sort" ? "chevron-down" : "chevron-right"
                  }
                  size={15}
                />
              </span>
              {query.sort.length > 0 ? (
                <span className="data-controls__menu-count" aria-hidden="true">
                  {query.sort.length}
                </span>
              ) : null}
            </Button>
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
                <Icon
                  name={
                    activePanel === "columns" ? "chevron-down" : "chevron-right"
                  }
                  size={15}
                />
              </span>
              {hiddenColumnNames.length > 0 ? (
                <span className="data-controls__menu-count" aria-hidden="true">
                  {hiddenColumnNames.length}
                </span>
              ) : null}
            </Button>
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
                <Icon
                  name={
                    activePanel === "filter" ? "chevron-down" : "chevron-right"
                  }
                  size={15}
                />
              </span>
              {query.filters.length > 0 ? (
                <span className="data-controls__menu-count" aria-hidden="true">
                  {query.filters.length}
                </span>
              ) : null}
            </Button>
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
          </div>

          <span className="data-controls__summary">
            {total} row{total !== 1 ? "s" : ""}
          </span>
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
                        : getFilterValueLabel(selectedFilterColumn)
                    }
                    placeholder={
                      filterOp === "in"
                        ? "Choose enum values"
                        : "Choose enum value"
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
                    label={getFilterValueLabel(selectedFilterColumn)}
                    name="filter-value"
                    value={filterValue}
                    onChange={(event) => setFilterValue(event.target.value)}
                    options={[
                      { label: "Choose true or false", value: "" },
                      { label: "true", value: "true" },
                      { label: "false", value: "false" },
                    ]}
                    disabled={disabled}
                  />
                ) : (
                  <TextField
                    label={getFilterValueLabel(selectedFilterColumn)}
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

      {showActiveChips ? (
        <div className="data-controls__active">
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
                {formatFilterChip(filter)}
              </span>
              <span className="data-controls__chip-actions">
                <button
                  type="button"
                  className="data-controls__chip-action"
                  onClick={() => handleStartEditingFilter(index)}
                  aria-label={`Edit filter ${formatFilterChip(filter)}`}
                  title="Edit filter"
                >
                  <Icon name="pencil" size={12} />
                </button>
                <button
                  type="button"
                  className="data-controls__chip-action"
                  onClick={() => handleRemoveFilter(index)}
                  aria-label={`Remove filter ${formatFilterChip(filter)}`}
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
      ) : null}
    </div>
  );
}
