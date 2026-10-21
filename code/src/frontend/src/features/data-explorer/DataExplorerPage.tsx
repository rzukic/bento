import { useEffect, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { EmptyState } from "../../components/ui/EmptyState";
import { Panel } from "../../components/ui/Panel";
import { Spinner } from "../../components/ui/Spinner";
import { useRowCounts } from "../../hooks/useRowCounts";
import { useAuthStore } from "../../stores/authStore";
import { QueryTableDialog } from "./QueryTable";
import { useSchemaStore } from "../../stores/schemaStore";
import { TableRow } from "./components/TableCard";
import "./DataExplorer.css";

export function DataExplorerPage() {
  const status = useSchemaStore((state) => state.status);
  const snapshot = useSchemaStore((state) => state.snapshot);
  const error = useSchemaStore((state) => state.error);
  const load = useSchemaStore((state) => state.load);
  const user = useAuthStore((state) => state.user);
  const [queryTableOpen, setQueryTableOpen] = useState(false);
  const navigate = useNavigate();

  useEffect(() => {
    if (status === "idle") {
      void load();
    }
  }, [status, load]);

  const loading = status === "loading" || status === "idle";
  const ready = status === "ready" && snapshot !== null;
  const tables = ready ? snapshot.tables : [];
  // N:M junction tables are plumbing: listed apart, by what they connect.
  const { dataTables, linkTables } = useMemo(() => {
    const links = new Map<string, [string, string]>();
    for (const relation of snapshot?.relations ?? []) {
      if (relation.type !== "N:M") continue;
      const junction =
        (typeof relation.junctionTable === "string" && relation.junctionTable) ||
        (typeof relation.resolvedJunctionTableName === "string" && relation.resolvedJunctionTableName) ||
        null;
      if (junction && relation.from && relation.to) links.set(junction, [relation.from, relation.to]);
    }
    const label = (name: string) => {
      const table = tables.find((candidate) => candidate.name === name);
      return table?.displayName ?? name;
    };
    return {
      dataTables: tables.filter((table) => !links.has(table.name)),
      linkTables: tables
        .filter((table) => links.has(table.name))
        .map((table) => {
          const [from, to] = links.get(table.name)!;
          return { table, links: [label(from), label(to)] as [string, string] };
        }),
    };
  }, [snapshot?.relations, tables]);
  const tableNames = useMemo(() => tables.map((table) => table.name), [tables]);
  const rowCounts = useRowCounts(tableNames);

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
              <>
                <Button
                  variant="primary"
                  leadingIcon={<Icon name="plus" size={14} strokeWidth={2.25} />}
                  onClick={() => navigate("/model")}
                >
                  Open schema designer
                </Button>
                <Button
                  variant="ghost"
                  leadingIcon={<Icon name="upload" size={14} />}
                  onClick={() => navigate("/data/new/csv")}
                >
                  Create from a CSV file
                </Button>
              </>
            ) : undefined
          }
        />
      </Panel>
    );
  }

  const canCreateTables = user === null || user.role === "admin";

  return (
    <div className="explorer-landing">
      {canCreateTables ? (
        <div className="explorer-landing__actions">
          <Button
            type="button"
            variant="ghost"
            size="sm"
            leadingIcon={<Icon name="upload" size={13} />}
            onClick={() => navigate("/data/new/csv")}
          >
            New table from CSV
          </Button>
          <Button type="button" variant="ghost" size="sm" leadingIcon={<Icon name="layers" size={13} />} onClick={() => setQueryTableOpen(true)}>
            New table from a query
          </Button>
        </div>
      ) : null}
      {queryTableOpen ? <QueryTableDialog onClose={() => setQueryTableOpen(false)} /> : null}
      <section className="table-list-section" aria-label="Tables">
        <ul className="table-list" role="list">
          {dataTables.map((table) => (
            <li key={table.name}>
              <TableRow
                table={table}
                index={tables.indexOf(table)}
                rowCount={rowCounts[table.name]}
              />
            </li>
          ))}
        </ul>
      </section>

      {linkTables.length > 0 ? (
        <section className="table-list-section" aria-labelledby="explorer-link-tables">
          <header className="table-list-section__header">
            <h2 id="explorer-link-tables" className="table-list-section__title">
              Link tables
            </h2>
            <p className="table-list-section__hint">
              Connect records of two tables. Usually edited from the records they link.
            </p>
          </header>
          <ul className="table-list table-list--quiet" role="list">
            {linkTables.map(({ table, links }) => (
              <li key={table.name}>
                <TableRow
                  table={table}
                  index={tables.indexOf(table)}
                  rowCount={rowCounts[table.name]}
                  links={links}
                />
              </li>
            ))}
          </ul>
        </section>
      ) : null}
    </div>
  );
}
