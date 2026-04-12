import { useEffect, useMemo } from "react";
import { Link, useParams } from "react-router-dom";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { EmptyState } from "../../components/ui/EmptyState";
import { Panel } from "../../components/ui/Panel";
import { Spinner } from "../../components/ui/Spinner";
import { useExplorerStore } from "../../stores/explorerStore";
import { useSchemaStore } from "../../stores/schemaStore";
import { ReadOnlyDataGrid } from "./components/ReadOnlyDataGrid";
import "./DataExplorer.css";

export function TableDetailPage() {
  const { tableId } = useParams<{ tableId: string }>();
  const schemaStatus = useSchemaStore((state) => state.status);
  const snapshot = useSchemaStore((state) => state.snapshot);
  const schemaError = useSchemaStore((state) => state.error);
  const loadSchema = useSchemaStore((state) => state.load);

  // Explorer store — view state for this table
  const setActiveTable = useExplorerStore((state) => state.setActiveTable);
  const fetchRows = useExplorerStore((state) => state.fetchRows);
  const view = useExplorerStore((state) =>
    tableId ? state.views[tableId] ?? null : null
  );

  // Ensure schema is loaded
  useEffect(() => {
    if (schemaStatus === "idle") {
      void loadSchema();
    }
  }, [schemaStatus, loadSchema]);

  // Set active table and trigger initial fetch when table is known
  useEffect(() => {
    if (!tableId) return;
    setActiveTable(tableId);
  }, [tableId, setActiveTable]);

  // Fetch rows once the table is active and schema is ready, but only on
  // the first load (idle → loading).
  useEffect(() => {
    if (!tableId || schemaStatus !== "ready") return;
    const v = useExplorerStore.getState().views[tableId];
    if (v && v.status === "idle") {
      void fetchRows();
    }
  }, [tableId, schemaStatus, fetchRows, view?.status]);

  const schemaLoading = schemaStatus === "loading" || schemaStatus === "idle";
  const schemaReady = schemaStatus === "ready" && snapshot !== null;

  const table = useMemo(() => {
    if (!schemaReady) return null;
    return snapshot.tables.find((t) => t.name === tableId) ?? null;
  }, [schemaReady, snapshot, tableId]);

  // Loading schema
  if (schemaLoading) {
    return (
      <div className="explorer-loading" data-testid="explorer-loading">
        <Spinner label="Loading schema" />
      </div>
    );
  }

  // Schema error
  if (schemaStatus === "error") {
    return (
      <Panel>
        <Alert
          tone="error"
          title="Schema unavailable"
          message={
            <>
              <span>{schemaError ?? "Unable to load the schema snapshot."}</span>{" "}
              <Button
                variant="ghost"
                size="sm"
                onClick={() => void loadSchema({ force: true })}
                leadingIcon={<Icon name="refresh" size={13} />}
              >
                Retry
              </Button>
            </>
          }
        />
      </Panel>
    );
  }

  // Table not found in schema
  if (!table) {
    return (
      <Panel>
        <EmptyState
          icon="database"
          title="Table not found"
          description={`No table named "${tableId ?? "unknown"}" exists in the current schema. It may have been renamed or removed.`}
          actions={
            <Button
              variant="default"
              leadingIcon={<Icon name="chevron-left" size={14} />}
              onClick={() => window.history.back()}
            >
              Back to tables
            </Button>
          }
        />
      </Panel>
    );
  }

  // Table found — render detail
  const displayName = table.displayName ?? table.name;
  const pkColumns = table.columns.filter((c) => c.primaryKey);
  const fkColumns = table.columns.filter((c) => c.type === "relation" || c.references);

  const dataStatus = view?.status ?? "idle";
  const dataError = view?.error ?? null;
  const rowCount = view?.meta?.total ?? null;

  return (
    <div className="table-detail">
      <div className="table-detail__header">
        <Link to="/data" className="table-detail__back">
          <Icon name="chevron-left" size={14} />
          All tables
        </Link>
      </div>

      <div className="explorer-summary">
        <span className="explorer-summary__chip">
          <Icon name="table" size={13} />
          {displayName}
        </span>
        <span className="explorer-summary__chip">
          <Icon name="columns" size={13} />
          {table.columns.length} column{table.columns.length !== 1 ? "s" : ""}
        </span>
        {pkColumns.length > 0 ? (
          <span className="explorer-summary__chip">
            PK: {pkColumns.map((c) => c.name).join(", ")}
          </span>
        ) : null}
        {fkColumns.length > 0 ? (
          <span className="explorer-summary__chip">
            <Icon name="link" size={13} />
            {fkColumns.length} reference{fkColumns.length !== 1 ? "s" : ""}
          </span>
        ) : null}
        {rowCount !== null ? (
          <span className="explorer-summary__chip">
            {rowCount} row{rowCount !== 1 ? "s" : ""}
          </span>
        ) : null}
      </div>

      {table.description ? (
        <p style={{ fontSize: "var(--fs-sm)", color: "var(--text-2)", margin: 0 }}>
          {table.description}
        </p>
      ) : null}

      <Panel title="Columns" eyebrow={`${table.columns.length} defined`}>
        <ul className="column-list">
          {table.columns.map((col) => (
            <li key={col.name} className="column-chip">
              <span className="column-chip__name">{col.displayName ?? col.name}</span>
              <span className="column-chip__type">{col.type}</span>
              {col.primaryKey ? <span className="column-chip__badge">PK</span> : null}
              {col.type === "relation" || col.references ? (
                <span className="column-chip__badge column-chip__badge--fk">FK</span>
              ) : null}
              {col.nullable ? (
                <span className="column-chip__type" title="Nullable">?</span>
              ) : null}
            </li>
          ))}
        </ul>
      </Panel>

      {/* Data loading status */}
      {dataStatus === "loading" ? (
        <Panel>
          <div className="explorer-loading" data-testid="data-loading">
            <Spinner label="Loading rows" />
          </div>
        </Panel>
      ) : dataStatus === "error" && dataError ? (
        <Panel>
          <Alert
            tone="error"
            title="Failed to load data"
            message={
              <>
                <span>{dataError}</span>{" "}
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => void fetchRows()}
                  leadingIcon={<Icon name="refresh" size={13} />}
                >
                  Retry
                </Button>
              </>
            }
          />
        </Panel>
      ) : dataStatus === "ready" && view ? (
        <Panel
          title="Data"
          eyebrow={`${view.meta?.total ?? 0} rows · page ${view.query.page} of ${view.meta?.total_pages ?? 1}`}
        >
          {view.rows.length === 0 ? (
            <EmptyState
              icon="database"
              title="No rows"
              description="This table exists in the schema but has no data yet."
            />
          ) : (
            <ReadOnlyDataGrid
              table={table}
              rows={view.rows}
              page={view.query.page}
              perPage={view.query.perPage}
            />
          )}
        </Panel>
      ) : (
        <Panel muted>
          <EmptyState
            icon="database"
            title="Data grid coming soon"
            description="TASK-069 will add the read-only data grid here, backed by the query client from TASK-068."
          />
        </Panel>
      )}
    </div>
  );
}
