import { useEffect, useState } from "react";
import { createApiKey, listApiKeys, revokeApiKey, type ApiKey, type ApiKeyScope } from "../../api/connections";
import { MCP_PERMISSIONS, type McpPermission } from "../../api/mcp";
import { PermissionChecklist } from "./PermissionChecklist";
import { ApiClientError } from "../../api/client";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { ConfirmDialog } from "../../components/ui/Dialog";
import { Spinner } from "../../components/ui/Spinner";
import { getCurrentLocale } from "../data-explorer/components/editors/temporalEditing";

const EXPIRY_OPTIONS = [
  { value: "", label: "Never expires" },
  { value: "30", label: "30 days" },
  { value: "90", label: "90 days" },
  { value: "365", label: "1 year" },
];

function formatWhen(value: string | null): string {
  if (!value) return "—";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return new Intl.DateTimeFormat(getCurrentLocale(), { dateStyle: "medium", timeStyle: "short" }).format(date);
}

function message(cause: unknown, fallback: string): string {
  if (cause instanceof ApiClientError) return cause.status === 403 ? "Only admins manage API keys." : cause.message;
  return fallback;
}

export function ApiKeysPanel() {
  const [keys, setKeys] = useState<ApiKey[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [name, setName] = useState("");
  const [scope, setScope] = useState<ApiKeyScope | "permissions">("read");
  // Keys for scripts that change the schema, functions or price sources: chosen like a connected app's.
  const [permissions, setPermissions] = useState<McpPermission[]>(["data.read"]);
  const [expiry, setExpiry] = useState("");
  const [creating, setCreating] = useState(false);
  const [created, setCreated] = useState<{ key: string; name: string } | null>(null);
  const [copied, setCopied] = useState(false);
  const [revoking, setRevoking] = useState<ApiKey | null>(null);

  const reload = () =>
    listApiKeys()
      .then(setKeys)
      .catch((cause) => {
        setKeys([]);
        setError(message(cause, "API keys could not be loaded."));
      });

  useEffect(() => {
    void reload();
  }, []);

  const create = async () => {
    setCreating(true);
    setError(null);
    setCopied(false);
    try {
      const days = expiry ? Number(expiry) : undefined;
      const result = scope === "permissions" ? await createApiKey(name.trim(), "read-write", days, permissions) : await createApiKey(name.trim(), scope, days);
      setCreated({ key: result.key, name: result.apiKey.name });
      setName("");
      await reload();
    } catch (cause) {
      setError(message(cause, "The key could not be created."));
    } finally {
      setCreating(false);
    }
  };

  const revoke = async (key: ApiKey) => {
    setRevoking(null);
    try {
      await revokeApiKey(key.id);
      await reload();
    } catch (cause) {
      setError(message(cause, "The key could not be revoked."));
    }
  };

  return (
    <div className="api-keys">
      <p className="api-keys__intro">
        A key lets a script or another tool use the API as you. <strong>Read</strong> keys read data and run queries;{" "}
        <strong>read-write</strong> keys also create, change and delete records and import files. With <strong>Choose…</strong> you tick
        what a key may do, also the schema and functions. No key can manage users, keys or credentials.
      </p>

      {error ? <Alert tone="error" message={error} /> : null}

      {created ? (
        <div className="api-keys__new" role="status">
          <div className="api-keys__new-head">
            <Icon name="check" size={14} />
            <strong>“{created.name}” is ready.</strong> Copy the key now — it is shown only once.
          </div>
          <div className="api-keys__secret">
            <code aria-label="New API key">{created.key}</code>
            <Button
              type="button"
              variant="primary"
              size="sm"
              leadingIcon={<Icon name={copied ? "check" : "copy"} size={13} />}
              onClick={() => {
                void navigator.clipboard?.writeText(created.key).then(() => setCopied(true), () => setCopied(false));
              }}
            >
              {copied ? "Copied" : "Copy"}
            </Button>
          </div>
          <pre className="api-keys__usage">{`curl "${window.location.origin}/api/v1/data/<table>" \\\n  -H "X-API-Key: ${created.key.slice(0, 12)}…"`}</pre>
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={() => {
              setCreated(null);
              void reload();
            }}
          >
            Done
          </Button>
        </div>
      ) : null}

      <form
        className="api-keys__form"
        onSubmit={(event) => {
          event.preventDefault();
          if (name.trim()) void create();
        }}
      >
        <label className="api-keys__field api-keys__field--grow">
          <span>Name</span>
          <input className="field__control field__control--compact" value={name} placeholder="e.g. Nightly bank sync" onChange={(event) => setName(event.target.value)} />
        </label>
        <label className="api-keys__field">
          <span>Access</span>
          <select className="field__control field__control--compact" value={scope} onChange={(event) => setScope(event.target.value as ApiKeyScope | "permissions")}>
            <option value="read">Read</option>
            <option value="read-write">Read and write</option>
            <option value="permissions">Choose…</option>
          </select>
        </label>
        <label className="api-keys__field">
          <span>Expires</span>
          <select className="field__control field__control--compact" value={expiry} onChange={(event) => setExpiry(event.target.value)}>
            {EXPIRY_OPTIONS.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
        </label>
        <Button type="submit" variant="primary" disabled={!name.trim() || creating} leadingIcon={<Icon name="plus" size={13} />}>
          {creating ? "Creating…" : "Create key"}
        </Button>
      </form>
      {scope === "permissions" ? (
        <div className="api-keys__permissions">
          <PermissionChecklist allowed={MCP_PERMISSIONS.map((p) => p.id)} granted={permissions} onChange={setPermissions} disabled={creating} />
          <p className="api-keys__empty">The key does what you allow here, and never more than you as its owner. It cannot manage users, keys or credentials.</p>
        </div>
      ) : null}

      {keys === null ? (
        <Spinner label="Loading keys" />
      ) : keys.length === 0 ? (
        <p className="api-keys__empty">No API keys yet.</p>
      ) : (
        <div className="api-keys__table-wrap">
          <table className="api-keys__table">
            <thead>
              <tr>
                <th>Name</th>
                <th>Key</th>
                <th>Access</th>
                <th>Created</th>
                <th>Last used</th>
                <th>Expires</th>
                <th>Status</th>
                <th aria-label="Actions" />
              </tr>
            </thead>
            <tbody>
              {keys.map((key) => (
                <tr key={key.id} className={key.status === "active" ? undefined : "api-keys__row--inactive"}>
                  <td>
                    <strong>{key.name}</strong>
                    {key.createdBy ? <span className="api-keys__by">by {key.createdBy}</span> : null}
                  </td>
                  <td>
                    <code>{key.prefix ? `${key.prefix}…` : "—"}</code>
                  </td>
                  <td>
                    {key.permissions
                      ? key.permissions.map((p) => MCP_PERMISSIONS.find((m) => m.id === p)?.label ?? p).join(", ")
                      : key.scope === "read"
                        ? "Read"
                        : "Read and write"}
                  </td>
                  <td>{formatWhen(key.createdAt)}</td>
                  <td>{key.lastUsedAt ? formatWhen(key.lastUsedAt) : "Never"}</td>
                  <td>{key.expiresAt ? formatWhen(key.expiresAt) : "Never"}</td>
                  <td>
                    <span className={`api-keys__status api-keys__status--${key.status}`}>{key.status}</span>
                  </td>
                  <td>
                    {key.status === "active" ? (
                      <Button type="button" variant="ghost" size="sm" onClick={() => setRevoking(key)}>
                        Revoke
                      </Button>
                    ) : null}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <ConfirmDialog
        open={revoking !== null}
        title={`Revoke “${revoking?.name ?? ""}”?`}
        description="Scripts using this key stop working immediately. This cannot be undone; create a new key instead."
        confirmLabel="Revoke key"
        confirmVariant="danger"
        onConfirm={() => revoking && void revoke(revoking)}
        onCancel={() => setRevoking(null)}
      />
    </div>
  );
}
