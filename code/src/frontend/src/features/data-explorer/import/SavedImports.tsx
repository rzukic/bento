import { useEffect, useState } from "react";
import {
  deleteImportSource,
  listImportSourceRuns,
  runImportSource,
  updateImportSource,
  type ImportHeader,
  type ImportSchedule,
  type ImportSource,
  type ImportSourceRun,
} from "../../../api/import";
import { listCredentials } from "../../../api/credentials";
import { ApiClientError } from "../../../api/client";
import { Icon } from "../../../components/common/Icon";
import { Button } from "../../../components/ui/Button";
import { getCurrentLocale } from "../components/editors/temporalEditing";

/**
 * Step 5.1: an export fetched from a URL instead of an upload, and imports
 * saved with their URL, mapping and schedule that run on their own.
 */

export type UrlSource = { url: string; headers: ImportHeader[] };

function formatWhen(value: string | null): string {
  if (!value) return "";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : new Intl.DateTimeFormat(getCurrentLocale(), { dateStyle: "medium", timeStyle: "short" }).format(date);
}

function plural(count: number, one: string, many = `${one}s`): string {
  return `${count} ${count === 1 ? one : many}`;
}

export function scheduleText(schedule: ImportSchedule | undefined): string {
  if (schedule?.kind === "daily") return `Every day at ${schedule.at}`;
  if (schedule?.kind === "hours") return schedule.every === 1 ? "Every hour" : `Every ${schedule.every} hours`;
  return "When you run it";
}

/** What a run did, in a line: "3 new, 1 updated, 2 categorised" or its error. */
export function runSummary(run: ImportSourceRun): string {
  if (run.status === "failed") return run.error ?? "Failed.";
  if (run.status === "running") return "Running…";
  const parts = [
    run.inserted > 0 ? `${run.inserted} new` : null,
    run.updated > 0 ? `${run.updated} updated` : null,
    run.categorised > 0 ? `${run.categorised} categorised` : null,
    run.invalid > 0 ? plural(run.invalid, "problem") : null,
  ].filter(Boolean);
  return parts.length > 0 ? parts.join(", ") : "Nothing new";
}

function errorText(cause: unknown, fallback: string): string {
  return cause instanceof ApiClientError ? cause.message : fallback;
}

/** The URL and its headers; credentials from the vault go in as {{name}}. */
export function UrlSourceForm({
  value,
  onChange,
  onFetch,
  busy,
  isAdmin,
}: {
  value: UrlSource;
  onChange: (next: UrlSource) => void;
  onFetch: () => void;
  busy: boolean;
  isAdmin: boolean;
}) {
  const [credentials, setCredentials] = useState<string[]>([]);
  useEffect(() => {
    if (!isAdmin) return;
    listCredentials()
      .then((list) => setCredentials(list.map((c) => c.name)))
      .catch(() => setCredentials([]));
  }, [isAdmin]);

  const setHeader = (index: number, patch: Partial<ImportHeader>) =>
    onChange({ ...value, headers: value.headers.map((h, i) => (i === index ? { ...h, ...patch } : h)) });

  return (
    <div className="import-url">
      <div className="import-url__row">
        <label className="field import-url__address">
          <span className="field__label">Address of the export</span>
          <input
            className="field__control"
            type="url"
            placeholder="https://…/export.csv"
            value={value.url}
            onChange={(event) => onChange({ ...value, url: event.target.value })}
            onKeyDown={(event) => {
              if (event.key === "Enter" && value.url.trim()) onFetch();
            }}
          />
        </label>
        <Button type="button" variant="primary" onClick={onFetch} disabled={busy || !value.url.trim()}>
          {busy ? "Fetching…" : "Fetch"}
        </Button>
      </div>
      {value.headers.map((header, index) => (
        <div className="import-url__header" key={index}>
          <input
            className="field__control field__control--compact"
            aria-label={`Header ${index + 1} name`}
            placeholder="Authorization"
            value={header.name}
            onChange={(event) => setHeader(index, { name: event.target.value })}
          />
          <input
            className="field__control field__control--compact"
            aria-label={`Header ${index + 1} value`}
            placeholder={isAdmin ? "Bearer {{credential}}" : "Value"}
            value={header.value}
            onChange={(event) => setHeader(index, { value: event.target.value })}
          />
          <Button
            type="button"
            variant="ghost"
            size="sm"
            aria-label={`Remove header ${index + 1}`}
            onClick={() => onChange({ ...value, headers: value.headers.filter((_, i) => i !== index) })}
          >
            <Icon name="trash" size={14} />
          </Button>
        </div>
      ))}
      <div className="import-url__foot">
        <Button type="button" variant="ghost" size="sm" onClick={() => onChange({ ...value, headers: [...value.headers, { name: "", value: "" }] })}>
          Add header
        </Button>
        <span className="import-url__hint">
          Only https, never addresses inside your network.{" "}
          {isAdmin
            ? credentials.length > 0
              ? `A key or token from the vault goes in as {{name}}: ${credentials.map((c) => `{{${c}}}`).join(", ")}.`
              : "Keys and tokens belong in the vault (Functions → Credentials) and go in as {{name}}."
            : "Keys from the vault can only be used by admins."}
        </span>
      </div>
    </div>
  );
}

/** Name and schedule for saving the current URL, mapping and settings as an import that runs on its own. */
export function SaveScheduleForm({
  initialName,
  initialSchedule,
  editing,
  busy,
  onSave,
}: {
  initialName: string;
  initialSchedule: ImportSchedule | undefined;
  editing: boolean;
  busy: boolean;
  onSave: (name: string, schedule: ImportSchedule, asNew: boolean) => void;
}) {
  const [name, setName] = useState(initialName);
  const [kind, setKind] = useState<ImportSchedule["kind"]>(initialSchedule?.kind ?? "daily");
  const [at, setAt] = useState(initialSchedule?.kind === "daily" ? initialSchedule.at : "06:00");
  const [every, setEvery] = useState(initialSchedule?.kind === "hours" ? initialSchedule.every : 6);
  const schedule: ImportSchedule = kind === "daily" ? { kind, at } : kind === "hours" ? { kind, every } : { kind: "manual" };

  return (
    <div className="import-schedule">
      <label className="field field--compact">
        <span className="field__label">Name</span>
        <input className="field__control field__control--compact" value={name} placeholder="e.g. Girokonto" onChange={(event) => setName(event.target.value)} />
      </label>
      <label className="field field--compact">
        <span className="field__label">Runs</span>
        <select className="field__control field__control--compact" value={kind} onChange={(event) => setKind(event.target.value as ImportSchedule["kind"])}>
          <option value="daily">Every day</option>
          <option value="hours">Every few hours</option>
          <option value="manual">Only when I run it</option>
        </select>
      </label>
      {kind === "daily" ? (
        <label className="field field--compact">
          <span className="field__label">At</span>
          <input className="field__control field__control--compact" type="time" value={at} onChange={(event) => setAt(event.target.value)} />
        </label>
      ) : null}
      {kind === "hours" ? (
        <label className="field field--compact">
          <span className="field__label">Every … hours</span>
          <input
            className="field__control field__control--compact"
            type="number"
            min={1}
            max={168}
            value={every}
            onChange={(event) => setEvery(Number(event.target.value))}
          />
        </label>
      ) : null}
      <div className="import-schedule__actions">
        <Button type="button" variant="primary" disabled={busy || !name.trim()} onClick={() => onSave(name.trim(), schedule, false)}>
          {editing ? "Update saved import" : "Save import"}
        </Button>
        {editing ? (
          <Button type="button" variant="ghost" disabled={busy || !name.trim()} onClick={() => onSave(name.trim(), schedule, true)}>
            Save as new
          </Button>
        ) : null}
      </div>
    </div>
  );
}

/** The table's saved imports: when they run, how the last run went, run now, history, edit, pause, delete. */
export function SavedImportsList({
  table,
  sources,
  editingId,
  onEdit,
  onChanged,
}: {
  table: string;
  sources: ImportSource[];
  editingId: number | null;
  onEdit: (source: ImportSource) => void;
  onChanged: () => void;
}) {
  const [busy, setBusy] = useState<number | null>(null);
  const [messages, setMessages] = useState<Record<number, string>>({});
  const [history, setHistory] = useState<Record<number, ImportSourceRun[]>>({});

  const act = async (id: number, action: () => Promise<string | null>) => {
    setBusy(id);
    try {
      const message = await action();
      setMessages((current) => {
        const next = { ...current };
        if (message) next[id] = message;
        else delete next[id];
        return next;
      });
      onChanged();
    } catch (cause) {
      setMessages((current) => ({ ...current, [id]: errorText(cause, "That did not work.") }));
    } finally {
      setBusy(null);
    }
  };

  const toggleHistory = async (id: number) => {
    if (history[id]) {
      setHistory(({ [id]: _, ...rest }) => rest);
      return;
    }
    try {
      const runs = await listImportSourceRuns(table, id);
      setHistory((current) => ({ ...current, [id]: runs }));
    } catch (cause) {
      setMessages((current) => ({ ...current, [id]: errorText(cause, "The runs could not be loaded.") }));
    }
  };

  return (
    <ul className="saved-imports">
      {sources.map((source) => {
        const last = source.lastRun;
        let host = source.config.url;
        try {
          host = new URL(source.config.url).host;
        } catch {
          // A URL with a {{credential}} in it may not parse; show it as it is.
        }
        return (
          <li key={source.id} className={["saved-import", editingId === source.id ? "saved-import--editing" : "", source.enabled ? "" : "saved-import--paused"].filter(Boolean).join(" ")}>
            <div className="saved-import__main">
              <div className="saved-import__title">
                <strong>{source.name}</strong>
                <span className="saved-import__meta">
                  {host} · {source.enabled ? scheduleText(source.config.schedule) : "Paused"}
                  {source.enabled && source.nextRunAt ? ` · next ${formatWhen(source.nextRunAt)}` : ""}
                </span>
              </div>
              {last ? (
                <p className={`saved-import__last saved-import__last--${last.status}`}>
                  <Icon name={last.status === "failed" ? "x" : "check"} size={13} />
                  <span>
                    {formatWhen(last.finishedAt ?? last.startedAt)}: {runSummary(last)}
                  </span>
                </p>
              ) : (
                <p className="saved-import__last">Not run yet.</p>
              )}
              {messages[source.id] ? <p className="saved-import__message" role="status">{messages[source.id]}</p> : null}
              {history[source.id] ? (
                <table className="saved-import__runs">
                  <tbody>
                    {history[source.id].map((run) => (
                      <tr key={run.id} className={`saved-import__run saved-import__run--${run.status}`}>
                        <td>{formatWhen(run.startedAt)}</td>
                        <td>{run.trigger === "schedule" ? "scheduled" : "by hand"}</td>
                        <td>{runSummary(run)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              ) : null}
            </div>
            <div className="saved-import__actions">
              <Button
                type="button"
                variant="ghost"
                size="sm"
                disabled={busy !== null}
                onClick={() =>
                  void act(source.id, async () => {
                    const run = await runImportSource(table, source.id);
                    return run.status === "failed" ? run.error : `Done: ${runSummary(run)}.`;
                  })
                }
              >
                {busy === source.id ? "Running…" : "Run now"}
              </Button>
              <Button type="button" variant="ghost" size="sm" onClick={() => void toggleHistory(source.id)}>
                {history[source.id] ? "Hide runs" : "Runs"}
              </Button>
              <Button type="button" variant="ghost" size="sm" disabled={busy !== null} onClick={() => onEdit(source)}>
                Edit
              </Button>
              <Button
                type="button"
                variant="ghost"
                size="sm"
                disabled={busy !== null}
                onClick={() => void act(source.id, async () => (await updateImportSource(table, source.id, { enabled: !source.enabled }), null))}
              >
                {source.enabled ? "Pause" : "Resume"}
              </Button>
              <Button
                type="button"
                variant="ghost"
                size="sm"
                aria-label={`Delete ${source.name}`}
                disabled={busy !== null}
                onClick={() => {
                  if (window.confirm(`Delete the saved import “${source.name}”? Rows it imported stay.`))
                    void act(source.id, async () => (await deleteImportSource(table, source.id), null));
                }}
              >
                <Icon name="trash" size={14} />
              </Button>
            </div>
          </li>
        );
      })}
    </ul>
  );
}
