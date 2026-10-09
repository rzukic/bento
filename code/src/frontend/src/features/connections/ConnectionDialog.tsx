import { Fragment, useCallback, useEffect, useState } from "react";
import { ApiClientError } from "../../api/client";
import { listMcpConnectionCalls, updateMcpConnection, type McpCall, type McpConnection, type McpPermission } from "../../api/mcp";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { Dialog } from "../../components/ui/Dialog";
import { Spinner } from "../../components/ui/Spinner";
import { getCurrentLocale } from "../data-explorer/components/editors/temporalEditing";
import { PermissionChecklist } from "./PermissionChecklist";

function formatWhen(value: string | null): string {
  if (!value) return "—";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : new Intl.DateTimeFormat(getCurrentLocale(), { dateStyle: "medium", timeStyle: "medium" }).format(date);
}

function pretty(json: string | null): string {
  if (!json) return "";
  try {
    return JSON.stringify(JSON.parse(json), null, 2);
  } catch {
    return json;
  }
}

function same(a: McpPermission[], b: McpPermission[]): boolean {
  return a.length === b.length && a.every((p) => b.includes(p));
}

/**
 * One connected app, opened from Connections → Claude: change what it may do
 * (applies to its next call) and look through every call it made.
 */
export function ConnectionDialog({
  connection,
  showUser,
  onClose,
  onSaved,
  onDisconnect,
}: {
  connection: McpConnection;
  showUser: boolean;
  onClose: () => void;
  onSaved: (connection: McpConnection) => void;
  onDisconnect: (connection: McpConnection) => void;
}) {
  const [granted, setGranted] = useState<McpPermission[]>(connection.permissions);
  const [saving, setSaving] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [calls, setCalls] = useState<McpCall[] | null>(null);
  const [expanded, setExpanded] = useState<number | null>(null);

  const loadCalls = useCallback(() => {
    setCalls(null);
    listMcpConnectionCalls(connection.id)
      .then(setCalls)
      .catch((cause) => {
        setCalls([]);
        setError(cause instanceof ApiClientError ? cause.message : "The calls could not be loaded.");
      });
  }, [connection.id]);

  useEffect(loadCalls, [loadCalls]);

  const save = async () => {
    setSaving(true);
    setError(null);
    setNotice(null);
    try {
      const updated = await updateMcpConnection(connection.id, granted);
      setGranted(updated.permissions);
      onSaved(updated);
      setNotice("Saved. It applies to the app's next call; new tools show up in Claude once it reconnects (e.g. after restarting Claude Desktop).");
    } catch (cause) {
      setError(cause instanceof ApiClientError ? cause.message : "The permissions could not be saved.");
    } finally {
      setSaving(false);
    }
  };

  const failed = calls?.filter((c) => !c.ok).length ?? 0;

  return (
    <Dialog
      open
      title={connection.clientName}
      description={`Connected ${formatWhen(connection.createdAt)}${showUser && connection.username ? ` by ${connection.username}` : ""} · last used ${connection.lastUsedAt ? formatWhen(connection.lastUsedAt) : "never"}`}
      onClose={onClose}
      panelClassName="mcp-connection"
      bodyClassName="mcp-connection__body"
      footer={
        <>
          <Button type="button" variant="ghost" className="mcp-connection__disconnect" onClick={() => onDisconnect(connection)}>
            Disconnect
          </Button>
          <Button type="button" variant="ghost" onClick={onClose}>
            Close
          </Button>
        </>
      }
    >
      {error ? <Alert tone="error" message={error} /> : null}

      <section className="mcp-connection__section" aria-label="Permissions">
        <PermissionChecklist allowed={connection.allowed} granted={granted} onChange={setGranted} disabled={saving} />
        <div className="mcp-connection__save">
          <Button type="button" variant="primary" size="sm" disabled={saving || same(granted, connection.permissions)} onClick={() => void save()}>
            {saving ? "Saving…" : "Save permissions"}
          </Button>
          {notice ? (
            <span className="mcp__hint" role="status">
              {notice}
            </span>
          ) : null}
        </div>
      </section>

      <section className="mcp-connection__section" aria-label="Calls">
        <div className="mcp__heading-row">
          <h3 className="mcp__heading">
            Calls{calls ? ` (${calls.length}${calls.length >= 100 ? "+" : ""}${failed ? `, ${failed} failed` : ""})` : ""}
          </h3>
          <Button type="button" variant="ghost" size="sm" leadingIcon={<Icon name="refresh" size={13} />} onClick={loadCalls}>
            Refresh
          </Button>
        </div>
        {calls === null ? (
          <Spinner label="Loading calls" />
        ) : calls.length === 0 ? (
          <p className="api-keys__empty">This app has made no calls yet.</p>
        ) : (
          <div className="api-keys__table-wrap">
            <table className="api-keys__table mcp__calls">
              <thead>
                <tr>
                  <th>When</th>
                  <th>Tool</th>
                  <th>Result</th>
                  <th>Time</th>
                </tr>
              </thead>
              <tbody>
                {calls.map((call) => {
                  const open = expanded === call.id;
                  return (
                    <Fragment key={call.id}>
                      <tr className="mcp-connection__call" onClick={() => setExpanded(open ? null : call.id)}>
                        <td>{formatWhen(call.calledAt)}</td>
                        <td>
                          <button
                            type="button"
                            className="mcp-connection__toggle"
                            aria-expanded={open}
                            aria-label={`${open ? "Hide" : "Show"} details of ${call.tool}`}
                            onClick={(event) => {
                              event.stopPropagation();
                              setExpanded(open ? null : call.id);
                            }}
                          >
                            <Icon name={open ? "chevron-down" : "chevron-right"} size={12} />
                          </button>
                          <code>{call.tool}</code>
                          {!open && call.arguments && call.arguments !== "{}" ? <span className="mcp__args">{call.arguments}</span> : null}
                        </td>
                        <td>
                          {call.ok ? (
                            <span className="api-keys__status api-keys__status--active">ok</span>
                          ) : (
                            <span className="mcp__error">{open ? "error" : (call.error ?? "error")}</span>
                          )}
                        </td>
                        <td>{call.durationMs} ms</td>
                      </tr>
                      {open ? (
                        <tr className="mcp-connection__details">
                          <td colSpan={4}>
                            {call.arguments && call.arguments !== "{}" ? (
                              <>
                                <span className="mcp__label">Arguments</span>
                                <pre>{pretty(call.arguments)}</pre>
                              </>
                            ) : (
                              <span className="mcp__label">No arguments</span>
                            )}
                            {call.error ? (
                              <>
                                <span className="mcp__label">Error</span>
                                <pre className="mcp-connection__error">{call.error}</pre>
                              </>
                            ) : null}
                          </td>
                        </tr>
                      ) : null}
                    </Fragment>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </Dialog>
  );
}
