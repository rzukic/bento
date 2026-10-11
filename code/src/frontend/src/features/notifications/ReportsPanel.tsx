import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { listDashboards, type Dashboard, type DashboardWidget } from "../../api/dashboards";
import {
  checkRule,
  createRule,
  deleteRule,
  listChannels,
  listRules,
  previewReport,
  testRule,
  updateRule,
  type NotificationChannel,
  type NotificationMessage,
  type NotificationRule,
  type ReportBlock,
  type ReportBlockType,
  type ReportFormat,
  type ReportUnit,
  type RuleConfig,
} from "../../api/notifications";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { Panel } from "../../components/ui/Panel";
import { Spinner } from "../../components/ui/Spinner";
import { useSchemaStore } from "../../stores/schemaStore";
import { errorText, formatWhen } from "./RulesPanel";
import "./Notifications.css";

const UNITS: { value: ReportUnit; label: string; noun: string }[] = [
  { value: "day", label: "Every day", noun: "day" },
  { value: "week", label: "Every week", noun: "week" },
  { value: "month", label: "Every month", noun: "month" },
  { value: "quarter", label: "Every quarter", noun: "quarter" },
  { value: "year", label: "Every year", noun: "year" },
];
const WEEKDAYS = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
const FORMATS: { value: ReportFormat; label: string }[] = [
  { value: "auto", label: "As the formula gives it" },
  { value: "number", label: "Number" },
  { value: "integer", label: "Whole number" },
  { value: "currency", label: "Currency" },
  { value: "percent", label: "Percent" },
];
const BLOCK_LABELS: Record<ReportBlockType, string> = {
  value: "Value",
  top: "Top list",
  dashboard: "Dashboard figures",
  rules: "Rules that hold",
  text: "Text",
};

/** Key figures a report can take from a dashboard: KPI widgets that follow the dashboard's range. */
export function reportKpis(dashboard: Dashboard): DashboardWidget[] {
  return (dashboard.dashboard.widgets ?? []).filter(
    (widget) => widget.type === "kpi" && !widget.data?.range && (widget.data?.query || widget.data?.savedQueryId !== undefined || widget.data?.variable),
  );
}

/**
 * Step 5.3 (Settings, admins): reports on a schedule — every day, week, month,
 * quarter or year, the numbers the user puts together from blocks (values as
 * formulas over any table, top lists, a dashboard's key figures, the rules that
 * hold, text), against the period before. Nothing in it is tied to one use.
 */
export function ReportsPanel() {
  const [reports, setReports] = useState<NotificationRule[] | null>(null);
  const [channels, setChannels] = useState<NotificationChannel[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState<NotificationRule | "new" | null>(null);
  const [busy, setBusy] = useState<number | null>(null);
  const [results, setResults] = useState<Record<number, string>>({});
  const [previews, setPreviews] = useState<Record<number, NotificationMessage>>({});

  const load = useCallback(() => {
    Promise.all([listRules(), listChannels()])
      .then(([rules, nextChannels]) => {
        setReports(rules.filter((rule) => rule.kind === "report"));
        setChannels(nextChannels);
      })
      .catch((cause) => {
        setReports([]);
        setError(errorText(cause, "The reports could not be loaded."));
      });
  }, []);
  useEffect(load, [load]);

  const preview = async (report: NotificationRule) => {
    if (previews[report.id]) {
      setPreviews(({ [report.id]: _, ...rest }) => rest);
      return;
    }
    setBusy(report.id);
    try {
      const result = await checkRule(report.id);
      if (result.notification) setPreviews((current) => ({ ...current, [report.id]: result.notification! }));
    } catch (cause) {
      setResults((current) => ({ ...current, [report.id]: errorText(cause, "The report could not be put together.") }));
    } finally {
      setBusy(null);
    }
  };

  const send = async (report: NotificationRule) => {
    setBusy(report.id);
    try {
      const result = await testRule(report.id);
      const failed = result.deliveries.filter((d) => d.status === "failed");
      setResults((current) => ({
        ...current,
        [report.id]: failed.length > 0 ? `Not delivered: ${failed[0]!.error}` : `Sent: “${result.notification?.title ?? ""}”`,
      }));
    } catch (cause) {
      setResults((current) => ({ ...current, [report.id]: errorText(cause, "That did not work.") }));
    } finally {
      setBusy(null);
    }
  };

  const toggle = async (report: NotificationRule) => {
    try {
      await updateRule(report.id, { enabled: !report.enabled });
      load();
    } catch (cause) {
      setError(errorText(cause, "The report could not be changed."));
    }
  };

  const remove = async (report: NotificationRule) => {
    if (!window.confirm(`Delete the report “${report.name}”?`)) return;
    try {
      await deleteRule(report.id);
      load();
    } catch (cause) {
      setError(errorText(cause, "The report could not be deleted."));
    }
  };

  const channelName = (id: number) => channels.find((c) => c.id === id)?.name ?? `#${id}`;

  return (
    <Panel
      title="Reports"
      description="A message on a schedule with the numbers you choose: values from any table, the biggest values of a group, a dashboard's key figures — each against the period before."
      actions={
        editing === null && channels.length > 0 ? (
          <Button type="button" variant="ghost" size="sm" leadingIcon={<Icon name="plus" size={13} />} onClick={() => setEditing("new")}>
            Add report
          </Button>
        ) : null
      }
    >
      {error ? <Alert tone="error" message={error} /> : null}
      {editing !== null ? (
        <ReportForm
          report={editing === "new" ? null : editing}
          channels={channels}
          onCancel={() => setEditing(null)}
          onSaved={() => {
            setEditing(null);
            setPreviews({});
            load();
          }}
        />
      ) : null}
      {reports === null ? (
        <Spinner label="Loading reports" />
      ) : channels.length === 0 ? (
        <p className="channels__empty">Add a channel above first: reports go out over it.</p>
      ) : reports.length === 0 && editing === null ? (
        <p className="channels__empty">No reports yet. For example: last month against the month before every 1st, or yesterday's numbers every morning.</p>
      ) : (
        <ul className="channels">
          {reports.map((report) => (
            <li key={report.id} className={report.enabled ? "channel" : "channel channel--off"}>
              <div className="channel__main">
                <div className="channel__title">
                  <strong>{report.name}</strong>
                  <span className="channel__kind">Report</span>
                  {!report.enabled ? <span className="channel__off">Off</span> : null}
                </div>
                <span className="channel__target">
                  {report.summary} → {report.channels.map(channelName).join(", ")}
                </span>
                {report.state.error ? (
                  <p className="channel__last channel__last--failed">{report.state.error}</p>
                ) : report.state.checkedAt ? (
                  <p className="channel__last">
                    Last sent {formatWhen(report.state.checkedAt)}
                    {report.state.value ? `: ${report.state.value}` : ""}
                  </p>
                ) : (
                  <p className="channel__last">Not sent yet.</p>
                )}
                {results[report.id] ? <p className="channel__result" role="status">{results[report.id]}</p> : null}
                {previews[report.id] ? <ReportPreview message={previews[report.id]!} /> : null}
              </div>
              <div className="channel__actions">
                <Button type="button" variant="ghost" size="sm" disabled={busy !== null} aria-expanded={!!previews[report.id]} onClick={() => void preview(report)}>
                  {busy === report.id && !previews[report.id] ? "Working…" : previews[report.id] ? "Hide preview" : "Preview"}
                </Button>
                <Button type="button" variant="ghost" size="sm" disabled={busy !== null} onClick={() => void send(report)}>
                  Send now
                </Button>
                <Button type="button" variant="ghost" size="sm" onClick={() => setEditing(report)}>
                  Edit
                </Button>
                <Button type="button" variant="ghost" size="sm" onClick={() => void toggle(report)}>
                  {report.enabled ? "Turn off" : "Turn on"}
                </Button>
                <Button type="button" variant="ghost" size="sm" aria-label={`Delete ${report.name}`} onClick={() => void remove(report)}>
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

/** A report as it reads in an email: title, text, then each section with its values and changes. */
export function ReportPreview({ message }: { message: NotificationMessage }) {
  return (
    <article className="report-preview" aria-label="Report preview">
      <h4 className="report-preview__title">{message.title}</h4>
      <p className="report-preview__text">{message.text}</p>
      {(message.sections ?? []).map((section, index) => (
        <section key={index} className="report-preview__section">
          {section.title ? <h5 className="report-preview__heading">{section.title}</h5> : null}
          {section.text ? <p className="report-preview__text">{section.text}</p> : null}
          {section.facts && section.facts.length > 0 ? (
            <table className="report-preview__facts">
              <tbody>
                {section.facts.map((fact, row) => (
                  <tr key={row}>
                    <th scope="row">{fact.label}</th>
                    <td className="report-preview__value">{fact.value}</td>
                    <td className={fact.tone ? `report-preview__detail report-preview__detail--${fact.tone}` : "report-preview__detail"}>{fact.detail ?? ""}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : null}
        </section>
      ))}
    </article>
  );
}

type EditableBlock = ReportBlock & { key: number };

function blankBlock(type: ReportBlockType, key: number): EditableBlock {
  switch (type) {
    case "value":
      return { key, type, source: "", formula: "", dateColumn: "", format: "auto", higherIsBetter: true };
    case "top":
      return { key, type, source: "", formula: "", groupBy: "", dateColumn: "", limit: 5, order: "desc", format: "auto", higherIsBetter: true };
    case "dashboard":
      return { key, type, dashboardId: null, widgets: null };
    case "text":
      return { key, type, text: "" };
    default:
      return { key, type };
  }
}

/** What goes to the server: the fields of the block's type, empty ones left out. */
function toBlock({ key: _key, ...block }: EditableBlock): ReportBlock {
  const label = block.label?.trim() || null;
  switch (block.type) {
    case "value":
    case "top":
      return {
        type: block.type,
        label,
        source: block.source || null,
        formula: block.formula?.trim() || null,
        dateColumn: block.dateColumn || null,
        format: block.format ?? "auto",
        higherIsBetter: block.higherIsBetter ?? true,
        ...(block.type === "top" ? { groupBy: block.groupBy || null, limit: block.limit ?? 5, order: block.order ?? "desc" } : {}),
      };
    case "dashboard":
      return { type: block.type, label, dashboardId: block.dashboardId ?? null, widgets: block.widgets && block.widgets.length > 0 ? block.widgets : null };
    case "text":
      return { type: block.type, label, text: block.text ?? "" };
    default:
      return { type: block.type, label };
  }
}

function ReportForm({
  report,
  channels,
  onCancel,
  onSaved,
}: {
  report: NotificationRule | null;
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
  const [dashboards, setDashboards] = useState<Dashboard[]>([]);
  useEffect(() => {
    listDashboards()
      .then(setDashboards)
      .catch(() => setDashboards([]));
  }, []);

  const config = report?.config;
  const nextKey = useRef(0);
  const [name, setName] = useState(report?.name ?? "");
  const [every, setEvery] = useState<ReportUnit>((config?.every as ReportUnit | undefined) ?? "month");
  const [day, setDay] = useState(config?.day ?? 1);
  const [at, setAt] = useState(config?.at ?? "08:00");
  const [range, setRange] = useState(config?.range ?? "last");
  const [compare, setCompare] = useState(config?.compare ?? "previous");
  const [intro, setIntro] = useState(config?.intro ?? "");
  const [ai, setAi] = useState(config?.ai ?? false);
  const [instructions, setInstructions] = useState(config?.aiInstructions ?? "");
  const [blocks, setBlocks] = useState<EditableBlock[]>(() => (config?.blocks ?? []).map((block) => ({ ...block, key: nextKey.current++ })));
  const [chosen, setChosen] = useState<number[]>(report?.channels ?? channels.slice(0, 1).map((c) => c.id));
  const [saving, setSaving] = useState(false);
  const [previewing, setPreviewing] = useState(false);
  const [preview, setPreview] = useState<NotificationMessage | null>(null);
  const [error, setError] = useState<string | null>(null);

  const noun = UNITS.find((u) => u.value === every)!.noun;

  const currentConfig = (): RuleConfig => ({
    every,
    day: every === "day" ? null : day,
    at,
    range,
    compare,
    intro: intro.trim() || null,
    ai,
    aiInstructions: ai ? instructions.trim() || null : null,
    blocks: blocks.map(toBlock),
  });

  const add = (type: ReportBlockType) => setBlocks((current) => [...current, blankBlock(type, nextKey.current++)]);
  const change = (key: number, next: Partial<ReportBlock>) => setBlocks((current) => current.map((block) => (block.key === key ? { ...block, ...next } : block)));
  const move = (index: number, by: -1 | 1) =>
    setBlocks((current) => {
      const next = [...current];
      const [block] = next.splice(index, 1);
      next.splice(index + by, 0, block!);
      return next;
    });
  const drop = (key: number) => setBlocks((current) => current.filter((block) => block.key !== key));

  const showPreview = async () => {
    setPreviewing(true);
    setError(null);
    try {
      setPreview(await previewReport(name.trim() || "Report", currentConfig()));
    } catch (cause) {
      setPreview(null);
      setError(errorText(cause, "The report could not be put together."));
    } finally {
      setPreviewing(false);
    }
  };

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      if (report) await updateRule(report.id, { name: name.trim(), config: currentConfig(), channels: chosen });
      else await createRule(name.trim(), "report", currentConfig(), chosen);
      onSaved();
    } catch (cause) {
      setError(errorText(cause, "The report could not be saved."));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="channel-form">
      <div className="channel-form__grid">
        <label className="field field--compact channel-form__wide">
          <span className="field__label">Name</span>
          <input className="field__control field__control--compact" value={name} placeholder="Monthly review" onChange={(e) => setName(e.target.value)} />
        </label>
      </div>

      <div className="channel-form__grid">
        <label className="field field--compact">
          <span className="field__label">Send</span>
          <select
            className="field__control field__control--compact"
            value={every}
            onChange={(e) => {
              setEvery(e.target.value as ReportUnit);
              setDay(1);
            }}
          >
            {UNITS.map((unit) => (
              <option key={unit.value} value={unit.value}>
                {unit.label}
              </option>
            ))}
          </select>
        </label>
        {every === "week" ? (
          <label className="field field--compact">
            <span className="field__label">On</span>
            <select className="field__control field__control--compact" value={day} onChange={(e) => setDay(Number(e.target.value))}>
              {WEEKDAYS.map((weekday, index) => (
                <option key={weekday} value={index + 1}>
                  {weekday}
                </option>
              ))}
            </select>
          </label>
        ) : every !== "day" ? (
          <label className="field field--compact">
            <span className="field__label">{every === "month" ? "On day" : every === "quarter" ? "On day of the quarter's first month" : "On day of January"}</span>
            <select className="field__control field__control--compact" value={day} onChange={(e) => setDay(Number(e.target.value))}>
              {Array.from({ length: 28 }, (_, index) => index + 1).map((value) => (
                <option key={value} value={value}>
                  {value}
                </option>
              ))}
            </select>
          </label>
        ) : null}
        <label className="field field--compact">
          <span className="field__label">At</span>
          <input className="field__control field__control--compact" type="time" value={at} onChange={(e) => setAt(e.target.value)} />
        </label>
      </div>

      <div className="channel-form__grid">
        <label className="field field--compact">
          <span className="field__label">About</span>
          <select className="field__control field__control--compact" value={range} onChange={(e) => setRange(e.target.value as "last" | "current")}>
            <option value="last">{noun === "day" ? "The day before" : `The ${noun} before`}</option>
            <option value="current">{`This ${noun} so far`}</option>
          </select>
        </label>
        <label className="field field--compact">
          <span className="field__label">Compared with</span>
          <select className="field__control field__control--compact" value={compare} onChange={(e) => setCompare(e.target.value as "previous" | "year" | "none")}>
            <option value="previous">{`The ${noun} before that`}</option>
            <option value="year">A year earlier</option>
            <option value="none">Nothing</option>
          </select>
        </label>
      </div>

      <label className="field field--compact">
        <span className="field__label">Text at the top (optional)</span>
        <textarea className="field__control field__control--compact report-form__text" rows={2} value={intro} onChange={(e) => setIntro(e.target.value)} />
      </label>

      <div className="report-form__blocks">
        <span className="field__label">Blocks</span>
        {blocks.length === 0 ? <p className="channel-form__hint">Add what the report shows, in the order it shows it.</p> : null}
        <ol className="report-blocks">
          {blocks.map((block, index) => (
            <li key={block.key} className="report-block">
              <div className="report-block__head">
                <span className="channel__kind">{BLOCK_LABELS[block.type]}</span>
                <input
                  className="field__control field__control--compact report-block__label"
                  aria-label={`Block ${index + 1} label`}
                  value={block.label ?? ""}
                  placeholder={block.type === "dashboard" ? "The dashboard's name" : block.type === "rules" ? "Rules that hold" : "Label"}
                  onChange={(e) => change(block.key, { label: e.target.value })}
                />
                <Button type="button" variant="ghost" size="sm" aria-label={`Move block ${index + 1} up`} disabled={index === 0} onClick={() => move(index, -1)}>
                  ↑
                </Button>
                <Button type="button" variant="ghost" size="sm" aria-label={`Move block ${index + 1} down`} disabled={index === blocks.length - 1} onClick={() => move(index, 1)}>
                  ↓
                </Button>
                <Button type="button" variant="ghost" size="sm" aria-label={`Remove block ${index + 1}`} onClick={() => drop(block.key)}>
                  <Icon name="x" size={13} />
                </Button>
              </div>
              <BlockFields block={block} index={index} tables={tables} dashboards={dashboards} onChange={(next) => change(block.key, next)} />
            </li>
          ))}
        </ol>
        <div className="report-form__add" role="group" aria-label="Add a block">
          {(Object.keys(BLOCK_LABELS) as ReportBlockType[]).map((type) => (
            <Button key={type} type="button" variant="ghost" size="sm" leadingIcon={<Icon name="plus" size={12} />} onClick={() => add(type)}>
              {BLOCK_LABELS[type]}
            </Button>
          ))}
        </div>
      </div>

      <label className="rule-form__channel">
        <input type="checkbox" checked={ai} onChange={(e) => setAi(e.target.checked)} />
        Let the AI write a few sentences about the numbers
      </label>
      {ai ? (
        <label className="field field--compact">
          <span className="field__label">For the AI (optional)</span>
          <input
            className="field__control field__control--compact"
            value={instructions}
            placeholder="In German; the biggest changes first"
            onChange={(e) => setInstructions(e.target.value)}
          />
        </label>
      ) : null}
      {ai ? <p className="channel-form__hint">It sees only the report's numbers and uses the key and monthly budget on the AI page; without them the report goes out without these sentences.</p> : null}

      <fieldset className="rule-form__channels">
        <legend className="field__label">Send to</legend>
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
      {preview ? <ReportPreview message={preview} /> : null}
      <div className="channel-form__actions">
        <Button type="button" variant="primary" disabled={saving || !name.trim() || blocks.length === 0} onClick={() => void save()}>
          {saving ? "Saving…" : report ? "Save report" : "Add report"}
        </Button>
        <Button type="button" variant="ghost" disabled={previewing || blocks.length === 0} onClick={() => void showPreview()}>
          {previewing ? "Putting it together…" : "Preview"}
        </Button>
        <Button type="button" variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
      </div>
      <p className="channel-form__hint">Periods and times are in Europe/Berlin. The preview shows the report as it would go out today, without the AI's sentences.</p>
    </div>
  );
}

function BlockFields({
  block,
  index,
  tables,
  dashboards,
  onChange,
}: {
  block: EditableBlock;
  index: number;
  tables: { name: string; displayName?: string | null; columns: { name: string; displayName?: string | null; type: string }[] }[];
  dashboards: Dashboard[];
  onChange: (next: Partial<ReportBlock>) => void;
}) {
  const label = (text: string) => `${text} (block ${index + 1})`;

  if (block.type === "rules") return <p className="channel-form__hint">The notification rules that hold when the report goes out, with their values.</p>;

  if (block.type === "text")
    return (
      <textarea
        className="field__control field__control--compact report-form__text"
        aria-label={label("Text")}
        rows={2}
        value={block.text ?? ""}
        onChange={(e) => onChange({ text: e.target.value })}
      />
    );

  if (block.type === "dashboard") {
    const withKpis = dashboards.filter((dashboard) => reportKpis(dashboard).length > 0);
    const dashboard = dashboards.find((d) => d.id === block.dashboardId) ?? null;
    const kpis = dashboard ? reportKpis(dashboard) : [];
    const picked = block.widgets ?? null;
    return (
      <>
        <label className="field field--compact">
          <span className="field__label">Dashboard</span>
          <select
            className="field__control field__control--compact"
            aria-label={label("Dashboard")}
            value={block.dashboardId ?? ""}
            onChange={(e) => onChange({ dashboardId: e.target.value ? Number(e.target.value) : null, widgets: null })}
          >
            <option value="">{withKpis.length === 0 ? "No dashboard has key figures" : "Choose…"}</option>
            {withKpis.map((d) => (
              <option key={d.id} value={d.id}>
                {d.name}
              </option>
            ))}
          </select>
        </label>
        {kpis.length > 0 ? (
          <fieldset className="rule-form__channels">
            <legend className="field__label">Key figures</legend>
            {kpis.map((widget) => (
              <label key={widget.id} className="rule-form__channel">
                <input
                  type="checkbox"
                  checked={picked === null || picked.includes(widget.id)}
                  onChange={(e) => {
                    const all = kpis.map((w) => w.id);
                    const current = picked ?? all;
                    const next = e.target.checked ? all.filter((w) => w === widget.id || current.includes(w)) : current.filter((w) => w !== widget.id);
                    onChange({ widgets: next.length === all.length ? null : next });
                  }}
                />
                {widget.title || widget.id}
              </label>
            ))}
          </fieldset>
        ) : null}
        <p className="channel-form__hint">Each key figure for the report's period, as the dashboard computes it for that range, against the period compared with.</p>
      </>
    );
  }

  // value and top
  const table = tables.find((t) => t.name === block.source) ?? null;
  const dateColumns = (table?.columns ?? []).filter((c) => c.type === "date" || c.type === "datetime");
  return (
    <>
      <div className="channel-form__grid">
        <label className="field field--compact">
          <span className="field__label">Table</span>
          <select
            className="field__control field__control--compact"
            aria-label={label("Table")}
            value={block.source ?? ""}
            onChange={(e) => {
              const next = tables.find((t) => t.name === e.target.value);
              const firstDate = (next?.columns ?? []).find((c) => c.type === "date" || c.type === "datetime");
              onChange({ source: e.target.value, dateColumn: firstDate?.name ?? "", groupBy: "" });
            }}
          >
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
            aria-label={label("Value (formula)")}
            value={block.formula ?? ""}
            placeholder="COUNT()"
            spellCheck={false}
            onChange={(e) => onChange({ formula: e.target.value })}
          />
        </label>
      </div>
      <div className="channel-form__grid">
        {block.type === "top" ? (
          <>
            <label className="field field--compact">
              <span className="field__label">Per</span>
              <select className="field__control field__control--compact" aria-label={label("Per")} value={block.groupBy ?? ""} onChange={(e) => onChange({ groupBy: e.target.value })}>
                <option value="">Choose…</option>
                {(table?.columns ?? []).map((c) => (
                  <option key={c.name} value={c.name}>
                    {c.displayName ?? c.name}
                  </option>
                ))}
              </select>
            </label>
            <label className="field field--compact">
              <span className="field__label">Show</span>
              <select
                className="field__control field__control--compact"
                aria-label={label("Show")}
                value={`${block.order ?? "desc"}:${block.limit ?? 5}`}
                onChange={(e) => {
                  const [order, limit] = e.target.value.split(":");
                  onChange({ order: order as "desc" | "asc", limit: Number(limit) });
                }}
              >
                {(["desc", "asc"] as const).flatMap((order) =>
                  [3, 5, 10, 20].map((limit) => (
                    <option key={`${order}:${limit}`} value={`${order}:${limit}`}>
                      {`${order === "desc" ? "The biggest" : "The smallest"} ${limit}`}
                    </option>
                  )),
                )}
              </select>
            </label>
          </>
        ) : null}
        <label className="field field--compact">
          <span className="field__label">Period by</span>
          <select className="field__control field__control--compact" aria-label={label("Period by")} value={block.dateColumn ?? ""} onChange={(e) => onChange({ dateColumn: e.target.value })}>
            {dateColumns.map((c) => (
              <option key={c.name} value={c.name}>
                {c.displayName ?? c.name}
              </option>
            ))}
            <option value="">All rows (no period)</option>
          </select>
        </label>
        <label className="field field--compact">
          <span className="field__label">Write as</span>
          <select className="field__control field__control--compact" aria-label={label("Write as")} value={block.format ?? "auto"} onChange={(e) => onChange({ format: e.target.value as ReportFormat })}>
            {FORMATS.map((format) => (
              <option key={format.value} value={format.value}>
                {format.label}
              </option>
            ))}
          </select>
        </label>
        <label className="field field--compact">
          <span className="field__label">More is</span>
          <select
            className="field__control field__control--compact"
            aria-label={label("More is")}
            value={block.higherIsBetter === false ? "bad" : "good"}
            onChange={(e) => onChange({ higherIsBetter: e.target.value === "good" })}
          >
            <option value="good">Good (green)</option>
            <option value="bad">Bad (red)</option>
          </select>
        </label>
      </div>
    </>
  );
}
