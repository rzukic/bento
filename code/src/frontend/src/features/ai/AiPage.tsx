import { useEffect, useState } from "react";
import { getAiSettings, getAiStatus, listAiUsage, saveAiSettings, type AiSettings, type AiStatus, type AiUsageRow } from "../../api/ai";
import { listDashboardChanges, restoreDashboardVersion, restoreDeletedDashboard, type DashboardVersion } from "../../api/dashboards";
import { Link } from "react-router-dom";
import { ApiClientError } from "../../api/client";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { Spinner } from "../../components/ui/Spinner";
import { useDocumentTitle } from "../../hooks/useDocumentTitle";
import { useAuthStore } from "../../stores/authStore";
import { getCurrentLocale } from "../data-explorer/components/editors/temporalEditing";
import "../connections/Connections.css";
import "./Ai.css";
import { forgetAiStatus } from "./useAiEnabled";

const FEATURES: Record<string, string> = { explain: "Explain", categorise: "Categorise", describe_widget: "Describe a widget", describe_formula: "Describe a formula" };

function formatWhen(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : new Intl.DateTimeFormat(getCurrentLocale(), { dateStyle: "medium", timeStyle: "short" }).format(date);
}

function formatUsd(value: number): string {
  return value < 0.01 && value > 0 ? "< $0.01" : `$${value.toFixed(2)}`;
}

function message(cause: unknown, fallback: string): string {
  return cause instanceof ApiClientError ? cause.message : fallback;
}

/** AI (Step 4.7): setting up Bento's AI, the monthly budget and every call. */
export function AiPage() {
  useDocumentTitle("AI · Bento");
  const isAdmin = useAuthStore((state) => state.user?.role === "admin");
  const [status, setStatus] = useState<AiStatus | null>(null);
  const [settings, setSettings] = useState<AiSettings | null>(null);
  const [usage, setUsage] = useState<AiUsageRow[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);
  const [apiKey, setApiKey] = useState("");
  const [model, setModel] = useState("");
  const [limit, setLimit] = useState("");
  const [busy, setBusy] = useState(false);
  const [changes, setChanges] = useState<DashboardVersion[] | null>(null);
  const [undone, setUndone] = useState<number[]>([]);

  const load = () => {
    void getAiStatus().then(setStatus, (cause) => setError(message(cause, "The AI status could not be loaded.")));
    void listAiUsage().then(setUsage, () => setUsage([]));
    void listDashboardChanges().then(setChanges, () => setChanges([]));
    if (isAdmin)
      void getAiSettings().then(
        (next) => {
          setSettings(next);
          setModel(next.model);
          setLimit(String(next.monthlyLimitUsd));
        },
        (cause) => setError(message(cause, "The AI settings could not be loaded.")),
      );
  };

  useEffect(load, [isAdmin]);

  const save = async (changes: Parameters<typeof saveAiSettings>[0], note: string) => {
    setBusy(true);
    setError(null);
    setSaved(null);
    try {
      const next = await saveAiSettings(changes);
      forgetAiStatus();
      setSettings(next);
      setApiKey("");
      setSaved(note);
      setStatus(await getAiStatus());
    } catch (cause) {
      setError(message(cause, "The settings could not be saved."));
    } finally {
      setBusy(false);
    }
  };

  /** Undo an app's change: back to the state before it (a deleted dashboard comes back). */
  const undo = async (change: DashboardVersion) => {
    setError(null);
    setSaved(null);
    try {
      if (change.action === "delete") await restoreDeletedDashboard(change.id);
      else await restoreDashboardVersion(change.dashboardId, change.id);
      setUndone((current) => [...current, change.id]);
      setSaved(`“${change.name}” is back to how it was before.`);
    } catch (cause) {
      setError(message(cause, "The change could not be undone."));
    }
  };

  const spentShare = settings && settings.monthlyLimitUsd > 0 ? Math.min(1, settings.spentThisMonthUsd / settings.monthlyLimitUsd) : 1;

  return (
    <div className="connections-page ai-page">
      <header>
        <h1 className="ai-page__title">AI</h1>
        <p className="ai-page__lead">
          Bento's AI explains the numbers on your dashboards — the <Icon name="sparkles" size={12} /> button on a widget says what a number is made
          of and what changed — categorises bank bookings when you import them, and builds widgets and formulas from a sentence. It uses Claude by Anthropic with your
          own API key; what it looks at is sent to Anthropic.
        </p>
      </header>

      {error ? <Alert tone="error" message={error} /> : null}
      {saved ? <Alert tone="success" message={saved} /> : null}

      {status && !status.configured && !isAdmin ? <Alert tone="info" message="An admin sets up the AI on this page with an Anthropic API key." /> : null}

      {isAdmin ? (
        settings ? (
          <section className="ai-card" aria-label="Setup">
            <h2>Setup</h2>
            <form
              className="ai-card__row"
              onSubmit={(event) => {
                event.preventDefault();
                if (apiKey.trim()) void save({ apiKey: apiKey.trim() }, "The API key is stored.");
              }}
            >
              <label className="ai-card__field">
                <span>Anthropic API key {settings.configured ? "(stored)" : ""}</span>
                <input
                  className="field__control field__control--compact"
                  type="password"
                  autoComplete="off"
                  value={apiKey}
                  placeholder={settings.configured ? "•••••••• — enter a new key to replace it" : "sk-ant-…"}
                  onChange={(event) => setApiKey(event.target.value)}
                />
              </label>
              <Button type="submit" variant="primary" disabled={busy || !apiKey.trim()}>
                {settings.configured ? "Replace key" : "Save key"}
              </Button>
              {settings.configured ? (
                <Button type="button" variant="ghost" disabled={busy} onClick={() => void save({ apiKey: "" }, "The API key is removed.")}>
                  Remove key
                </Button>
              ) : null}
            </form>
            <p className="ai-card__hint">
              Create a key at console.anthropic.com. Bento keeps it encrypted in the credential vault and never shows it again.
            </p>

            <form
              className="ai-card__row"
              onSubmit={(event) => {
                event.preventDefault();
                const value = Number(limit.replace(",", "."));
                void save({ model, monthlyLimitUsd: Number.isFinite(value) ? value : undefined }, "Saved.");
              }}
            >
              <label className="ai-card__field">
                <span>Model</span>
                <select className="field__control field__control--compact" value={model} onChange={(event) => setModel(event.target.value)}>
                  {settings.models.map((option) => (
                    <option key={option.id} value={option.id}>
                      {option.name} — {option.description}
                    </option>
                  ))}
                </select>
              </label>
              <label className="ai-card__field" style={{ flex: "0 1 10rem" }}>
                <span>Monthly limit (US$)</span>
                <input className="field__control field__control--compact" inputMode="decimal" value={limit} onChange={(event) => setLimit(event.target.value)} />
              </label>
              <Button type="submit" variant="primary" disabled={busy}>
                Save
              </Button>
              <label className="ai-card__toggle">
                <input type="checkbox" checked={settings.enabled} disabled={busy || !settings.configured} onChange={(event) => void save({ enabled: event.target.checked }, event.target.checked ? "AI is on." : "AI is off.")} />
                <span>On</span>
              </label>
            </form>

            <div className="ai-budget">
              <div className="ai-budget__track" aria-hidden="true">
                <div className={`ai-budget__fill${spentShare >= 1 ? " ai-budget__fill--over" : ""}`} style={{ width: `${Math.round(spentShare * 100)}%` }} />
              </div>
              <span className="ai-budget__label">
                {formatUsd(settings.spentThisMonthUsd)} of {formatUsd(settings.monthlyLimitUsd)} used this month
                {spentShare >= 1 ? " — the AI pauses until next month or a higher limit" : ""}
              </span>
            </div>
          </section>
        ) : (
          <Spinner label="Loading the AI settings" />
        )
      ) : status ? (
        <section className="ai-card" aria-label="Status">
          <h2>Status</h2>
          <p className="ai-card__hint">
            {status.enabled ? (status.budgetLeft ? `On, with ${status.model}.` : "This month's budget is used up.") : "Off."}
          </p>
        </section>
      ) : null}

      {changes && changes.length > 0 ? (
        <section className="mcp__section" aria-label="Changes by AI apps">
          <h3 className="mcp__heading">Changes by AI apps</h3>
          <p className="ai-card__hint">
            What Claude, ChatGPT or a script changed in your dashboards. Undo puts a dashboard back to how it was before that change; the state
            you undo stays in the dashboard's history.
          </p>
          <div className="api-keys__table-wrap">
            <table className="api-keys__table">
              <thead>
                <tr>
                  <th>When</th>
                  <th>App</th>
                  <th>Dashboard</th>
                  <th>Change</th>
                  <th aria-label="Actions" />
                </tr>
              </thead>
              <tbody>
                {changes.map((change) => (
                  <tr key={change.id}>
                    <td>{formatWhen(change.createdAt)}</td>
                    <td>{change.via}</td>
                    <td>
                      {change.action === "delete" ? change.name : <Link to={`/dashboards/${change.dashboardId}`}>{change.name}</Link>}
                    </td>
                    <td>{change.action === "delete" ? "Deleted" : change.action === "restore" ? "Restored an earlier state" : "Changed"}</td>
                    <td>
                      {undone.includes(change.id) ? (
                        <span className="api-keys__status api-keys__status--active">undone</span>
                      ) : (
                        <Button type="button" variant="ghost" size="sm" onClick={() => void undo(change)}>
                          Undo
                        </Button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      ) : null}

      <section className="mcp__section">
        <div className="mcp__heading-row">
          <h3 className="mcp__heading">Recent AI calls</h3>
          <Button type="button" variant="ghost" size="sm" leadingIcon={<Icon name="refresh" size={13} />} onClick={load}>
            Refresh
          </Button>
        </div>
        {usage === null ? (
          <Spinner label="Loading calls" />
        ) : usage.length === 0 ? (
          <p className="api-keys__empty">No AI calls yet.</p>
        ) : (
          <div className="api-keys__table-wrap">
            <table className="api-keys__table">
              <thead>
                <tr>
                  <th>When</th>
                  <th>Feature</th>
                  {isAdmin ? <th>User</th> : null}
                  <th>Model</th>
                  <th>Tokens in / out</th>
                  <th>Cost</th>
                  <th>Result</th>
                  <th>Time</th>
                </tr>
              </thead>
              <tbody>
                {usage.map((row) => (
                  <tr key={row.id}>
                    <td>{formatWhen(row.calledAt)}</td>
                    <td>{FEATURES[row.feature] ?? row.feature}</td>
                    {isAdmin ? <td>{row.username ?? "—"}</td> : null}
                    <td>
                      <code>{row.model}</code>
                    </td>
                    <td title={`${row.cacheReadTokens} read from cache`}>
                      {(row.inputTokens + row.cacheReadTokens + row.cacheWriteTokens).toLocaleString(getCurrentLocale())} / {row.outputTokens.toLocaleString(getCurrentLocale())}
                    </td>
                    <td>{formatUsd(row.costUsd)}</td>
                    <td>
                      {row.ok ? (
                        <span className="api-keys__status api-keys__status--active">ok</span>
                      ) : (
                        <span className="mcp__error" title={row.error ?? undefined}>
                          {row.error ?? "error"}
                        </span>
                      )}
                    </td>
                    <td>{(row.durationMs / 1000).toFixed(1)} s</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </div>
  );
}
