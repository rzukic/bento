import { useEffect, useMemo, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { createDashboard, listDashboards, type Dashboard, type DashboardDocument } from "../../api/dashboards";
import { ApiClientError } from "../../api/client";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { Spinner } from "../../components/ui/Spinner";
import { useSchemaStore } from "../../stores/schemaStore";
import { cashFlowDashboard, dataTables, isFinanceTable } from "./starter";
import "./Dashboards.css";

export function DashboardsListPage() {
  const navigate = useNavigate();
  const snapshot = useSchemaStore((state) => state.snapshot);
  const loadSchema = useSchemaStore((state) => state.load);
  const [dashboards, setDashboards] = useState<Dashboard[] | null>(null);
  const [name, setName] = useState("");
  const [creating, setCreating] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    void loadSchema();
    listDashboards()
      .then(setDashboards)
      .catch((cause) => {
        setDashboards([]);
        setError(cause instanceof ApiClientError ? cause.message : "Dashboards could not be loaded.");
      });
  }, [loadSchema]);

  const financeTables = useMemo(() => dataTables(snapshot?.tables ?? []).filter(isFinanceTable), [snapshot]);

  const create = async (
    dashboardName: string,
    document: DashboardDocument = { dateRange: { preset: "last-12-months" }, refreshSeconds: 0, widgets: [] },
  ) => {
    setCreating(true);
    setError(null);
    try {
      const created = await createDashboard(dashboardName, document);
      navigate(`/dashboards/${created.id}`, { state: { edit: document.widgets.length === 0 } });
    } catch (cause) {
      setError(cause instanceof ApiClientError ? cause.message : "The dashboard could not be created.");
    } finally {
      setCreating(false);
    }
  };

  return (
    <div className="dashboards-page">
      <header className="dashboards-page__header">
        <h1 className="dashboards-page__title">Dashboards</h1>
        <p className="dashboards-page__lead">Key figures and charts from your tables, for any date range.</p>
      </header>

      {error ? <Alert tone="error" message={error} /> : null}

      {dashboards === null ? (
        <Spinner label="Loading dashboards" />
      ) : (
        <ul className="dashboards-list" role="list">
          {dashboards.map((dashboard) => (
            <li key={dashboard.id}>
              <Link className="dashboards-list__item" to={`/dashboards/${dashboard.id}`}>
                <Icon name="dashboard" size={16} />
                <span className="dashboards-list__name">{dashboard.name}</span>
                <span className="dashboards-list__meta">
                  {dashboard.dashboard.widgets.length} {dashboard.dashboard.widgets.length === 1 ? "widget" : "widgets"}
                  {dashboard.isShared ? " · shared" : ""}
                  {!dashboard.isOwner ? " · by someone else" : ""}
                </span>
                <Icon name="chevron-right" size={14} />
              </Link>
            </li>
          ))}
        </ul>
      )}

      <section className="dashboards-new" aria-labelledby="dashboards-new-heading">
        <h2 id="dashboards-new-heading" className="dashboards-new__title">New dashboard</h2>
        {financeTables.length > 0 ? (
          <div className="dashboards-new__starters">
            {financeTables.map((table) => (
              <button
                key={table.name}
                type="button"
                className="dashboards-new__starter"
                disabled={creating}
                onClick={() => void create(`Cash flow · ${table.displayName ?? table.name}`, cashFlowDashboard(table))}
              >
                <span className="dashboards-new__starter-title">Monthly cash flow</span>
                <span className="dashboards-new__starter-text">
                  Balance, net per month, spending by category and top payees from {table.displayName ?? table.name}
                </span>
              </button>
            ))}
          </div>
        ) : null}
        <form
          className="dashboards-new__form"
          onSubmit={(event) => {
            event.preventDefault();
            if (name.trim()) void create(name.trim());
          }}
        >
          <input
            className="field__control"
            aria-label="Dashboard name"
            placeholder="Empty dashboard, e.g. Net worth"
            value={name}
            onChange={(event) => setName(event.target.value)}
          />
          <Button type="submit" variant="primary" disabled={!name.trim() || creating} leadingIcon={<Icon name="plus" size={13} />}>
            Create
          </Button>
        </form>
      </section>
    </div>
  );
}
