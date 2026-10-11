import { useEffect } from "react";
import { createPortal } from "react-dom";
import { Link } from "react-router-dom";
import type { BudgetConfig, DashboardWidget } from "../../api/dashboards";
import { Icon } from "../../components/common/Icon";
import { Button } from "../../components/ui/Button";
import { valueTypeOf } from "../../lib/columns";
import type { ModelSnapshot, ModelTable } from "../../types/api";
import { defaultBudget, limitColumns, limitTable } from "./budget";

const NUMERIC = new Set(["currency", "float", "integer", "decimal", "double"]);

/**
 * Settings of a budget widget (Step 4.5): where spending comes from (table,
 * amount, date, the link to categories) and the categories' column with
 * the monthly limit.
 */
export function BudgetEditor({
  widget,
  tables,
  snapshot,
  onChange,
  onClose,
}: {
  widget: DashboardWidget;
  tables: ModelTable[];
  snapshot: ModelSnapshot | null;
  onChange: (widget: DashboardWidget) => void;
  onClose: () => void;
}) {
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const config: BudgetConfig = widget.data?.budget ?? defaultBudget(tables, snapshot) ?? { source: "", amount: "", date: "", category: "", limit: "" };
  const set = (patch: Partial<BudgetConfig>) => onChange({ ...widget, data: { ...widget.data, budget: { ...config, ...patch } } });
  const source = tables.find((t) => t.name === config.source);
  const columns = source?.columns ?? [];
  const categories = limitTable(config, snapshot);
  const limits = limitColumns(categories);

  const select = (label: string, value: string, options: { value: string; label: string }[], onSelect: (value: string) => void) => (
    <label className="widget-editor__field">
      <span className="widget-editor__label">{label}</span>
      <select className="field__control field__control--compact" aria-label={label} value={value} onChange={(event) => onSelect(event.target.value)}>
        <option value="">Choose…</option>
        {options.map((o) => (
          <option key={o.value} value={o.value}>
            {o.label}
          </option>
        ))}
      </select>
    </label>
  );
  const named = (c: { name: string; displayName?: string | null }) => ({ value: c.name, label: c.displayName ?? c.name });

  const panel = (
    <aside className="widget-editor" aria-label="Budget settings">
      <header className="widget-editor__header">
        <h2>Budgets</h2>
        <Button type="button" variant="ghost" size="sm" onClick={onClose} aria-label="Close budget settings">
          <Icon name="x" size={14} />
        </Button>
      </header>
      <div className="widget-editor__body">
        <label className="widget-editor__field">
          <span className="widget-editor__label">Title</span>
          <input className="field__control field__control--compact" aria-label="Title" value={widget.title} onChange={(event) => onChange({ ...widget, title: event.target.value })} />
        </label>
        <p className="widget-editor__hint">
          Each category's monthly limit against what was spent — in the last month of the dashboard's range, or in the whole range. A row opens its bookings.
        </p>
        <section className="widget-editor__section">
          {select("Spending from", config.source, tables.map(named), (value) => {
            const table = tables.find((t) => t.name === value);
            const next = table ? defaultBudget([table], snapshot) : undefined;
            set(next ?? { source: value, amount: "", date: "", category: "", limit: "" });
          })}
          {select("Amount", config.amount, columns.filter((c) => NUMERIC.has(valueTypeOf(c)) && !c.primaryKey).map(named), (amount) => set({ amount }))}
          {select("Date", config.date, columns.filter((c) => ["date", "datetime"].includes(valueTypeOf(c))).map(named), (date) => set({ date }))}
          {select("Category", config.category, columns.filter((c) => c.type === "reference").map(named), (category) => set({ category, limit: "" }))}
          <label className="widget-editor__field">
            <span className="widget-editor__label">Spending is stored as</span>
            <select className="field__control field__control--compact" aria-label="Spending is stored as" value={config.expenses ?? "negative"} onChange={(event) => set({ expenses: event.target.value as BudgetConfig["expenses"] })}>
              <option value="negative">Negative amounts (a bank account)</option>
              <option value="positive">Positive amounts (an expenses table)</option>
            </select>
          </label>
          <label className="widget-editor__field">
            <span className="widget-editor__label">Period</span>
            <select className="field__control field__control--compact" aria-label="Period" value={config.period ?? "month"} onChange={(event) => set({ period: event.target.value as BudgetConfig["period"] })}>
              <option value="month">The range's last month (this month)</option>
              <option value="range">The whole range (limits × its months)</option>
            </select>
          </label>
        </section>
        <section className="widget-editor__section">
          {select(`Monthly limit in ${categories?.displayName ?? categories?.name ?? "the categories"}`, config.limit, limits.map(named), (limit) => set({ limit }))}
          {categories && limits.length === 0 ? (
            <p className="widget-editor__hint">
              {categories.displayName ?? categories.name} has no number column yet. Add one (e.g. <code>budget</code>, an amount) in the{" "}
              <Link to="/model">Schema Designer</Link> and enter each category's monthly limit.
            </p>
          ) : null}
        </section>
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
