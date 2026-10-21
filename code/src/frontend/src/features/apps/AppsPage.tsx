import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { ApiClientError } from "../../api/client";
import {
  createApp,
  createAppPassword,
  deleteApp,
  listApps,
  revokeAppPassword,
  updateApp,
  type AppCalendar,
  type AppFilter,
  type BentoApp,
  type CreatedAppPassword,
} from "../../api/apps";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { Panel } from "../../components/ui/Panel";
import { Spinner } from "../../components/ui/Spinner";
import { useAuthStore } from "../../stores/authStore";
import { useSchemaStore } from "../../stores/schemaStore";
import type { ModelTable } from "../../types/api";
import { getCurrentLocale } from "../data-explorer/components/editors/temporalEditing";
import "../notifications/Notifications.css";
import "./Apps.css";

/** Colours for new calendars, one after the other. */
const COLORS = ["#3B82F6", "#14B8A6", "#F59E0B", "#8B5CF6", "#F43F5E", "#22C55E"];

const OPS: { value: AppFilter["op"]; label: string }[] = [
  { value: "eq", label: "is" },
  { value: "neq", label: "is not" },
  { value: "gt", label: "above" },
  { value: "gte", label: "at least" },
  { value: "lt", label: "below" },
  { value: "lte", label: "at most" },
  { value: "like", label: "contains" },
  { value: "null", label: "is empty" },
  { value: "notnull", label: "is not empty" },
];

function errorText(cause: unknown, fallback: string): string {
  return cause instanceof ApiClientError ? cause.message : fallback;
}

function formatWhen(value: string | null | undefined): string {
  if (!value) return "";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : new Intl.DateTimeFormat(getCurrentLocale(), { dateStyle: "medium", timeStyle: "short" }).format(date);
}

/** The column's value type; a computed column counts as its result. */
function valueType(column: { type: string; resultType?: string | null }): string {
  return column.type === "computed" ? (column.resultType ?? "text").toLowerCase() : column.type;
}

/**
 * Apps (Step 5.4): calendar programs on the user's devices show tables as
 * calendars. The rows stay in Bento — added in the app, over the API or by
 * Claude, they show up on the phone. Each device gets its own password.
 */
export function AppsPage() {
  const user = useAuthStore((state) => state.user);
  const [apps, setApps] = useState<BentoApp[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState<BentoApp | "new" | null>(null);

  const load = useCallback(() => {
    listApps()
      .then(setApps)
      .catch((cause) => {
        setApps([]);
        setError(errorText(cause, "The apps could not be loaded."));
      });
  }, []);
  useEffect(() => {
    if (user?.role === "admin") load();
  }, [load, user?.role]);

  if (user?.role !== "admin") {
    return <p className="apps__intro">Apps are set up by an administrator.</p>;
  }

  return (
    <div className="apps">
      <p className="apps__intro">
        Show tables as calendars on your iPhone, Mac, Android (DAVx⁵) or in Thunderbird, over CalDAV. The events stay rows in Bento: what you, the API or Claude add
        or change shows up on your devices.
      </p>
      {error ? <Alert tone="error" message={error} /> : null}
      <Panel
        title="Calendars"
        description="Each calendar is a table: one row, one event. A device signs in with its own password, which you can take back at any time."
        actions={
          editing === null ? (
            <Button type="button" variant="ghost" size="sm" leadingIcon={<Icon name="plus" size={13} />} onClick={() => setEditing("new")}>
              Add calendar app
            </Button>
          ) : null
        }
      >
        {editing !== null ? (
          <AppForm
            app={editing === "new" ? null : editing}
            onCancel={() => setEditing(null)}
            onSaved={() => {
              setEditing(null);
              load();
            }}
          />
        ) : null}
        {apps === null ? (
          <Spinner label="Loading apps" />
        ) : apps.length === 0 && editing === null ? (
          <p className="channels__empty">No calendar app yet. For example: appointments, deadlines from your tasks, birthdays.</p>
        ) : (
          <ul className="channels">
            {apps.map((app) => (
              <AppCard key={app.id} app={app} onEdit={() => setEditing(app)} onChanged={load} onError={setError} />
            ))}
          </ul>
        )}
      </Panel>
    </div>
  );
}

function AppCard({ app, onEdit, onChanged, onError }: { app: BentoApp; onEdit: () => void; onChanged: () => void; onError: (message: string) => void }) {
  const [device, setDevice] = useState("");
  const [busy, setBusy] = useState(false);
  const [created, setCreated] = useState<CreatedAppPassword | null>(null);

  const addDevice = async () => {
    setBusy(true);
    try {
      setCreated(await createAppPassword(app.id, device.trim()));
      setDevice("");
      onChanged();
    } catch (cause) {
      onError(errorText(cause, "The password could not be created."));
    } finally {
      setBusy(false);
    }
  };

  const revoke = async (id: number, name: string) => {
    if (!window.confirm(`Take back the password of “${name}”? The device stops seeing the calendars.`)) return;
    try {
      await revokeAppPassword(app.id, id);
      onChanged();
    } catch (cause) {
      onError(errorText(cause, "The password could not be taken back."));
    }
  };

  const toggle = async () => {
    try {
      await updateApp(app.id, { enabled: !app.enabled });
      onChanged();
    } catch (cause) {
      onError(errorText(cause, "The app could not be changed."));
    }
  };

  const remove = async () => {
    if (!window.confirm(`Delete “${app.name}”? Devices stop seeing its calendars; the tables stay as they are.`)) return;
    try {
      await deleteApp(app.id);
      onChanged();
    } catch (cause) {
      onError(errorText(cause, "The app could not be deleted."));
    }
  };

  return (
    <li className={app.enabled ? "channel" : "channel channel--off"}>
      <div className="channel__main">
        <div className="channel__title">
          <strong>{app.name}</strong>
          <span className="channel__kind">Calendar</span>
          {!app.enabled ? <span className="channel__off">Off</span> : null}
        </div>
        <ul className="apps__calendars">
          {app.config.calendars.map((calendar) => (
            <li key={calendar.slug ?? calendar.name}>
              <span className="apps__swatch" style={{ background: calendar.color ?? "var(--text-3)" }} aria-hidden="true" />
              <strong>{calendar.name}</strong>
              <span className="channel__target">
                {" "}
                · {calendar.table}: {calendar.title}, {calendar.start}
                {calendar.end ? ` → ${calendar.end}` : ""}
                {calendar.filters?.length ? ` · ${calendar.filters.length} ${calendar.filters.length === 1 ? "filter" : "filters"}` : ""}
              </span>
            </li>
          ))}
        </ul>

        <div className="apps__devices">
          <span className="field__label">Devices</span>
          {app.passwords.length === 0 ? <p className="channel__last">No device yet.</p> : null}
          <ul className="apps__device-list">
            {app.passwords.map((password) => (
              <li key={password.id} className="apps__device">
                <span>
                  <strong>{password.name}</strong> <code className="apps__prefix">{password.prefix}…</code>
                </span>
                <span className="channel__last">{password.lastUsedAt ? `Last seen ${formatWhen(password.lastUsedAt)}` : "Not used yet"}</span>
                <Button type="button" variant="ghost" size="sm" aria-label={`Take back the password of ${password.name}`} onClick={() => void revoke(password.id, password.name)}>
                  <Icon name="x" size={13} />
                </Button>
              </li>
            ))}
          </ul>
          <div className="apps__add-device">
            <input
              className="field__control field__control--compact"
              aria-label="Device name"
              placeholder="iPhone, Mac, Thunderbird …"
              value={device}
              onChange={(event) => setDevice(event.target.value)}
            />
            <Button type="button" variant="ghost" size="sm" disabled={busy || !device.trim()} onClick={() => void addDevice()}>
              Add device
            </Button>
          </div>
          {created ? <NewPassword created={created} onClose={() => setCreated(null)} /> : null}
        </div>
      </div>
      <div className="channel__actions">
        <Button type="button" variant="ghost" size="sm" onClick={onEdit}>
          Edit
        </Button>
        <Button type="button" variant="ghost" size="sm" onClick={() => void toggle()}>
          {app.enabled ? "Turn off" : "Turn on"}
        </Button>
        <Button type="button" variant="ghost" size="sm" aria-label={`Delete ${app.name}`} onClick={() => void remove()}>
          <Icon name="trash" size={14} />
        </Button>
      </div>
    </li>
  );
}

/** The new password with the addresses to enter on the device — shown this once. */
function NewPassword({ created, onClose }: { created: CreatedAppPassword; onClose: () => void }) {
  const [copied, setCopied] = useState<string | null>(null);
  const copy = (text: string) => {
    void navigator.clipboard?.writeText(text).then(
      () => setCopied(text),
      () => setCopied(null),
    );
  };
  return (
    <div className="apps__secret" role="region" aria-label="New device password">
      <p className="apps__secret-lead">
        <strong>{created.password.name}</strong>: copy the password now — it is shown only this once.
      </p>
      {created.links
        .filter((link) => link.kind === "caldav")
        .map((link) => (
          <div key={link.url} className="apps__caldav">
            <span className="field__label">CalDAV account</span>
            <dl className="apps__account">
              <dt>Server</dt>
              <dd>
                <code className="apps__url">{link.url}</code>
                <Button type="button" variant="ghost" size="sm" leadingIcon={<Icon name="copy" size={12} />} onClick={() => copy(link.url)}>
                  {copied === link.url ? "Copied" : "Copy"}
                </Button>
              </dd>
              <dt>User name</dt>
              <dd>anything, e.g. your name</dd>
              <dt>Password</dt>
              <dd>
                <code className="apps__url">{created.secret}</code>
                <Button type="button" variant="ghost" size="sm" leadingIcon={<Icon name="copy" size={12} />} onClick={() => copy(created.secret)}>
                  {copied === created.secret ? "Copied" : "Copy"}
                </Button>
              </dd>
            </dl>
            <p className="channel-form__hint">
              iPhone / iPad: Settings → Apps → Calendar → Calendar Accounts → Add Account → Other → Add CalDAV Account. Mac: Calendar → Settings → Accounts → + →
              Other CalDAV Account (Manual). Android: DAVx⁵ with “Login with URL and user name”. Thunderbird: New Calendar → On the Network → the server address.
              Events made, moved or deleted there become rows here.
            </p>
          </div>
        ))}
      <Button type="button" variant="ghost" size="sm" onClick={onClose}>
        Done
      </Button>
    </div>
  );
}

type EditableCalendar = AppCalendar & { key: number };

function AppForm({ app, onCancel, onSaved }: { app: BentoApp | null; onCancel: () => void; onSaved: () => void }) {
  const snapshot = useSchemaStore((state) => state.snapshot);
  const loadSchema = useSchemaStore((state) => state.load);
  useEffect(() => {
    void loadSchema();
  }, [loadSchema]);
  const tables = useMemo(() => (snapshot?.tables ?? []).filter((t) => !t.name.startsWith("_")), [snapshot]);
  const nextKey = useRef(0);
  const [name, setName] = useState(app?.name ?? "");
  const [calendars, setCalendars] = useState<EditableCalendar[]>(() =>
    (app?.config.calendars ?? [{ name: "", table: "", title: "", start: "", color: COLORS[0] }]).map((c) => ({ ...c, key: nextKey.current++ })),
  );
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const change = (key: number, patch: Partial<AppCalendar>) => setCalendars((current) => current.map((c) => (c.key === key ? { ...c, ...patch } : c)));

  const save = async () => {
    const payload = calendars.map(({ key: _key, ...calendar }) => ({
      ...calendar,
      end: calendar.end || null,
      allDay: calendar.allDay || null,
      description: calendar.description || null,
      location: calendar.location || null,
      recurrence: calendar.recurrence || null,
      color: calendar.color || null,
      filters: (calendar.filters ?? []).filter((f) => f.column),
    }));
    setSaving(true);
    setError(null);
    try {
      if (app) await updateApp(app.id, { name: name.trim(), calendars: payload });
      else await createApp(name.trim(), payload);
      onSaved();
    } catch (cause) {
      setError(errorText(cause, "The app could not be saved."));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="channel-form">
      <label className="field field--compact">
        <span className="field__label">Name</span>
        <input className="field__control field__control--compact" value={name} placeholder="My calendar" onChange={(e) => setName(e.target.value)} />
      </label>
      <ol className="report-blocks">
        {calendars.map((calendar, index) => (
          <li key={calendar.key} className="report-block">
            <div className="report-block__head">
              <span className="channel__kind">Calendar {index + 1}</span>
              <span className="apps__spacer" />
              {calendars.length > 1 ? (
                <Button type="button" variant="ghost" size="sm" aria-label={`Remove calendar ${index + 1}`} onClick={() => setCalendars((c) => c.filter((x) => x.key !== calendar.key))}>
                  <Icon name="x" size={13} />
                </Button>
              ) : null}
            </div>
            <CalendarFields calendar={calendar} index={index} tables={tables} onChange={(patch) => change(calendar.key, patch)} />
          </li>
        ))}
      </ol>
      <div>
        <Button
          type="button"
          variant="ghost"
          size="sm"
          leadingIcon={<Icon name="plus" size={12} />}
          onClick={() => setCalendars((c) => [...c, { name: "", table: "", title: "", start: "", color: COLORS[c.length % COLORS.length], key: nextKey.current++ }])}
        >
          Another calendar
        </Button>
      </div>
      {error ? <Alert tone="error" message={error} /> : null}
      <div className="channel-form__actions">
        <Button type="button" variant="primary" disabled={saving || !name.trim()} onClick={() => void save()}>
          {saving ? "Saving…" : app ? "Save app" : "Add app"}
        </Button>
        <Button type="button" variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
      </div>
    </div>
  );
}

function CalendarFields({
  calendar,
  index,
  tables,
  onChange,
}: {
  calendar: EditableCalendar;
  index: number;
  tables: ModelTable[];
  onChange: (patch: Partial<AppCalendar>) => void;
}) {
  const table = tables.find((t) => t.name === calendar.table);
  const columns = (table?.columns ?? []).filter((c) => !c.primaryKey);
  const temporal = columns.filter((c) => ["date", "datetime"].includes(valueType(c)));
  const yesNo = columns.filter((c) => valueType(c) === "boolean");
  const label = (text: string) => `${text} (calendar ${index + 1})`;
  const named = (c: { name: string; displayName?: string | null }) => c.displayName ?? c.name;

  const select = (text: string, value: string | null | undefined, options: { name: string; displayName?: string | null }[], onSelect: (value: string) => void, optional = true) => (
    <label className="field field--compact">
      <span className="field__label">{text}</span>
      <select className="field__control field__control--compact" aria-label={label(text)} value={value ?? ""} onChange={(e) => onSelect(e.target.value)}>
        <option value="">{optional ? "None" : "Choose…"}</option>
        {options.map((c) => (
          <option key={c.name} value={c.name}>
            {named(c)}
          </option>
        ))}
      </select>
    </label>
  );

  return (
    <>
      <div className="channel-form__grid">
        <label className="field field--compact">
          <span className="field__label">Calendar name</span>
          <input className="field__control field__control--compact" aria-label={label("Calendar name")} value={calendar.name} placeholder="Appointments" onChange={(e) => onChange({ name: e.target.value })} />
        </label>
        <label className="field field--compact">
          <span className="field__label">Table</span>
          <select
            className="field__control field__control--compact"
            aria-label={label("Table")}
            value={calendar.table}
            onChange={(e) => {
              const next = tables.find((t) => t.name === e.target.value);
              const nextColumns = (next?.columns ?? []).filter((c) => !c.primaryKey);
              const start = nextColumns.find((c) => ["datetime", "date"].includes(valueType(c)));
              const title = nextColumns.find((c) => valueType(c) === "text");
              onChange({ table: e.target.value, start: start?.name ?? "", title: title?.name ?? "", end: null, allDay: null, description: null, location: null, recurrence: null, filters: [] });
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
        <label className="field field--compact">
          <span className="field__label">Colour</span>
          <input className="field__control field__control--compact apps__color" type="color" aria-label={label("Colour")} value={(calendar.color ?? COLORS[0]).toLowerCase()} onChange={(e) => onChange({ color: e.target.value })} />
        </label>
      </div>
      {table ? (
        <>
          <div className="channel-form__grid">
            {select("Title", calendar.title, columns, (title) => onChange({ title }), false)}
            {select("Starts", calendar.start, temporal, (start) => onChange({ start }), false)}
            {select("Ends", calendar.end, temporal, (end) => onChange({ end: end || null }))}
            {select("All day when", calendar.allDay, yesNo, (allDay) => onChange({ allDay: allDay || null }))}
          </div>
          <div className="channel-form__grid">
            {select("Location", calendar.location, columns, (location) => onChange({ location: location || null }))}
            {select("Notes", calendar.description, columns, (description) => onChange({ description: description || null }))}
            {select("Repeats", calendar.recurrence, columns.filter((c) => valueType(c) === "text"), (recurrence) => onChange({ recurrence: recurrence || null }))}
            <label className="field field--compact">
              <span className="field__label">Without an end, minutes</span>
              <input
                className="field__control field__control--compact"
                inputMode="numeric"
                aria-label={label("Duration")}
                value={calendar.duration ?? ""}
                placeholder="60"
                onChange={(e) => onChange({ duration: e.target.value ? Number(e.target.value) : null })}
              />
            </label>
          </div>
          <div className="apps__filters">
            <span className="field__label">Only rows where</span>
            {(calendar.filters ?? []).map((filter, i) => (
              <div key={i} className="apps__filter">
                <select
                  className="field__control field__control--compact"
                  aria-label={label(`Filter ${i + 1} column`)}
                  value={filter.column}
                  onChange={(e) => onChange({ filters: (calendar.filters ?? []).map((f, j) => (j === i ? { ...f, column: e.target.value } : f)) })}
                >
                  {columns.map((c) => (
                    <option key={c.name} value={c.name}>
                      {named(c)}
                    </option>
                  ))}
                </select>
                <select
                  className="field__control field__control--compact"
                  aria-label={label(`Filter ${i + 1} condition`)}
                  value={filter.op}
                  onChange={(e) => onChange({ filters: (calendar.filters ?? []).map((f, j) => (j === i ? { ...f, op: e.target.value as AppFilter["op"] } : f)) })}
                >
                  {OPS.map((op) => (
                    <option key={op.value} value={op.value}>
                      {op.label}
                    </option>
                  ))}
                </select>
                {filter.op === "null" || filter.op === "notnull" ? null : (
                  <input
                    className="field__control field__control--compact"
                    aria-label={label(`Filter ${i + 1} value`)}
                    value={filter.value ?? ""}
                    placeholder="true, open, 2026-01-01 …"
                    onChange={(e) => onChange({ filters: (calendar.filters ?? []).map((f, j) => (j === i ? { ...f, value: e.target.value } : f)) })}
                  />
                )}
                <Button type="button" variant="ghost" size="sm" aria-label={label(`Remove filter ${i + 1}`)} onClick={() => onChange({ filters: (calendar.filters ?? []).filter((_, j) => j !== i) })}>
                  <Icon name="x" size={12} />
                </Button>
              </div>
            ))}
            <div>
              <Button
                type="button"
                variant="ghost"
                size="sm"
                leadingIcon={<Icon name="plus" size={12} />}
                onClick={() => onChange({ filters: [...(calendar.filters ?? []), { column: columns[0]?.name ?? "", op: "eq", value: "" }] })}
              >
                Filter
              </Button>
            </div>
          </div>
          <p className="channel-form__hint">
            A date column makes all-day events; a date-time column timed ones, in the workspace's time zone. For a title made of several columns, add a computed
            column to the table. What the table has no column for (alarms, attendees) is kept for events made on a device.
          </p>
        </>
      ) : null}
    </>
  );
}
