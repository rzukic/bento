import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { ApiClientError } from "../../api/client";
import { getQueryTable, listSavedQueries, refreshQueryTable, saveQueryAsTable, type QueryTableStatus, type SavedQuery } from "../../api/query";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { Dialog } from "../../components/ui/Dialog";
import { Spinner } from "../../components/ui/Spinner";
import { useSchemaStore } from "../../stores/schemaStore";
import type { ModelTable } from "../../types/api";
import { getCurrentLocale } from "./components/editors/temporalEditing";

function errorText(cause: unknown, fallback: string): string {
  return cause instanceof ApiClientError ? cause.message : fallback;
}

function suggestedName(query: SavedQuery): string {
  const name = query.name.toLowerCase().replace(/[^a-z0-9_]+/g, "_").replace(/^_+|_+$/g, "");
  return /^[a-z]/.test(name) ? name.slice(0, 60) : `q_${name}`.slice(0, 60);
}

/**
 * Step 5.8: a saved query's result as a table — read-only, filled again
 * whenever the data changes, usable in other queries, formulas, dashboards.
 */
export function QueryTableDialog({ onClose }: { onClose: () => void }) {
  const navigate = useNavigate();
  const loadSchema = useSchemaStore((state) => state.load);
  const [queries, setQueries] = useState<SavedQuery[] | null>(null);
  const [chosen, setChosen] = useState<number | null>(null);
  const [name, setName] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    listSavedQueries()
      .then(setQueries)
      .catch((cause) => {
        setQueries([]);
        setError(errorText(cause, "The saved queries could not be loaded."));
      });
  }, []);

  const create = async () => {
    if (chosen === null) return;
    setBusy(true);
    setError(null);
    try {
      const made = await saveQueryAsTable(chosen, name.trim() || undefined);
      await loadSchema({ force: true });
      onClose();
      navigate(`/data/${made.table}`);
    } catch (cause) {
      setError(errorText(cause, "The table could not be made."));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog
      open
      title="New table from a saved query"
      description="The query's result as a table: read-only, filled again whenever the data changes. Use it in other queries, formulas and dashboards like any table."
      onClose={onClose}
      footer={
        <>
          <Button type="button" variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button type="button" variant="primary" disabled={busy || chosen === null} onClick={() => void create()}>
            {busy ? "Making…" : "Make table"}
          </Button>
        </>
      }
    >
      {error ? <Alert tone="error" message={error} /> : null}
      {queries === null ? (
        <Spinner label="Loading saved queries" />
      ) : queries.length === 0 ? (
        <p className="query-table__hint">No saved queries yet. Save one from a dashboard widget (Data → Save as query).</p>
      ) : (
        <div className="query-table__form">
          <label className="field">
            <span className="field__label">Saved query</span>
            <select
              className="field__control"
              value={chosen ?? ""}
              onChange={(event) => {
                const id = Number(event.target.value) || null;
                setChosen(id);
                const query = queries.find((q) => q.id === id);
                if (query) setName(suggestedName(query));
              }}
            >
              <option value="">Choose…</option>
              {queries.map((query) => (
                <option key={query.id} value={query.id}>
                  {query.name} ({query.source})
                </option>
              ))}
            </select>
          </label>
          <label className="field">
            <span className="field__label">Table name</span>
            <input className="field__control" value={name} onChange={(event) => setName(event.target.value)} placeholder="monthly_spending" />
          </label>
        </div>
      )}
    </Dialog>
  );
}

/** On a table made from a saved query: where its rows come from, when they were filled, and filling it now. */
export function QueryTableBanner({ table }: { table: ModelTable }) {
  const [status, setStatus] = useState<QueryTableStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    getQueryTable(table.name).then(setStatus, () => undefined);
  }, [table.name]);

  const refresh = async () => {
    setBusy(true);
    setError(null);
    try {
      setStatus(await refreshQueryTable(table.name));
    } catch (cause) {
      setError(errorText(cause, "The table could not be filled."));
    } finally {
      setBusy(false);
    }
  };

  const when = status?.refreshedAt
    ? new Intl.DateTimeFormat(getCurrentLocale(), { dateStyle: "medium", timeStyle: "short" }).format(new Date(status.refreshedAt))
    : null;
  return (
    <div className="query-table__banner" role="note">
      <Icon name="layers" size={14} />
      <span>
        Made from a saved query — read-only, filled again whenever the data changes.
        {when ? ` ${status!.rows} rows, filled ${when}.` : ""}
        {status?.error ? ` ${status.error}` : ""}
        {error ? ` ${error}` : ""}
      </span>
      <Button type="button" variant="ghost" size="sm" leadingIcon={<Icon name="refresh" size={13} />} disabled={busy} onClick={() => void refresh()}>
        {busy ? "Filling…" : "Refresh now"}
      </Button>
    </div>
  );
}
