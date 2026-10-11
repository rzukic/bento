import { useEffect } from "react";
import { createPortal } from "react-dom";
import { Link } from "react-router-dom";
import type { DashboardWidget, TargetConfig } from "../../api/dashboards";
import { Icon } from "../../components/common/Icon";
import { Button } from "../../components/ui/Button";
import { valueTypeOf } from "../../lib/columns";
import type { ModelSnapshot, ModelTable } from "../../types/api";
import { defaultTarget, defaultValue, targetColumns, targetTable } from "./target";

const EMPTY: TargetConfig = { source: "", value: "", date: "", group: "", target: "" };

/**
 * Settings of a target widget: where the values come from (table, a formula,
 * the date, the link to the groups), the groups' target column, what one
 * target covers, and whether it is a limit or a goal.
 */
export function TargetEditor({
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

  const config: TargetConfig = widget.data?.target ?? defaultTarget(tables, snapshot) ?? EMPTY;
  const set = (patch: Partial<TargetConfig>) => onChange({ ...widget, data: { ...widget.data, target: { ...config, ...patch } } });
  const source = tables.find((t) => t.name === config.source);
  const columns = source?.columns ?? [];
  const groups = targetTable(config, snapshot);
  const targets = targetColumns(groups);
  const groupsName = groups?.displayName ?? groups?.name ?? "the linked table";

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
    <aside className="widget-editor" aria-label="Target settings">
      <header className="widget-editor__header">
        <h2>Targets</h2>
        <Button type="button" variant="ghost" size="sm" onClick={onClose} aria-label="Close target settings">
          <Icon name="x" size={14} />
        </Button>
      </header>
      <div className="widget-editor__body">
        <label className="widget-editor__field">
          <span className="widget-editor__label">Title</span>
          <input className="field__control field__control--compact" aria-label="Title" value={widget.title} onChange={(event) => onChange({ ...widget, title: event.target.value })} />
        </label>
        <p className="widget-editor__hint">
          Each group's value against its target — hours per project, orders per region, spending per category. A row opens the rows behind it.
        </p>
        <section className="widget-editor__section">
          {select("Values from", config.source, tables.map(named), (value) => {
            const table = tables.find((t) => t.name === value);
            const next = table ? defaultTarget([table], snapshot) : undefined;
            set(next ?? { ...EMPTY, source: value, value: defaultValue(table) });
          })}
          <label className="widget-editor__field">
            <span className="widget-editor__label">Value (formula)</span>
            <input
              className="field__control field__control--compact"
              aria-label="Value (formula)"
              value={config.value}
              placeholder="SUM(hours)"
              spellCheck={false}
              onChange={(event) => set({ value: event.target.value })}
            />
          </label>
          {select("Date", config.date, columns.filter((c) => ["date", "datetime"].includes(valueTypeOf(c))).map(named), (date) => set({ date }))}
          {select("Per", config.group, columns.filter((c) => c.type === "reference").map(named), (group) => set({ group, target: "" }))}
        </section>
        <section className="widget-editor__section">
          {select(`Target in ${groupsName}`, config.target, targets.map(named), (target) => set({ target }))}
          {groups && targets.length === 0 ? (
            <p className="widget-editor__hint">
              {groupsName} has no number column yet. Add one (e.g. <code>target</code>) in the <Link to="/model">Schema Designer</Link> and enter each one's target.
            </p>
          ) : null}
          <label className="widget-editor__field">
            <span className="widget-editor__label">One target covers</span>
            <select className="field__control field__control--compact" aria-label="One target covers" value={config.per ?? "month"} onChange={(event) => set({ per: event.target.value as TargetConfig["per"] })}>
              <option value="week">A week</option>
              <option value="month">A month</option>
              <option value="quarter">A quarter</option>
              <option value="year">A year</option>
            </select>
          </label>
          <label className="widget-editor__field">
            <span className="widget-editor__label">Compare</span>
            <select className="field__control field__control--compact" aria-label="Compare" value={config.period ?? "current"} onChange={(event) => set({ period: event.target.value as TargetConfig["period"] })}>
              <option value="current">The range's last period (e.g. this month)</option>
              <option value="range">The whole range (targets × its periods)</option>
            </select>
          </label>
          <label className="widget-editor__field">
            <span className="widget-editor__label">The target is</span>
            <select className="field__control field__control--compact" aria-label="The target is" value={config.kind ?? "limit"} onChange={(event) => set({ kind: event.target.value as TargetConfig["kind"] })}>
              <option value="goal">A goal to reach (green when reached)</option>
              <option value="limit">A limit to stay under (amber from 80 %, red over)</option>
            </select>
          </label>
          <label className="widget-editor__field">
            <span className="widget-editor__label">Write as</span>
            <select className="field__control field__control--compact" aria-label="Write as" value={config.format ?? "auto"} onChange={(event) => set({ format: event.target.value as TargetConfig["format"] })}>
              <option value="auto">As the formula gives it</option>
              <option value="number">Number</option>
              <option value="integer">Whole number</option>
              <option value="currency">Currency</option>
              <option value="percent">Percent</option>
            </select>
          </label>
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
