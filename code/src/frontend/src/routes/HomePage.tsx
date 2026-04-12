import { useEffect } from "react";
import { Icon } from "../components/common/Icon";
import { PlaceholderCard } from "../components/common/PlaceholderCard";
import { Alert } from "../components/ui/Alert";
import { Button } from "../components/ui/Button";
import { Panel } from "../components/ui/Panel";
import { useSchemaStore } from "../stores/schemaStore";
import "./HomePage.css";

type Stat = {
  label: string;
  value: string;
  unit?: string;
  delta?: string;
  tone?: "up" | "down" | "flat" | "pending";
};

const activity = [
  { when: "just now", event: "Shell foundation rendered.", kind: "info" as const },
  { when: "today", event: "Frontend workspace scaffolded (TASK-062).", kind: "done" as const },
  { when: "today", event: "SPA shell and route tree wired (TASK-063).", kind: "done" as const },
  { when: "today", event: "Browser auth and route guard landed (TASK-064).", kind: "done" as const },
  { when: "today", event: "Typed API client + schema snapshot store (TASK-065).", kind: "done" as const }
];

const quickActions = [
  { icon: "schema" as const, label: "Define a schema", hint: "Model JSON" },
  { icon: "database" as const, label: "Browse data", hint: "Read-only" },
  { icon: "activity" as const, label: "Inspect health", hint: "GET /health" }
];

export function HomePage() {
  const status = useSchemaStore((state) => state.status);
  const snapshot = useSchemaStore((state) => state.snapshot);
  const error = useSchemaStore((state) => state.error);
  const load = useSchemaStore((state) => state.load);

  useEffect(() => {
    if (status === "idle") {
      void load();
    }
  }, [status, load]);

  const loading = status === "loading" || status === "idle";
  const ready = status === "ready" && snapshot !== null;

  const tableCount = ready ? snapshot.tables.length : null;
  const columnCount = ready
    ? snapshot.tables.reduce((acc, table) => acc + table.columns.length, 0)
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
      label: "API calls · 24h",
      value: "—",
      delta: "metrics wiring pending",
      tone: "flat"
    }
  ];

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
        <Panel className="home-activity" title="Recent activity" actions={<span className="section-label">last 24h</span>}>
          <ol className="home-activity__list">
            {activity.map((row, i) => (
              <li key={i} className="home-activity__row">
                <span
                  className={`home-activity__dot home-activity__dot--${row.kind}`}
                  aria-hidden="true"
                />
                <span className="home-activity__when mono">{row.when}</span>
                <span className="home-activity__event">{row.event}</span>
              </li>
            ))}
          </ol>
        </Panel>

        <Panel className="home-quick" title="Quick actions">
          <ul className="home-quick__list">
            {quickActions.map((a) => (
              <li key={a.label}>
                <Button type="button" variant="ghost" className="home-quick__item">
                  <span className="home-quick__icon">
                    <Icon name={a.icon} size={15} />
                  </span>
                  <span className="home-quick__label">{a.label}</span>
                  <span className="home-quick__hint mono">{a.hint}</span>
                  <Icon name="chevron-right" size={14} />
                </Button>
              </li>
            ))}
          </ul>
        </Panel>
      </section>

      <PlaceholderCard
        title="Shell foundation is live"
        description="The app now has a shared shell, grouped sidebar navigation, breadcrumbs, and a stable route structure for later modules."
        bullets={[
          "Use the sidebar to move between the first Phase 2 route groups.",
          "Breadcrumbs now reflect the active route and dynamic table pages.",
          "Later modules already have placeholder routes so the navigation structure is stable."
        ]}
      />
    </>
  );
}
