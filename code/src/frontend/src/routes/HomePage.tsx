import { useEffect } from "react";
import { Link } from "react-router-dom";
import { Icon, type IconName } from "../components/common/Icon";
import { Alert } from "../components/ui/Alert";
import { Button } from "../components/ui/Button";
import { Panel } from "../components/ui/Panel";
import { useAuthStore } from "../stores/authStore";
import { useSchemaStore } from "../stores/schemaStore";
import "./HomePage.css";

type Stat = {
  label: string;
  value: string;
  unit?: string;
  delta?: string;
  tone?: "up" | "down" | "flat" | "pending";
};

type QuickAction = {
  icon: IconName;
  label: string;
  hint: string;
  to: string;
  adminOnly?: boolean;
};

const quickActions: QuickAction[] = [
  { icon: "database", label: "Browse data", hint: "Explorer", to: "/data" },
  { icon: "schema", label: "Design schema", hint: "Admin", to: "/model", adminOnly: true },
  { icon: "settings", label: "Settings", hint: "Workspace", to: "/settings" }
];

const MAX_TABLES_ON_HOME = 8;

export function HomePage() {
  const status = useSchemaStore((state) => state.status);
  const snapshot = useSchemaStore((state) => state.snapshot);
  const error = useSchemaStore((state) => state.error);
  const load = useSchemaStore((state) => state.load);
  const user = useAuthStore((state) => state.user);

  useEffect(() => {
    if (status === "idle") {
      void load();
    }
  }, [status, load]);

  const loading = status === "loading" || status === "idle";
  const ready = status === "ready" && snapshot !== null;
  const isAdmin = user?.role === "admin";

  const tables = ready ? snapshot.tables : [];
  const tableCount = ready ? tables.length : null;
  const columnCount = ready
    ? tables.reduce((acc, table) => acc + table.columns.length, 0)
    : null;
  const relationCount = ready ? snapshot.relations?.length ?? 0 : null;
  const schemaVersion = ready ? snapshot.version : null;

  const stats: Stat[] = [
    {
      label: "Tables",
      value: loading ? "—" : String(tableCount ?? 0),
      delta: loading
        ? "loading schema…"
        : status === "error"
          ? "schema unavailable"
          : (tableCount ?? 0) === 0
            ? "no tables yet"
            : `${columnCount ?? 0} columns`,
      tone: loading ? "pending" : status === "error" ? "down" : "flat"
    },
    {
      label: "Schema version",
      value: loading ? "—" : schemaVersion !== null ? `v${schemaVersion}` : "—",
      delta: loading
        ? "loading…"
        : status === "error"
          ? "unknown"
          : schemaVersion === 0
            ? "not initialised"
            : "current",
      tone: loading ? "pending" : status === "error" ? "down" : "flat"
    },
    {
      label: "Relations",
      value: loading ? "—" : String(relationCount ?? 0),
      delta: loading ? "loading…" : (relationCount ?? 0) === 0 ? "none defined" : "linked",
      tone: loading ? "pending" : "flat"
    },
    {
      label: "Signed in as",
      value: user?.username ?? "—",
      delta: user ? `role · ${user.role}` : "no session",
      tone: "flat"
    }
  ];

  const visibleTables = tables.slice(0, MAX_TABLES_ON_HOME);
  const hiddenTableCount = tables.length - visibleTables.length;
  const visibleActions = quickActions.filter((action) => !action.adminOnly || isAdmin);

  return (
    <>
      <section className="home-grid">
        {stats.map((stat) => (
          <article key={stat.label} className="stat">
            <header className="stat__label">{stat.label}</header>
            <div className="stat__value mono">
              {stat.value}
              {stat.unit ? <span className="stat__unit">{stat.unit}</span> : null}
            </div>
            {stat.delta ? (
              <div className={`stat__delta stat__delta--${stat.tone}`}>{stat.delta}</div>
            ) : null}
          </article>
        ))}
      </section>

      {status === "error" && error ? (
        <Alert
          tone="error"
          title="Schema snapshot unavailable"
          message={
            <>
              <span>Schema snapshot could not be loaded: {error}</span>
              {" "}
              <Button
                variant="ghost"
                size="sm"
                className="home-banner__retry"
                onClick={() => void load({ force: true })}
              >
                Retry
              </Button>
            </>
          }
        />
      ) : null}

      <section className="home-split">
        <Panel
          className="home-activity"
          title="Tables"
          actions={
            tables.length > 0 ? (
              <Link to="/data" className="section-label home-activity__all">
                all tables
              </Link>
            ) : null
          }
        >
          {ready && tables.length === 0 ? (
            <p className="home-activity__empty">
              {isAdmin ? (
                <>
                  No tables yet. <Link to="/model">Open the schema designer</Link> to create the first one.
                </>
              ) : (
                "No tables yet. An administrator has to define the schema first."
              )}
            </p>
          ) : (
            <ol className="home-activity__list">
              {visibleTables.map((table) => (
                <li key={table.name} className="home-activity__row">
                  <span className="home-activity__dot home-activity__dot--done" aria-hidden="true" />
                  <span className="home-activity__when mono">
                    {table.columns.length} {table.columns.length === 1 ? "column" : "columns"}
                  </span>
                  <span className="home-activity__event">
                    <Link to={`/data/${encodeURIComponent(table.name)}`}>
                      {table.displayName || table.name}
                    </Link>
                  </span>
                </li>
              ))}
              {hiddenTableCount > 0 ? (
                <li className="home-activity__row">
                  <span className="home-activity__dot" aria-hidden="true" />
                  <span className="home-activity__when mono">+{hiddenTableCount}</span>
                  <span className="home-activity__event">
                    <Link to="/data">more tables in the Data Explorer</Link>
                  </span>
                </li>
              ) : null}
            </ol>
          )}
        </Panel>

        <Panel className="home-quick" title="Quick actions">
          <ul className="home-quick__list">
            {visibleActions.map((action) => (
              <li key={action.label}>
                <Link to={action.to} className="btn btn--ghost home-quick__item">
                  <span className="home-quick__icon">
                    <Icon name={action.icon} size={15} />
                  </span>
                  <span className="home-quick__label">{action.label}</span>
                  <span className="home-quick__hint mono">{action.hint}</span>
                  <Icon name="chevron-right" size={14} />
                </Link>
              </li>
            ))}
          </ul>
        </Panel>
      </section>
    </>
  );
}
