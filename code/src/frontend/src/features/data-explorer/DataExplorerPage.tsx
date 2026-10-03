import { useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { EmptyState } from "../../components/ui/EmptyState";
import { Panel } from "../../components/ui/Panel";
import { Spinner } from "../../components/ui/Spinner";
import { useAuthStore } from "../../stores/authStore";
import { useSchemaStore } from "../../stores/schemaStore";
import { TableCard } from "./components/TableCard";
import "./DataExplorer.css";

export function DataExplorerPage() {
  const status = useSchemaStore((state) => state.status);
  const snapshot = useSchemaStore((state) => state.snapshot);
  const error = useSchemaStore((state) => state.error);
  const load = useSchemaStore((state) => state.load);
  const user = useAuthStore((state) => state.user);
  const navigate = useNavigate();

  useEffect(() => {
    if (status === "idle") {
      void load();
    }
  }, [status, load]);

  const loading = status === "loading" || status === "idle";
  const ready = status === "ready" && snapshot !== null;
  const tables = ready ? snapshot.tables : [];
  const relationCount = ready ? snapshot.relations?.length ?? 0 : 0;

  // Loading state
  if (loading) {
    return (
      <div className="explorer-loading" data-testid="explorer-loading">
        <Spinner label="Loading schema" />
      </div>
    );
  }

  // Error state
  if (status === "error") {
    return (
      <Panel>
        <Alert
          tone="error"
          title="Schema unavailable"
          message={
            <>
              <span>{error ?? "Your tables could not be loaded. Try again in a moment."}</span>{" "}
              <Button
                variant="ghost"
                size="sm"
                onClick={() => void load({ force: true })}
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

  // Empty schema — no tables defined yet
  if (tables.length === 0) {
    const canDesignSchema = user === null || user.role === "admin";
    return (
      <Panel>
        <EmptyState
          icon="database"
          title="No tables yet"
          description={
            canDesignSchema
              ? "Create your first table in the schema designer, then come back here to browse your data."
              : "An administrator can create the first table in the schema designer."
          }
          actions={
            canDesignSchema ? (
              <Button
                variant="primary"
                leadingIcon={<Icon name="plus" size={14} strokeWidth={2.25} />}
                onClick={() => navigate("/model")}
              >
                Open schema designer
              </Button>
            ) : undefined
          }
        />
      </Panel>
    );
  }

  return (
    <div className="explorer-landing">
      <div className="explorer-summary">
        <span className="explorer-summary__chip">
          <Icon name="table" size={13} />
          {tables.length} table{tables.length !== 1 ? "s" : ""}
        </span>
        <span className="explorer-summary__chip">
          <Icon name="columns" size={13} />
          {tables.reduce((acc, t) => acc + t.columns.length, 0)} columns
        </span>
        {relationCount > 0 ? (
          <span className="explorer-summary__chip">
            <Icon name="link" size={13} />
            {relationCount} relation{relationCount !== 1 ? "s" : ""}
          </span>
        ) : null}
      </div>

      <ul className="table-list" role="list">
        {tables.map((table) => (
          <li key={table.name}>
            <TableCard table={table} />
          </li>
        ))}
      </ul>
    </div>
  );
}
