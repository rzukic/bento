import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { fetchTableRows } from "../api/data";
import { Icon, type IconName } from "../components/common/Icon";
import { OutlinedNumeral } from "../components/common/OutlinedNumeral";
import { Alert } from "../components/ui/Alert";
import { Button } from "../components/ui/Button";
import { Panel } from "../components/ui/Panel";
import { postSampleData } from "../features/setup/api";
import { useSetupStore } from "../features/setup/setupStore";
import { useAuthStore } from "../stores/authStore";
import { useSchemaStore } from "../stores/schemaStore";
import { ApiClientError } from "../api/client";
import "./HomePage.css";

type QuickAction = {
  icon: IconName;
  label: string;
  hint: string;
  to: string;
  adminOnly?: boolean;
};

const quickActions: QuickAction[] = [
  { icon: "plus", label: "Add a table", hint: "Schema", to: "/model", adminOnly: true },
  { icon: "database", label: "Browse data", hint: "Explorer", to: "/data" },
  { icon: "settings", label: "Settings", hint: "Workspace", to: "/settings" }
];

const MAX_TABLES_ON_HOME = 8;
const TABLE_DOTS = ["#60a5fa", "#2dd4bf", "#818cf8", "#fbbf24", "#f472b6", "#a3e635", "#fb923c", "#38bdf8"];

function greeting(now = new Date()): string {
  const hour = now.getHours();
  if (hour < 5) return "Good night";
  if (hour < 12) return "Good morning";
  if (hour < 18) return "Good afternoon";
  return "Good evening";
}

/** Row counts per table, fetched one lightweight page at a time. */
function useRowCounts(tableNames: string[]): Record<string, number | null> {
  const [counts, setCounts] = useState<Record<string, number | null>>({});
  const key = tableNames.join("\u0000");

  useEffect(() => {
    if (tableNames.length === 0) {
      setCounts({});
      return;
    }
    let cancelled = false;
    setCounts(Object.fromEntries(tableNames.map((name) => [name, null])));
    for (const name of tableNames) {
      fetchTableRows(name, { page: 1, perPage: 1, sort: [], search: "", filters: [] })
        .then((response) => {
          if (!cancelled) setCounts((prev) => ({ ...prev, [name]: response.meta.total }));
        })
        .catch(() => {
          // Counts are decorative; a failed count simply stays unknown.
        });
    }
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);

  return counts;
}

export function HomePage() {
  const status = useSchemaStore((state) => state.status);
  const snapshot = useSchemaStore((state) => state.snapshot);
  const error = useSchemaStore((state) => state.error);
  const load = useSchemaStore((state) => state.load);
  const user = useAuthStore((state) => state.user);
  const setupInfo = useSetupStore((state) => state.info);

  useEffect(() => {
    if (status === "idle") {
      void load();
    }
  }, [status, load]);

  const loading = status === "loading" || status === "idle";
  const ready = status === "ready" && snapshot !== null;
  const isAdmin = user?.role === "admin";

  const tables = ready ? snapshot.tables : [];
  const relations = ready ? snapshot.relations ?? [] : [];
  const columnCount = tables.reduce((acc, table) => acc + table.columns.length, 0);

  const visibleTables = tables.slice(0, MAX_TABLES_ON_HOME);
  const hiddenTableCount = tables.length - visibleTables.length;
  const rowCounts = useRowCounts(visibleTables.map((table) => table.name));
  const knownCounts = Object.values(rowCounts).filter((count): count is number => count !== null);
  const rowsKnown = visibleTables.length > 0 && knownCounts.length === visibleTables.length && hiddenTableCount === 0;
  const totalRows = knownCounts.reduce((sum, count) => sum + count, 0);

  const visibleActions = quickActions.filter((action) => !action.adminOnly || isAdmin);
  const isEmpty = ready && tables.length === 0;

  const stats = [
    {
      label: "Tables",
      value: loading ? "—" : String(tables.length),
      numeral: String(tables.length),
      detail: loading ? "loading…" : status === "error" ? "unavailable" : tables.length === 0 ? "none yet" : "in this workspace"
    },
    {
      label: "Columns",
      value: loading ? "—" : String(columnCount),
      numeral: String(columnCount),
      detail: loading ? "loading…" : columnCount === 0 ? "none yet" : "across all tables"
    },
    {
      label: "Relations",
      value: loading ? "—" : String(relations.length),
      numeral: String(relations.length),
      detail: relations.length === 0 ? "none yet" : "between tables"
    },
    {
      label: "Rows",
      value: loading ? "—" : rowsKnown ? String(totalRows) : visibleTables.length === 0 ? "0" : "…",
      numeral: rowsKnown ? String(totalRows) : "0",
      detail: visibleTables.length === 0 ? "none yet" : rowsKnown ? "across all tables" : "counting…"
    }
  ];

  const subline = loading
    ? "Loading your workspace…"
    : status === "error"
      ? "Your workspace could not be loaded."
      : isEmpty
        ? "Create your first table or load the demo to get started."
        : `${tables.length} ${tables.length === 1 ? "table" : "tables"}${rowsKnown ? ` · ${totalRows} ${totalRows === 1 ? "row" : "rows"}` : ""}`;

  return (
    <>
      <header className="home-hero">
        <div className="home-hero__text">
          <span className="section-label home-hero__eyebrow">Workspace</span>
          <h1 className="home-hero__title display">
            {isEmpty ? "Nothing here yet." : `${greeting()}, ${user?.username ?? "there"}.`}
          </h1>
          <p className="home-hero__subline mono">{subline}</p>
        </div>
        {!isEmpty ? (
          <div className="home-hero__actions">
            {isAdmin ? (
              <Link to="/model" className="btn">
                Design schema
              </Link>
            ) : null}
            <Link to="/data" className="btn btn--primary">
              Browse data <Icon name="arrow-right" size={14} />
            </Link>
          </div>
        ) : null}
      </header>

      <section className="home-grid" aria-label="Workspace summary">
        {stats.map((stat) => (
          <article key={stat.label} className={`stat${isEmpty ? " stat--empty" : ""}`}>
            <header className="stat__label section-label">{stat.label}</header>
            <div className="stat__value display">{stat.value}</div>
            <div className="stat__detail mono">{stat.detail}</div>
            <OutlinedNumeral value={stat.numeral} className="stat__numeral" tone={isEmpty ? "muted" : "aurora"} />
          </article>
        ))}
      </section>

      {status === "error" && error ? (
        <Alert
          tone="error"
          title="Couldn't load your workspace"
          message={
            <>
              <span>{error}</span>{" "}
              <Button variant="ghost" size="sm" className="home-banner__retry" onClick={() => void load({ force: true })}>
                Retry
              </Button>
            </>
          }
        />
      ) : null}

      {isEmpty ? (
        <EmptyWorkspace isAdmin={isAdmin} sampleAvailable={setupInfo?.sample_data_available ?? false} />
      ) : (
        <section className="home-split">
          <Panel
            className="home-tables"
            title="Tables"
            bodyClassName="home-tables__body"
            actions={
              tables.length > 0 ? (
                <Link to="/data" className="section-label home-tables__all">
                  All tables →
                </Link>
              ) : null
            }
          >
            <ol className="home-tables__list">
              {visibleTables.map((table, index) => (
                <li key={table.name} className="home-tables__row">
                  <span
                    className="home-tables__dot"
                    aria-hidden="true"
                    style={{ background: TABLE_DOTS[index % TABLE_DOTS.length] }}
                  />
                  <Link to={`/data/${encodeURIComponent(table.name)}`} className="home-tables__name mono">
                    {table.displayName || table.name}
                  </Link>
                  <span className="home-tables__meta section-label">
                    {table.columns.length} {table.columns.length === 1 ? "col" : "cols"}
                  </span>
                  <span className="home-tables__meta home-tables__meta--strong section-label">
                    {rowCounts[table.name] === null || rowCounts[table.name] === undefined
                      ? "…"
                      : `${rowCounts[table.name]} rows`}
                  </span>
                </li>
              ))}
              {hiddenTableCount > 0 ? (
                <li className="home-tables__row">
                  <span className="home-tables__dot" aria-hidden="true" />
                  <Link to="/data" className="home-tables__name">
                    +{hiddenTableCount} more in the Data Explorer
                  </Link>
                </li>
              ) : null}
            </ol>
          </Panel>

          <Panel className="home-quick" title="Quick actions" bodyClassName="home-quick__body">
            <ul className="home-quick__list">
              {visibleActions.map((action) => (
                <li key={action.label}>
                  <Link to={action.to} className="home-quick__item">
                    <span className="home-quick__icon" aria-hidden="true">
                      <Icon name={action.icon} size={14} />
                    </span>
                    <span className="home-quick__label">{action.label}</span>
                    <span className="home-quick__hint section-label">{action.hint}</span>
                    <Icon name="chevron-right" size={14} />
                  </Link>
                </li>
              ))}
            </ul>
          </Panel>
        </section>
      )}
    </>
  );
}

function EmptyWorkspace({ isAdmin, sampleAvailable }: { isAdmin: boolean; sampleAvailable: boolean }) {
  const reloadSchema = useSchemaStore((state) => state.load);
  const reloadSetup = useSetupStore((state) => state.load);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function loadSample() {
    setLoading(true);
    setError(null);
    try {
      await postSampleData();
      await reloadSetup({ force: true });
      await reloadSchema({ force: true });
    } catch (cause) {
      setError(cause instanceof ApiClientError ? cause.message : "The demo could not be loaded. Check your connection and try again.");
      setLoading(false);
    }
  }

  return (
    <section className="home-empty" aria-label="Get started">
      <div className="home-empty__panel">
        <span className="section-label">Start from scratch</span>
        <h2 className="home-empty__title display">Draw your first table.</h2>
        <p className="home-empty__text">
          {isAdmin
            ? "Open the schema designer, add a table and its columns, review the changes and apply them. The table shows up in the Data Explorer right away."
            : "An administrator sets up tables in the schema designer. Once they exist, you will find them in the Data Explorer."}
        </p>
        {isAdmin ? (
          <div>
            <Link to="/model" className="btn btn--primary">
              Open schema designer <Icon name="arrow-right" size={14} />
            </Link>
          </div>
        ) : null}
      </div>
      <div className="home-empty__panel">
        <span className="section-label">Or look around first</span>
        <h2 className="home-empty__title display">Load the demo workspace.</h2>
        <p className="home-empty__text">
          A small example with customers, orders and products, already linked together, so you can try things out
          before adding your own data.
        </p>
        {error ? (
          <p className="home-empty__error" role="alert">
            {error}
          </p>
        ) : null}
        <div className="home-empty__actions">
          {isAdmin && sampleAvailable ? (
            <Button onClick={() => void loadSample()} disabled={loading} leadingIcon={<Icon name="database" size={14} />}>
              {loading ? "Loading the demo…" : "Load the demo"}
            </Button>
          ) : (
            <span className="home-empty__note mono">
              {isAdmin ? "The demo can only be loaded into an empty workspace." : "Ask an administrator to load it."}
            </span>
          )}
          {isAdmin ? (
            <Link to="/model" className="section-label home-empty__link">
              Import a schema file
            </Link>
          ) : null}
        </div>
      </div>
    </section>
  );
}
