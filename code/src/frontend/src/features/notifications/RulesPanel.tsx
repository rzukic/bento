import { useCallback, useEffect, useMemo, useState } from "react";
import { workspaceCalendar } from "../../lib/calendar";
import { ApiClientError } from "../../api/client";
import { listDashboards, type Dashboard } from "../../api/dashboards";
import {
  checkRule,
  createRule,
  deleteRule,
  listChannels,
  listRules,
  testRule,
  updateRule,
  type NotificationChannel,
  type NotificationRule,
  type RuleCheck,
  type RuleConfig,
  type RuleKind,
  type RuleOp,
  type RulePeriod,
} from "../../api/notifications";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { Panel } from "../../components/ui/Panel";
import { Spinner } from "../../components/ui/Spinner";
import { useSchemaStore } from "../../stores/schemaStore";
import { getCurrentLocale } from "../data-explorer/components/editors/temporalEditing";
import "./Notifications.css";

const KIND_LABELS: Record<RuleKind, string> = { condition: "Condition", target: "Target", event: "Event", report: "Report" };
const PERIODS: { value: RulePeriod | ""; label: string }[] = [
  { value: "this-month", label: "This month" },
  { value: "this-week", label: "This week" },
  { value: "this-quarter", label: "This quarter" },
  { value: "today", label: "Today" },
  { value: "this-year", label: "This year" },
  { value: "", label: "All rows" },
];
const OPS: { value: RuleOp; label: string }[] = [
  { value: ">", label: "above" },
  { value: ">=", label: "at or above" },
  { value: "<", label: "below" },
  { value: "<=", label: "at or below" },
  { value: "=", label: "exactly" },
  { value: "<>", label: "not" },
];
const EVENTS = [
  { value: "import.failed", label: "A scheduled import fails" },
  { value: "price.failed", label: "A price update fails" },
] as const;

export function formatWhen(value: string | null | undefined): string {
  if (!value) return "";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : new Intl.DateTimeFormat(getCurrentLocale(), { dateStyle: "medium", timeStyle: "short" }).format(date);
}

export function errorText(cause: unknown, fallback: string): string {
  return cause instanceof ApiClientError ? cause.message : fallback;
}

/** Target widgets on the dashboards: what a target rule watches. */
function targetWidgets(dashboards: Dashboard[]) {
  return dashboards.flatMap((dashboard) =>
    (dashboard.dashboard.widgets ?? [])
      .filter((widget) => widget.type === "target")
      .map((widget) => ({ dashboardId: dashboard.id, widgetId: widget.id, label: `${dashboard.name} · ${widget.title || "Targets"}` })),
  );
}

/**
 * Step 5.3 (Settings, admins): when Bento speaks — a value crossing a line,
 * a group reaching its target or limit, an import or a price update failing. Each rule tells
 * you once when it starts to hold and stays quiet until it clears.
 */
export function RulesPanel() {
  const [rules, setRules] = useState<NotificationRule[] | null>(null);
  const [channels, setChannels] = useState<NotificationChannel[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState<NotificationRule | "new" | null>(null);
  const [busy, setBusy] = useState<number | null>(null);
  const [results, setResults] = useState<Record<number, string>>({});

  const load = useCallback(() => {
    Promise.all([listRules(), listChannels()])
      .then(([nextRules, nextChannels]) => {
        // Reports have their own panel.
        setRules(nextRules.filter((rule) => rule.kind !== "report"));
        setChannels(nextChannels);
      })
      .catch((cause) => {
        setRules([]);
        setError(errorText(cause, "The rules could not be loaded."));
      });
  }, []);
  useEffect(load, [load]);

  const run = async (rule: NotificationRule, action: "check" | "test") => {
    setBusy(rule.id);
    try {
      const result: RuleCheck = action === "check" ? await checkRule(rule.id) : await testRule(rule.id);
      const failed = result.deliveries.filter((d) => d.status === "failed");
      setResults((current) => ({
        ...current,
        [rule.id]:
          action === "test"
            ? failed.length > 0
              ? `Test not delivered: ${failed[0]!.error}`
              : `Test sent: “${result.notification?.text ?? ""}”`
            : result.state.error
              ? `Could not check: ${result.state.error}`
              : result.fired
                ? `It holds now and was told: “${result.notification?.text ?? ""}”`
                : `Checked: ${result.state.value ?? "—"}${result.state.active ? " — holds, told already" : " — does not hold"}.`,
      }));
      load();
    } catch (cause) {
      setResults((current) => ({ ...current, [rule.id]: errorText(cause, "That did not work.") }));
    } finally {
      setBusy(null);
    }
  };

  const toggle = async (rule: NotificationRule) => {
    try {
      await updateRule(rule.id, { enabled: !rule.enabled });
      load();
    } catch (cause) {
      setError(errorText(cause, "The rule could not be changed."));
    }
  };

  const remove = async (rule: NotificationRule) => {
    if (!window.confirm(`Delete the rule “${rule.name}”?`)) return;
    try {
      await deleteRule(rule.id);
      load();
    } catch (cause) {
      setError(errorText(cause, "The rule could not be deleted."));
    }
  };

  const channelName = (id: number) => channels.find((c) => c.id === id)?.name ?? `#${id}`;

  return (
    <Panel
      title="Notification rules"
      description="When Bento tells you something: a value crossing a line, a group reaching its target or limit, an import failing. Each rule speaks once when it starts to hold, then waits until it clears."
      actions={
        editing === null && channels.length > 0 ? (
          <Button type="button" variant="ghost" size="sm" leadingIcon={<Icon name="plus" size={13} />} onClick={() => setEditing("new")}>
            Add rule
          </Button>
        ) : null
      }
    >
      {error ? <Alert tone="error" message={error} /> : null}
      {editing !== null ? (
        <RuleForm
          rule={editing === "new" ? null : editing}
          channels={channels}
          onCancel={() => setEditing(null)}
          onSaved={() => {
            setEditing(null);
            load();
          }}
        />
      ) : null}
      {rules === null ? (
        <Spinner label="Loading rules" />
      ) : channels.length === 0 ? (
        <p className="channels__empty">Add a channel above first: rules tell you over it.</p>
      ) : rules.length === 0 && editing === null ? (
        <p className="channels__empty">No rules yet. For example: a value this month above a number, a project reaching its weekly hours, a scheduled import failing.</p>
      ) : (
        <ul className="channels">
          {rules.map((rule) => (
            <li key={rule.id} className={rule.enabled ? "channel" : "channel channel--off"}>
              <div className="channel__main">
                <div className="channel__title">
                  <strong>{rule.name}</strong>
                  <span className="channel__kind">{KIND_LABELS[rule.kind]}</span>
                  {rule.state.active ? <span className="channel__kind channel__kind--on">Holds</span> : null}
                  {!rule.enabled ? <span className="channel__off">Off</span> : null}
                </div>
                <span className="channel__target">
                  {rule.summary} → {rule.channels.map(channelName).join(", ")}
                </span>
                {rule.state.error ? (
                  <p className="channel__last channel__last--failed">{rule.state.error}</p>
                ) : rule.state.checkedAt ? (
                  <p className="channel__last">
                    {formatWhen(rule.state.checkedAt)}
                    {rule.state.value ? `: ${rule.state.value}` : ""}
                    {rule.state.firedAt ? ` · last told ${formatWhen(rule.state.firedAt)}` : ""}
                  </p>
                ) : (
                  <p className="channel__last">Not checked yet.</p>
                )}
                {results[rule.id] ? <p className="channel__result" role="status">{results[rule.id]}</p> : null}
              </div>
              <div className="channel__actions">
                {rule.kind !== "event" ? (
                  <Button type="button" variant="ghost" size="sm" disabled={busy !== null} onClick={() => void run(rule, "check")}>
                    {busy === rule.id ? "Checking…" : "Check now"}
                  </Button>
                ) : null}
                <Button type="button" variant="ghost" size="sm" disabled={busy !== null} onClick={() => void run(rule, "test")}>
                  Send test
                </Button>
                <Button type="button" variant="ghost" size="sm" onClick={() => setEditing(rule)}>
                  Edit
                </Button>
                <Button type="button" variant="ghost" size="sm" onClick={() => void toggle(rule)}>
                  {rule.enabled ? "Turn off" : "Turn on"}
                </Button>
                <Button type="button" variant="ghost" size="sm" aria-label={`Delete ${rule.name}`} onClick={() => void remove(rule)}>
                  <Icon name="trash" size={14} />
                </Button>
              </div>
            </li>
          ))}
        </ul>
      )}
    </Panel>
  );
}

function RuleForm({
  rule,
  channels,
  onCancel,
  onSaved,
}: {
  rule: NotificationRule | null;
  channels: NotificationChannel[];
  onCancel: () => void;
  onSaved: () => void;
}) {
  const snapshot = useSchemaStore((state) => state.snapshot);
  const loadSchema = useSchemaStore((state) => state.load);
  useEffect(() => {
    void loadSchema();
  }, [loadSchema]);
  const tables = useMemo(() => (snapshot?.tables ?? []).filter((t) => !t.name.startsWith("_")), [snapshot]);

  const [kind, setKind] = useState<RuleKind>(rule?.kind ?? "condition");
  const [name, setName] = useState(rule?.name ?? "");
  const [source, setSource] = useState(rule?.config.source ?? "");
  const [formula, setFormula] = useState(rule?.config.formula ?? "");
  const [period, setPeriod] = useState<RulePeriod | "">(rule ? (rule.config.period ?? "") : "this-month");
  const [dateColumn, setDateColumn] = useState(rule?.config.dateColumn ?? "");
  const [op, setOp] = useState<RuleOp>(rule?.config.op ?? ">");
  const [threshold, setThreshold] = useState(rule?.config.threshold != null ? String(rule.config.threshold) : "");
  const [every, setEvery] = useState<"hour" | "day">(rule?.config.every === "day" ? "day" : "hour");
  const [at, setAt] = useState(rule?.config.at ?? "08:00");
  const [target, setTarget] = useState(rule?.config.widgetId ? `${rule.config.dashboardId}:${rule.config.widgetId}` : "");
  const [percent, setPercent] = useState(rule?.config.percent != null ? String(rule.config.percent) : "100");
  const [event, setEvent] = useState<"import.failed" | "price.failed">(rule?.config.event ?? "import.failed");
  const [chosen, setChosen] = useState<number[]>(rule?.channels ?? channels.slice(0, 1).map((c) => c.id));
  const [dashboards, setDashboards] = useState<Dashboard[]>([]);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (kind !== "target") return;
    listDashboards()
      .then(setDashboards)
      .catch(() => setDashboards([]));
  }, [kind]);
  const targets = useMemo(() => targetWidgets(dashboards), [dashboards]);
  useEffect(() => {
    if (kind === "target" && !target && targets[0]) setTarget(`${targets[0].dashboardId}:${targets[0].widgetId}`);
  }, [kind, target, targets]);

  const table = tables.find((t) => t.name === source) ?? null;
  const dateColumns = (table?.columns ?? []).filter((c) => c.type === "date" || c.type === "datetime");
  useEffect(() => {
    if (period && !dateColumns.some((c) => c.name === dateColumn) && dateColumns[0]) setDateColumn(dateColumns[0].name);
  }, [period, dateColumn, dateColumns]);

  const save = async () => {
    let config: RuleConfig;
    if (kind === "condition") {
      config = {
        source,
        formula,
        period: period || null,
        dateColumn: period ? dateColumn : null,
        op,
        threshold: threshold.trim() === "" ? null : Number(threshold.replace(",", ".")),
        every,
        at: every === "day" ? at : null,
      };
    } else if (kind === "target") {
      const [dashboardId, widgetId] = target.split(":");
      config = { dashboardId: dashboardId ? Number(dashboardId) : null, widgetId: widgetId ?? null, percent: Number(percent) };
    } else {
      config = { event };
    }
    setSaving(true);
    setError(null);
    try {
      if (rule) await updateRule(rule.id, { name: name.trim(), config, channels: chosen });
      else await createRule(name.trim(), kind, config, chosen);
      onSaved();
    } catch (cause) {
      setError(errorText(cause, "The rule could not be saved."));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="channel-form">
      <div className="channel-form__grid">
        <label className="field field--compact">
          <span className="field__label">Kind</span>
          <select className="field__control field__control--compact" value={kind} disabled={!!rule} onChange={(event) => setKind(event.target.value as RuleKind)}>
            <option value="condition">A value crosses a line</option>
            <option value="target">A group reaches its target or limit</option>
            <option value="event">Something fails</option>
          </select>
        </label>
        <label className="field field--compact">
          <span className="field__label">Name</span>
          <input
            className="field__control field__control--compact"
            value={name}
            placeholder={kind === "condition" ? "Orders this month" : kind === "target" ? "Target reached" : "Import failed"}
            onChange={(e) => setName(e.target.value)}
          />
        </label>
      </div>

      {kind === "condition" ? (
        <>
          <div className="channel-form__grid">
            <label className="field field--compact">
              <span className="field__label">Table</span>
              <select className="field__control field__control--compact" value={source} onChange={(e) => setSource(e.target.value)}>
                <option value="">Choose…</option>
                {tables.map((t) => (
                  <option key={t.name} value={t.name}>
                    {t.displayName ?? t.name}
                  </option>
                ))}
              </select>
            </label>
            <label className="field field--compact channel-form__wide">
              <span className="field__label">Value (formula)</span>
              <input
                className="field__control field__control--compact"
                value={formula}
                placeholder='COUNT(WHERE status = "open")'
                spellCheck={false}
                onChange={(e) => setFormula(e.target.value)}
              />
            </label>
          </div>
          <div className="channel-form__grid">
            <label className="field field--compact">
              <span className="field__label">Rows of</span>
              <select className="field__control field__control--compact" value={period} onChange={(e) => setPeriod(e.target.value as RulePeriod | "")}>
                {PERIODS.map((p) => (
                  <option key={p.value} value={p.value}>
                    {p.label}
                  </option>
                ))}
              </select>
            </label>
            {period ? (
              <label className="field field--compact">
                <span className="field__label">By date in</span>
                <select className="field__control field__control--compact" value={dateColumn} onChange={(e) => setDateColumn(e.target.value)}>
                  {dateColumns.length === 0 ? <option value="">No date column</option> : null}
                  {dateColumns.map((c) => (
                    <option key={c.name} value={c.name}>
                      {c.displayName ?? c.name}
                    </option>
                  ))}
                </select>
              </label>
            ) : null}
            <label className="field field--compact">
              <span className="field__label">Tell me when it is</span>
              <select className="field__control field__control--compact" value={op} onChange={(e) => setOp(e.target.value as RuleOp)}>
                {OPS.map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </select>
            </label>
            <label className="field field--compact">
              <span className="field__label">Number</span>
              <input className="field__control field__control--compact" inputMode="decimal" value={threshold} placeholder="500" onChange={(e) => setThreshold(e.target.value)} />
            </label>
          </div>
          <div className="channel-form__grid">
            <label className="field field--compact">
              <span className="field__label">Check</span>
              <select className="field__control field__control--compact" value={every} onChange={(e) => setEvery(e.target.value as "hour" | "day")}>
                <option value="hour">Every hour and after imports</option>
                <option value="day">Once a day</option>
              </select>
            </label>
            {every === "day" ? (
              <label className="field field--compact">
                <span className="field__label">At</span>
                <input className="field__control field__control--compact" type="time" value={at} onChange={(e) => setAt(e.target.value)} />
              </label>
            ) : null}
          </div>
          <p className="channel-form__hint">
            The value is one number from the table, written like a widget's formula — e.g. <code>SUM(hours)</code> for the hours logged, <code>-SUM(amount WHERE amount &lt; 0)</code> for what was spent, or{" "}
            <code>COUNT()</code> for how many rows there are. Periods and times follow the workspace's calendar ({workspaceCalendar().timeZone}, Settings → Workspace).
          </p>
        </>
      ) : null}

      {kind === "target" ? (
        <>
          <div className="channel-form__grid">
            <label className="field field--compact channel-form__wide">
              <span className="field__label">Targets</span>
              <select className="field__control field__control--compact" value={target} onChange={(e) => setTarget(e.target.value)}>
                {targets.length === 0 ? <option value="">No dashboard has a target widget</option> : null}
                {targets.map((t) => (
                  <option key={`${t.dashboardId}:${t.widgetId}`} value={`${t.dashboardId}:${t.widgetId}`}>
                    {t.label}
                  </option>
                ))}
              </select>
            </label>
            <label className="field field--compact">
              <span className="field__label">At % of the target</span>
              <input className="field__control field__control--compact" inputMode="numeric" value={percent} onChange={(e) => setPercent(e.target.value)} />
            </label>
          </div>
          <p className="channel-form__hint">
            Each group is told once per period of its target (its week, month …), when its value reaches the share — 100 for a goal reached or a limit
            used up, 80 to hear about a limit early.
          </p>
        </>
      ) : null}

      {kind === "event" ? (
        <div className="channel-form__grid">
          <label className="field field--compact channel-form__wide">
            <span className="field__label">When</span>
            <select className="field__control field__control--compact" value={event} onChange={(e) => setEvent(e.target.value as typeof event)}>
              {EVENTS.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </select>
          </label>
        </div>
      ) : null}

      <fieldset className="rule-form__channels">
        <legend className="field__label">Tell me on</legend>
        {channels.map((channel) => (
          <label key={channel.id} className="rule-form__channel">
            <input
              type="checkbox"
              checked={chosen.includes(channel.id)}
              onChange={(e) => setChosen((current) => (e.target.checked ? [...current, channel.id] : current.filter((id) => id !== channel.id)))}
            />
            {channel.name}
          </label>
        ))}
      </fieldset>

      {error ? <Alert tone="error" message={error} /> : null}
      <div className="channel-form__actions">
        <Button type="button" variant="primary" disabled={saving || !name.trim()} onClick={() => void save()}>
          {saving ? "Saving…" : rule ? "Save rule" : "Add rule"}
        </Button>
        <Button type="button" variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
      </div>
    </div>
  );
}
