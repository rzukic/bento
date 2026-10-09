import { useEffect, useState } from "react";
import { ApiClientError } from "../../api/client";
import { getMcpInfo, listMcpCalls, listMcpConnections, revokeMcpConnection, type McpCall, type McpConnection, type McpInfo } from "../../api/mcp";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { ConfirmDialog } from "../../components/ui/Dialog";
import { Spinner } from "../../components/ui/Spinner";
import { useAuthStore } from "../../stores/authStore";
import { getCurrentLocale } from "../data-explorer/components/editors/temporalEditing";

function formatWhen(value: string | null): string {
  if (!value) return "—";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return new Intl.DateTimeFormat(getCurrentLocale(), { dateStyle: "medium", timeStyle: "short" }).format(date);
}

function message(cause: unknown, fallback: string): string {
  return cause instanceof ApiClientError ? cause.message : fallback;
}

function viaLabel(call: McpCall): string {
  if (call.via === "oauth") return call.credential ?? "App";
  if (call.via === "api_key") return call.credential ?? "API key";
  if (call.via === "open") return "Open mode";
  return "Session";
}

function CopyButton({ text, label }: { text: string; label: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <Button
      type="button"
      variant="ghost"
      size="sm"
      aria-label={label}
      leadingIcon={<Icon name={copied ? "check" : "copy"} size={13} />}
      onClick={() => {
        void navigator.clipboard?.writeText(text).then(
          () => setCopied(true),
          () => setCopied(false)
        );
      }}
    >
      {copied ? "Copied" : "Copy"}
    </Button>
  );
}

/** Connections → Claude: connect Bento to Claude over MCP, see connected apps and every call. */
export function McpPanel() {
  const isAdmin = useAuthStore((state) => state.user?.role === "admin");
  const [info, setInfo] = useState<McpInfo | null>(null);
  const [connections, setConnections] = useState<McpConnection[] | null>(null);
  const [calls, setCalls] = useState<McpCall[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [revoking, setRevoking] = useState<McpConnection | null>(null);

  const reload = () => {
    void listMcpConnections()
      .then(setConnections)
      .catch((cause) => {
        setConnections([]);
        setError(message(cause, "Connected apps could not be loaded."));
      });
    void listMcpCalls()
      .then(setCalls)
      .catch(() => setCalls([]));
  };

  useEffect(() => {
    void getMcpInfo()
      .then(setInfo)
      .catch((cause) => setError(message(cause, "The MCP address could not be loaded.")));
    reload();
  }, []);

  const revoke = async (connection: McpConnection) => {
    setRevoking(null);
    try {
      await revokeMcpConnection(connection.id);
      reload();
    } catch (cause) {
      setError(message(cause, "The app could not be disconnected."));
    }
  };

  const endpoint = info?.endpoint ?? `${window.location.origin}/mcp`;
  const codeCommand = `claude mcp add --transport http bento ${endpoint}`;
  const keyCommand = `claude mcp add --transport http bento ${endpoint} \\\n  --header "Authorization: Bearer <API key>"`;

  return (
    <div className="mcp">
      <p className="api-keys__intro">
        Ask Claude about your data — or let it add a transaction. Bento is an <strong>MCP server</strong>: connect it once and Claude can
        read your tables, run analyses, look at dashboards and, if you allow it, add and change rows. It never changes the schema and
        never deletes anything.
      </p>

      {error ? <Alert tone="error" message={error} /> : null}

      <section className="mcp__address" aria-label="MCP address">
        <span className="mcp__label">Address</span>
        <code>{endpoint}</code>
        <CopyButton text={endpoint} label="Copy the MCP address" />
      </section>

      <div className="mcp__setups">
        <article className="mcp__setup">
          <h3>
            <Icon name="sparkles" size={14} /> Claude app, web and phone
          </h3>
          <ol>
            <li>
              In Claude open <strong>Settings → Connectors</strong> and choose <strong>Add custom connector</strong>.
            </li>
            <li>
              Name it <em>Bento</em> and paste the address above.
            </li>
            <li>
              Click <strong>Connect</strong>, sign in to Bento and pick the access.
            </li>
          </ol>
          <p className="mcp__hint">Claude on the web reaches Bento from the internet, so it needs Bento's public address.</p>
        </article>

        <article className="mcp__setup">
          <h3>
            <Icon name="braces" size={14} /> Claude Code
          </h3>
          <div className="mcp__command">
            <pre>{codeCommand}</pre>
            <CopyButton text={codeCommand} label="Copy the Claude Code command" />
          </div>
          <p className="mcp__hint">
            Then run <code>/mcp</code> in Claude Code and choose <em>Authenticate</em>: your browser opens Bento's sign-in.
          </p>
        </article>

        <article className="mcp__setup">
          <h3>
            <Icon name="link" size={14} /> With an API key
          </h3>
          <div className="mcp__command">
            <pre>{keyCommand}</pre>
            <CopyButton text={keyCommand.replace("\\\n  ", "")} label="Copy the API key command" />
          </div>
          <p className="mcp__hint">For clients without sign-in. A read key only reads; a read-write key may add and change rows.</p>
        </article>
      </div>

      {info && !info.oauth ? (
        <Alert tone="info" message="Sign-in is off on this installation (open mode): MCP clients connect without signing in." />
      ) : null}

      <section className="mcp__section">
        <h3 className="mcp__heading">Connected apps</h3>
        {connections === null ? (
          <Spinner label="Loading connected apps" />
        ) : connections.length === 0 ? (
          <p className="api-keys__empty">No app is connected with {isAdmin ? "an account" : "your account"} yet.</p>
        ) : (
          <div className="api-keys__table-wrap">
            <table className="api-keys__table">
              <thead>
                <tr>
                  <th>App</th>
                  {isAdmin ? <th>User</th> : null}
                  <th>Access</th>
                  <th>Connected</th>
                  <th>Last used</th>
                  <th aria-label="Actions" />
                </tr>
              </thead>
              <tbody>
                {connections.map((connection) => (
                  <tr key={connection.id}>
                    <td>
                      <strong>{connection.clientName}</strong>
                    </td>
                    {isAdmin ? <td>{connection.username ?? "—"}</td> : null}
                    <td>{connection.scope === "read" ? "Read" : "Read and write"}</td>
                    <td>{formatWhen(connection.createdAt)}</td>
                    <td>{connection.lastUsedAt ? formatWhen(connection.lastUsedAt) : "Never"}</td>
                    <td>
                      <Button type="button" variant="ghost" size="sm" onClick={() => setRevoking(connection)}>
                        Disconnect
                      </Button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <section className="mcp__section">
        <div className="mcp__heading-row">
          <h3 className="mcp__heading">Recent calls</h3>
          <Button type="button" variant="ghost" size="sm" leadingIcon={<Icon name="refresh" size={13} />} onClick={reload}>
            Refresh
          </Button>
        </div>
        {calls === null ? (
          <Spinner label="Loading calls" />
        ) : calls.length === 0 ? (
          <p className="api-keys__empty">No calls yet. Ask Claude something about your data.</p>
        ) : (
          <div className="api-keys__table-wrap">
            <table className="api-keys__table mcp__calls">
              <thead>
                <tr>
                  <th>When</th>
                  <th>Tool</th>
                  <th>Via</th>
                  {isAdmin ? <th>User</th> : null}
                  <th>Result</th>
                  <th>Time</th>
                </tr>
              </thead>
              <tbody>
                {calls.map((call) => (
                  <tr key={call.id}>
                    <td>{formatWhen(call.calledAt)}</td>
                    <td>
                      <code>{call.tool}</code>
                      {call.arguments && call.arguments !== "{}" ? (
                        <span className="mcp__args" title={call.arguments}>
                          {call.arguments}
                        </span>
                      ) : null}
                    </td>
                    <td>{viaLabel(call)}</td>
                    {isAdmin ? <td>{call.username ?? "—"}</td> : null}
                    <td>
                      {call.ok ? (
                        <span className="api-keys__status api-keys__status--active">ok</span>
                      ) : (
                        <span className="mcp__error" title={call.error ?? undefined}>
                          {call.error ?? "error"}
                        </span>
                      )}
                    </td>
                    <td>{call.durationMs} ms</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <ConfirmDialog
        open={revoking !== null}
        title={`Disconnect “${revoking?.clientName ?? ""}”?`}
        description="The app loses access immediately. Connect it again from the app to use Bento there."
        confirmLabel="Disconnect"
        confirmVariant="danger"
        onConfirm={() => revoking && void revoke(revoking)}
        onCancel={() => setRevoking(null)}
      />
    </div>
  );
}
