import { useCallback, useEffect, useState, type InputHTMLAttributes } from "react";
import { ApiClientError } from "../../api/client";
import {
  createChannel,
  deleteChannel,
  listChannels,
  listDeliveries,
  testChannel,
  updateChannel,
  type ChannelConfig,
  type ChannelKind,
  type NotificationChannel,
  type NotificationDelivery,
} from "../../api/notifications";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { Panel } from "../../components/ui/Panel";
import { Spinner } from "../../components/ui/Spinner";
import { getCurrentLocale } from "../data-explorer/components/editors/temporalEditing";
import "./Notifications.css";

const KIND_LABELS: Record<ChannelKind, string> = { email: "Email", slack: "Slack", webhook: "Webhook" };
const SECURITY_PORTS: Record<string, number> = { starttls: 587, ssl: 465, none: 25 };

function formatWhen(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : new Intl.DateTimeFormat(getCurrentLocale(), { dateStyle: "medium", timeStyle: "short" }).format(date);
}

function errorText(cause: unknown, fallback: string): string {
  return cause instanceof ApiClientError ? cause.message : fallback;
}

/**
 * Step 5.2 (Settings, admins): where Bento reaches you — email over SMTP,
 * Slack, or a signed webhook. A test shows at once whether a channel works;
 * every delivery is logged. What is sent when comes with rules (Step 5.3).
 */
export function NotificationsPanel() {
  const [channels, setChannels] = useState<NotificationChannel[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState<NotificationChannel | "new" | null>(null);
  const [busy, setBusy] = useState<number | null>(null);
  const [results, setResults] = useState<Record<number, NotificationDelivery | string>>({});
  const [history, setHistory] = useState<Record<number, NotificationDelivery[]>>({});

  const load = useCallback(() => {
    listChannels()
      .then(setChannels)
      .catch((cause) => {
        setChannels([]);
        setError(errorText(cause, "The channels could not be loaded."));
      });
  }, []);
  useEffect(load, [load]);

  const test = async (channel: NotificationChannel) => {
    setBusy(channel.id);
    try {
      const delivery = await testChannel(channel.id);
      setResults((current) => ({ ...current, [channel.id]: delivery }));
      load();
    } catch (cause) {
      setResults((current) => ({ ...current, [channel.id]: errorText(cause, "The test could not be sent.") }));
    } finally {
      setBusy(null);
    }
  };

  const toggleHistory = async (channel: NotificationChannel) => {
    if (history[channel.id]) {
      setHistory(({ [channel.id]: _, ...rest }) => rest);
      return;
    }
    try {
      const deliveries = await listDeliveries(channel.id);
      setHistory((current) => ({ ...current, [channel.id]: deliveries }));
    } catch (cause) {
      setResults((current) => ({ ...current, [channel.id]: errorText(cause, "The deliveries could not be loaded.") }));
    }
  };

  const toggleEnabled = async (channel: NotificationChannel) => {
    try {
      await updateChannel(channel.id, { enabled: !channel.enabled });
      load();
    } catch (cause) {
      setError(errorText(cause, "The channel could not be changed."));
    }
  };

  const remove = async (channel: NotificationChannel) => {
    if (!window.confirm(`Delete the channel “${channel.name}”?`)) return;
    try {
      await deleteChannel(channel.id);
      load();
    } catch (cause) {
      setError(errorText(cause, "The channel could not be deleted."));
    }
  };

  return (
    <Panel
      title="Notifications"
      description="Where Bento reaches you: email, Slack or a webhook (n8n, Home Assistant, Zapier …). Send a test to check a channel."
      actions={
        editing === null ? (
          <Button type="button" variant="ghost" size="sm" leadingIcon={<Icon name="plus" size={13} />} onClick={() => setEditing("new")}>
            Add channel
          </Button>
        ) : null
      }
    >
      {error ? <Alert tone="error" message={error} /> : null}
      {editing !== null ? (
        <ChannelForm
          channel={editing === "new" ? null : editing}
          onCancel={() => setEditing(null)}
          onSaved={() => {
            setEditing(null);
            load();
          }}
        />
      ) : null}
      {channels === null ? (
        <Spinner label="Loading channels" />
      ) : channels.length === 0 && editing === null ? (
        <p className="channels__empty">No channels yet. Add one to be told when an import fails or a target is reached.</p>
      ) : (
        <ul className="channels">
          {channels.map((channel) => {
            const result = results[channel.id];
            const last = channel.lastDelivery;
            return (
              <li key={channel.id} className={channel.enabled ? "channel" : "channel channel--off"}>
                <div className="channel__main">
                  <div className="channel__title">
                    <strong>{channel.name}</strong>
                    <span className="channel__kind">{KIND_LABELS[channel.kind]}</span>
                    <span className="channel__target">{channel.target}</span>
                    {!channel.enabled ? <span className="channel__off">Off</span> : null}
                  </div>
                  {last ? (
                    <p className={`channel__last channel__last--${last.status}`}>
                      <Icon name={last.status === "sent" ? "check" : "x"} size={12} />
                      <span>
                        {formatWhen(last.createdAt)}: {last.title} — {last.status === "sent" ? "sent" : last.error}
                      </span>
                    </p>
                  ) : (
                    <p className="channel__last">Nothing sent yet.</p>
                  )}
                  {typeof result === "string" ? <p className="channel__result channel__result--failed" role="status">{result}</p> : null}
                  {result && typeof result !== "string" ? (
                    <p className={`channel__result channel__result--${result.status}`} role="status">
                      {result.status === "sent" ? `Test sent in ${result.durationMs} ms. Check that it arrived.` : `Test failed: ${result.error}`}
                    </p>
                  ) : null}
                  {history[channel.id] ? (
                    <table className="channel__history">
                      <tbody>
                        {history[channel.id].map((delivery) => (
                          <tr key={delivery.id} className={`channel__delivery--${delivery.status}`}>
                            <td>{formatWhen(delivery.createdAt)}</td>
                            <td>{delivery.title}</td>
                            <td>{delivery.status === "sent" ? "sent" : delivery.error}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  ) : null}
                </div>
                <div className="channel__actions">
                  <Button type="button" variant="ghost" size="sm" disabled={busy !== null} onClick={() => void test(channel)}>
                    {busy === channel.id ? "Sending…" : "Send test"}
                  </Button>
                  <Button type="button" variant="ghost" size="sm" onClick={() => void toggleHistory(channel)}>
                    {history[channel.id] ? "Hide log" : "Log"}
                  </Button>
                  <Button type="button" variant="ghost" size="sm" onClick={() => setEditing(channel)}>
                    Edit
                  </Button>
                  <Button type="button" variant="ghost" size="sm" onClick={() => void toggleEnabled(channel)}>
                    {channel.enabled ? "Turn off" : "Turn on"}
                  </Button>
                  <Button type="button" variant="ghost" size="sm" aria-label={`Delete ${channel.name}`} onClick={() => void remove(channel)}>
                    <Icon name="trash" size={14} />
                  </Button>
                </div>
              </li>
            );
          })}
        </ul>
      )}
    </Panel>
  );
}

/** Add or change a channel. Stored secrets are not shown: leave the field empty to keep one. */
function ChannelForm({ channel, onCancel, onSaved }: { channel: NotificationChannel | null; onCancel: () => void; onSaved: () => void }) {
  const [kind, setKind] = useState<ChannelKind>(channel?.kind ?? "email");
  const [name, setName] = useState(channel?.name ?? "");
  const [to, setTo] = useState((channel?.config.to ?? []).join(", "));
  const [from, setFrom] = useState(channel?.config.from ?? "");
  const [host, setHost] = useState(channel?.config.smtpHost ?? "");
  const [security, setSecurity] = useState<"starttls" | "ssl" | "none">(channel?.config.smtpSecurity ?? "starttls");
  const [port, setPort] = useState(channel?.config.smtpPort ? String(channel.config.smtpPort) : "");
  const [user, setUser] = useState(channel?.config.smtpUser ?? "");
  const [password, setPassword] = useState("");
  const [url, setUrl] = useState("");
  const [secret, setSecret] = useState("");
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const stored = (has: boolean | undefined) => (channel && has ? "Stored — leave empty to keep" : "");

  const save = async () => {
    const config: ChannelConfig =
      kind === "email"
        ? {
            to: to.split(/[,;\s]+/).filter(Boolean),
            from: from.trim() || null,
            smtpHost: host.trim(),
            smtpPort: port.trim() ? Number(port) : null,
            smtpSecurity: security,
            smtpUser: user.trim() || null,
            ...(password ? { smtpPassword: password } : {}),
          }
        : { ...(url.trim() ? { url: url.trim() } : {}), ...(kind === "webhook" && secret ? { secret } : {}) };
    setSaving(true);
    setError(null);
    try {
      if (channel) await updateChannel(channel.id, { name: name.trim(), config });
      else await createChannel(name.trim(), kind, config);
      onSaved();
    } catch (cause) {
      setError(errorText(cause, "The channel could not be saved."));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="channel-form">
      <div className="channel-form__grid">
        <label className="field field--compact">
          <span className="field__label">Kind</span>
          <select className="field__control field__control--compact" value={kind} disabled={!!channel} onChange={(event) => setKind(event.target.value as ChannelKind)}>
            <option value="email">Email</option>
            <option value="slack">Slack</option>
            <option value="webhook">Webhook</option>
          </select>
        </label>
        <Field label="Name" value={name} onChange={setName} placeholder={kind === "email" ? "My email" : kind === "slack" ? "#finance" : "n8n"} />
      </div>

      {kind === "email" ? (
        <>
          <div className="channel-form__grid">
            <Field label="Send to" value={to} onChange={setTo} placeholder="me@example.com, partner@example.com" wide />
            <Field label="From" value={from} onChange={setFrom} placeholder={user.includes("@") ? user : "bento@example.com"} />
          </div>
          <div className="channel-form__grid">
            <Field label="SMTP server" value={host} onChange={setHost} placeholder="smtp.example.com" />
            <label className="field field--compact">
              <span className="field__label">Security</span>
              <select className="field__control field__control--compact" value={security} onChange={(event) => setSecurity(event.target.value as typeof security)}>
                <option value="starttls">STARTTLS (587)</option>
                <option value="ssl">TLS (465)</option>
                <option value="none">None (local relay)</option>
              </select>
            </label>
            <Field label="Port" value={port} onChange={setPort} placeholder={String(SECURITY_PORTS[security])} inputMode="numeric" />
          </div>
          <div className="channel-form__grid">
            <Field label="User" value={user} onChange={setUser} placeholder="bento@example.com" />
            <Field label="Password" value={password} onChange={setPassword} type="password" placeholder={stored(channel?.hasPassword)} autoComplete="new-password" />
          </div>
          <p className="channel-form__hint">For Gmail and Outlook use an app password. The password is stored encrypted and never shown again.</p>
        </>
      ) : (
        <>
          <div className="channel-form__grid">
            <Field
              label={kind === "slack" ? "Webhook address" : "Address"}
              value={url}
              onChange={setUrl}
              type="password"
              placeholder={stored(channel?.hasUrl) || (kind === "slack" ? "https://hooks.slack.com/services/…" : "https://n8n.example.com/webhook/…")}
              autoComplete="off"
              wide
            />
            {kind === "webhook" ? (
              <Field label="Signing secret (optional)" value={secret} onChange={setSecret} type="password" placeholder={stored(channel?.hasSecret)} autoComplete="new-password" />
            ) : null}
          </div>
          <p className="channel-form__hint">
            {kind === "slack"
              ? "In Slack: Apps → Incoming Webhooks → add to a channel, then copy the address. Mattermost and other Slack-compatible webhooks work too. The address is stored encrypted."
              : "Bento posts JSON (event, title, text, link, facts). With a secret, X-Bento-Signature carries sha256= and the HMAC-SHA256 of the body, so the receiver can check it came from Bento."}
          </p>
        </>
      )}

      {error ? <Alert tone="error" message={error} /> : null}
      <div className="channel-form__actions">
        <Button type="button" variant="primary" disabled={saving || !name.trim()} onClick={() => void save()}>
          {saving ? "Saving…" : channel ? "Save channel" : "Add channel"}
        </Button>
        <Button type="button" variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
      </div>
    </div>
  );
}

function Field({
  label,
  value,
  onChange,
  wide,
  ...props
}: { label: string; value: string; onChange: (value: string) => void; wide?: boolean } & Omit<InputHTMLAttributes<HTMLInputElement>, "value" | "onChange">) {
  return (
    <label className={wide ? "field field--compact channel-form__wide" : "field field--compact"}>
      <span className="field__label">{label}</span>
      <input className="field__control field__control--compact" value={value} onChange={(event) => onChange(event.target.value)} {...props} />
    </label>
  );
}
