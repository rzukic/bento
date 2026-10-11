import { useEffect, useState } from "react";
import { ApiClientError } from "../../api/client";
import { listDashboardVersions, restoreDashboardVersion, type Dashboard, type DashboardVersion } from "../../api/dashboards";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { Dialog } from "../../components/ui/Dialog";
import { Spinner } from "../../components/ui/Spinner";
import { getCurrentLocale } from "../data-explorer/components/editors/temporalEditing";

export function formatVersionTime(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : new Intl.DateTimeFormat(getCurrentLocale(), { dateStyle: "medium", timeStyle: "short" }).format(date);
}

/** "Changed by Claude Desktop (ruben)", "Changed by you", … */
export function describeVersion(version: DashboardVersion, me: string | undefined): string {
  const verb = version.action === "delete" ? "Deleted" : version.action === "restore" ? "Restored" : "Changed";
  const who = version.via ?? (version.changedBy && version.changedBy !== me ? version.changedBy : "you");
  return `${verb} by ${who}`;
}

/**
 * A dashboard's history (Step 4.9): the state before every change, by whom and
 * through which app (Claude over MCP, an AI-built widget, the user). Restore
 * brings a state back; the state before the restore is kept, so it can be undone too.
 */
export function HistoryDialog({
  dashboard,
  me,
  onRestored,
  onClose,
}: {
  dashboard: Dashboard;
  me?: string;
  onRestored: (dashboard: Dashboard) => void;
  onClose: () => void;
}) {
  const [versions, setVersions] = useState<DashboardVersion[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<number | null>(null);

  useEffect(() => {
    listDashboardVersions(dashboard.id)
      .then(setVersions)
      .catch((cause) => {
        setVersions([]);
        setError(cause instanceof ApiClientError ? cause.message : "The history could not be loaded.");
      });
  }, [dashboard.id]);

  const restore = async (version: DashboardVersion) => {
    setBusy(version.id);
    setError(null);
    try {
      onRestored(await restoreDashboardVersion(dashboard.id, version.id));
    } catch (cause) {
      setError(cause instanceof ApiClientError ? cause.message : "The dashboard could not be restored.");
      setBusy(null);
    }
  };

  return (
    <Dialog
      open
      title="History"
      description={`Every change to “${dashboard.name}” keeps the state before it. Restore one to go back; that can be undone here as well.`}
      onClose={onClose}
      panelClassName="history-dialog"
      bodyClassName="history-dialog__body"
      footer={
        <Button type="button" variant="ghost" onClick={onClose}>
          Close
        </Button>
      }
    >
      {error ? <Alert tone="error" message={error} /> : null}
      {versions === null ? (
        <Spinner label="Loading the history" />
      ) : versions.length === 0 ? (
        <p className="history-dialog__empty">No changes yet. Each save from now on keeps the state before it.</p>
      ) : (
        <ol className="history-dialog__list">
          {versions.map((version) => (
            <li key={version.id} className="history-dialog__item">
              <div className="history-dialog__text">
                <span className="history-dialog__what">
                  {describeVersion(version, me)}
                  {version.via ? <span className="history-dialog__app">app</span> : null}
                </span>
                <span className="history-dialog__when">
                  {formatVersionTime(version.createdAt)} · before: “{version.name}”, {version.widgets} {version.widgets === 1 ? "widget" : "widgets"}
                </span>
                {version.note ? <span className="history-dialog__note">{version.note}</span> : null}
              </div>
              {dashboard.isOwner ? (
                <Button type="button" variant="ghost" size="sm" disabled={busy !== null} onClick={() => void restore(version)}>
                  {busy === version.id ? "Restoring…" : "Restore"}
                </Button>
              ) : null}
            </li>
          ))}
        </ol>
      )}
    </Dialog>
  );
}
