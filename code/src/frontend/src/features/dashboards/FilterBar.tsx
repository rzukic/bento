import { useEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import type { DashboardFilter, DashboardWidget } from "../../api/dashboards";
import { runQuery } from "../../api/query";
import { Icon } from "../../components/common/Icon";
import { Button } from "../../components/ui/Button";
import type { ModelTable } from "../../types/api";
import { filterableColumns, tablesWithColumn } from "./dashboardFilters";

type Option = { value: string; label: string };

/** The table a filter's values come from: one the widgets use, else any with the column. */
function optionSource(filter: DashboardFilter, widgets: DashboardWidget[], tables: ModelTable[]): string | undefined {
  const withColumn = tablesWithColumn(tables, filter.column);
  return widgets.map((w) => w.data?.query?.source).find((s): s is string => Boolean(s) && withColumn.includes(s!)) ?? withColumn[0];
}

/** The values of a filter's column (record labels for links), most used first. */
function useOptions(filter: DashboardFilter, source: string | undefined): Option[] {
  const [options, setOptions] = useState<Option[]>([]);
  useEffect(() => {
    if (!source) return;
    let cancelled = false;
    runQuery({ source, groupBy: [{ column: filter.column }], measures: [{ name: "rows", fn: "count" }], sort: ["-rows"], limit: 200 })
      .then((result) => {
        if (cancelled) return;
        const labelKey = result.meta.columns.find((c) => c.role === "label" && c.labelOf === filter.column)?.name;
        setOptions(
          result.data
            .filter((row) => row[filter.column] !== null && row[filter.column] !== "")
            .map((row) => ({ value: String(row[filter.column]), label: String((labelKey ? row[labelKey] : null) ?? row[filter.column]) })),
        );
      })
      .catch(() => !cancelled && setOptions([]));
    return () => {
      cancelled = true;
    };
  }, [filter.column, source]);
  return options;
}

/**
 * The dashboard's filter bar (Step 3.8, Part 9 R9.6): one menu per filter;
 * the chosen values narrow every widget whose table has the column.
 */
export function FilterBar({
  filters,
  widgets,
  tables,
  selection,
  onSelect,
  onReset,
}: {
  filters: DashboardFilter[];
  widgets: DashboardWidget[];
  tables: ModelTable[];
  selection: Record<string, string[]>;
  onSelect: (column: string, values: string[]) => void;
  onReset: () => void;
}) {
  if (filters.length === 0) return null;
  const active = filters.some((f) => (selection[f.column] ?? []).length > 0);
  return (
    <div className="dashboard-filters" role="group" aria-label="Dashboard filters">
      {filters.map((filter) => (
        <FilterMenu
          key={filter.column}
          filter={filter}
          source={optionSource(filter, widgets, tables)}
          label={filter.label || tables.flatMap((t) => t.columns).find((c) => c.name === filter.column)?.displayName || filter.column}
          chosen={selection[filter.column] ?? []}
          onSelect={(values) => onSelect(filter.column, values)}
        />
      ))}
      {active ? (
        <Button type="button" variant="ghost" size="sm" onClick={onReset}>
          Reset filters
        </Button>
      ) : null}
    </div>
  );
}

function FilterMenu({
  filter,
  source,
  label,
  chosen,
  onSelect,
}: {
  filter: DashboardFilter;
  source: string | undefined;
  label: string;
  chosen: string[];
  onSelect: (values: string[]) => void;
}) {
  const [open, setOpen] = useState(false);
  const options = useOptions(filter, source);
  const ref = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!open) return;
    const onDown = (event: MouseEvent) => !ref.current?.contains(event.target as Node) && setOpen(false);
    const onKey = (event: KeyboardEvent) => event.key === "Escape" && setOpen(false);
    window.addEventListener("mousedown", onDown);
    window.addEventListener("keydown", onKey);
    return () => {
      window.removeEventListener("mousedown", onDown);
      window.removeEventListener("keydown", onKey);
    };
  }, [open]);

  const summary =
    chosen.length === 0 ? "All" : chosen.length === 1 ? (options.find((o) => o.value === chosen[0])?.label ?? chosen[0]) : `${chosen.length} selected`;
  const toggle = (value: string) => onSelect(chosen.includes(value) ? chosen.filter((v) => v !== value) : [...chosen, value]);

  return (
    <div className="dashboard-filter" ref={ref}>
      <button
        type="button"
        className={chosen.length > 0 ? "dashboard-filter__button dashboard-filter__button--active" : "dashboard-filter__button"}
        aria-haspopup="true"
        aria-expanded={open}
        onClick={() => setOpen((o) => !o)}
      >
        <span className="dashboard-filter__label">{label}</span>
        <span className="dashboard-filter__value">{summary}</span>
        <Icon name="chevron-down" size={12} />
      </button>
      {open ? (
        <div className="dashboard-filter__menu" role="group" aria-label={`${label} values`}>
          <label className="dashboard-filter__option">
            <input type="checkbox" checked={chosen.length === 0} onChange={() => onSelect([])} />
            All
          </label>
          {options.map((option) => (
            <label key={option.value} className="dashboard-filter__option">
              <input type="checkbox" checked={chosen.includes(option.value)} onChange={() => toggle(option.value)} />
              {option.label}
            </label>
          ))}
          {options.length === 0 ? <p className="dashboard-filter__empty">No values yet.</p> : null}
        </div>
      ) : null}
    </div>
  );
}

/** Edit mode: which filters the bar offers. */
export function FiltersEditor({
  filters,
  widgets,
  tables,
  onChange,
  onClose,
}: {
  filters: DashboardFilter[];
  widgets: DashboardWidget[];
  tables: ModelTable[];
  onChange: (filters: DashboardFilter[]) => void;
  onClose: () => void;
}) {
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => event.key === "Escape" && onClose();
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  // Columns of the tables the widgets use come first.
  const used = useMemo(() => new Set(widgets.map((w) => w.data?.query?.source).filter(Boolean)), [widgets]);
  const candidates = useMemo(() => {
    const all = filterableColumns(tables).filter((c) => !filters.some((f) => f.column === c.column));
    return [...all.filter((c) => c.tables.some((t) => used.has(t))), ...all.filter((c) => !c.tables.some((t) => used.has(t)))];
  }, [tables, filters, used]);
  const widgetsOn = (column: string) => {
    const on = tablesWithColumn(tables, column);
    return widgets.filter((w) => w.data?.query?.source && on.includes(w.data.query.source)).length;
  };

  const panel = (
    <aside className="widget-editor" aria-label="Dashboard filters">
      <header className="widget-editor__header">
        <h2>Filters</h2>
        <Button type="button" variant="ghost" size="sm" onClick={onClose} aria-label="Close filters">
          <Icon name="x" size={14} />
        </Button>
      </header>
      <div className="widget-editor__body">
        <p className="widget-editor__hint">
          Menus above the widgets, e.g. an account or a category. A filter narrows every widget, variable and drill-down whose table has the column.
        </p>
        {filters.map((filter, index) => (
          <section key={filter.column} className="widget-editor__section">
            <div className="widget-editor__row">
              <label className="widget-editor__field">
                <span className="widget-editor__label">Label</span>
                <input
                  className="field__control field__control--compact"
                  value={filter.label ?? ""}
                  placeholder={tables.flatMap((t) => t.columns).find((c) => c.name === filter.column)?.displayName || filter.column}
                  aria-label={`Label of filter ${filter.column}`}
                  onChange={(event) => onChange(filters.map((f, i) => (i === index ? { ...f, label: event.target.value || undefined } : f)))}
                />
              </label>
              <Button type="button" variant="ghost" size="sm" aria-label={`Remove filter ${filter.column}`} onClick={() => onChange(filters.filter((_, i) => i !== index))}>
                <Icon name="trash" size={13} />
              </Button>
            </div>
            <p className="widget-editor__hint">
              <code>{filter.column}</code> · on {tablesWithColumn(tables, filter.column).join(", ") || "no table"} · {widgetsOn(filter.column)} of {widgets.length} widgets
            </p>
          </section>
        ))}
        {candidates.length > 0 ? (
          <label className="widget-editor__field">
            <span className="widget-editor__label">Add a filter</span>
            <select
              className="field__control field__control--compact"
              value=""
              aria-label="Add a filter"
              onChange={(event) => event.target.value && onChange([...filters, { column: event.target.value }])}
            >
              <option value="">Choose a column…</option>
              {candidates.map((c) => (
                <option key={c.column} value={c.column}>
                  {c.label} ({c.tables.join(", ")})
                </option>
              ))}
            </select>
          </label>
        ) : null}
      </div>
      <footer className="widget-editor__footer">
        <Button type="button" variant="primary" onClick={onClose}>
          Done
        </Button>
      </footer>
    </aside>
  );
  return createPortal(panel, document.body);
}
