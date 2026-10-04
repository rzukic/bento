import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Link, useLocation, useNavigate, useParams, useSearchParams } from "react-router-dom";
import {
  deleteDashboard,
  getDashboard,
  updateDashboard,
  type Dashboard,
  type DashboardDateRange,
  type DashboardDocument,
  type DashboardWidget,
  type WidgetType,
} from "../../api/dashboards";
import { ApiClientError } from "../../api/client";
import { evaluateVariables } from "../../api/query";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { Spinner } from "../../components/ui/Spinner";
import { useSchemaStore } from "../../stores/schemaStore";
import { DashboardGrid } from "./DashboardGrid";
import { DATE_RANGE_PRESETS, rangeFromSearch, rangeKey, rangeToSearch, resolveRange } from "./dateRange";
import type { LayoutItem } from "./layout";
import { WIDGET_LABELS, dataTables, isFinanceTable, newWidget } from "./starter";
import { variableRequests, type VariableResults } from "./formulaQueries";
import { VariablesEditor } from "./VariablesEditor";
import { LensEditor } from "./lens/LensEditor";
import { WidgetContent, useChartTokens } from "./widgets/WidgetContent";
import "./Dashboards.css";

const REFRESH_OPTIONS = [
  { value: 0, label: "Refresh: off" },
  { value: 30, label: "Every 30 s" },
  { value: 60, label: "Every minute" },
  { value: 300, label: "Every 5 min" },
];

function message(cause: unknown, fallback: string): string {
  if (cause instanceof ApiClientError) return cause.status === 403 ? "Only the owner can change this dashboard." : cause.message;
  return fallback;
}

export function DashboardPage() {
  const { dashboardId } = useParams();
  const id = Number(dashboardId);
  const navigate = useNavigate();
  const location = useLocation();
  const [searchParams, setSearchParams] = useSearchParams();
  const snapshot = useSchemaStore((state) => state.snapshot);
  const loadSchema = useSchemaStore((state) => state.load);
  const tokens = useChartTokens();

  const [dashboard, setDashboard] = useState<Dashboard | null>(null);
  const [draft, setDraft] = useState<{ name: string; isShared: boolean; document: DashboardDocument } | null>(null);
  const [editingWidgetId, setEditingWidgetId] = useState<string | null>(null);
  const [variablesOpen, setVariablesOpen] = useState(false);
  const [variableResults, setVariableResults] = useState<VariableResults | undefined>(undefined);
  const [addOpen, setAddOpen] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [refreshToken, setRefreshToken] = useState(0);
  const lastRefresh = useRef(Date.now());

  useEffect(() => {
    void loadSchema();
  }, [loadSchema]);

  useEffect(() => {
    if (!Number.isFinite(id)) return;
    getDashboard(id)
      .then((loaded) => {
        setDashboard(loaded);
        // A new, empty dashboard opens ready to add widgets.
        if ((location.state as { edit?: boolean } | null)?.edit && loaded.isOwner) {
          setDraft({ name: loaded.name, isShared: loaded.isShared, document: loaded.dashboard });
        }
      })
      .catch((cause) => setError(message(cause, "The dashboard could not be loaded.")));
    // Only on first load of this dashboard.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id]);

  const editing = draft !== null;
  const document = draft?.document ?? dashboard?.dashboard ?? null;
  const range: DashboardDateRange = rangeFromSearch(searchParams) ?? document?.dateRange ?? { preset: "last-12-months" };
  // Resolved once per range key so widgets do not refetch on every render.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const resolved = useMemo(() => resolveRange(range), [rangeKey(range)]);
  const refreshSeconds = document?.refreshSeconds ?? 0;

  const refresh = useCallback(() => {
    lastRefresh.current = Date.now();
    setRefreshToken((n) => n + 1);
  }, []);

  // Interval refresh while the tab is visible, and on coming back to it.
  useEffect(() => {
    const onVisible = () => {
      if (window.document.visibilityState === "visible" && Date.now() - lastRefresh.current > 15_000) refresh();
    };
    window.addEventListener("focus", onVisible);
    window.document.addEventListener("visibilitychange", onVisible);
    const timer =
      refreshSeconds > 0
        ? window.setInterval(() => {
            if (window.document.visibilityState === "visible") refresh();
          }, refreshSeconds * 1000)
        : undefined;
    return () => {
      window.removeEventListener("focus", onVisible);
      window.document.removeEventListener("visibilitychange", onVisible);
      if (timer) window.clearInterval(timer);
    };
  }, [refresh, refreshSeconds]);

  const tables = useMemo(() => snapshot?.tables ?? [], [snapshot]);
  const widgets = useMemo(() => document?.widgets ?? [], [document]);
  const variables = useMemo(() => document?.variables ?? [], [document]);
  const variablesKey = JSON.stringify(variables);

  // Variables are evaluated together (they may use each other), with the
  // range on their date column; while editing, after a pause in typing.
  useEffect(() => {
    const definitions = JSON.parse(variablesKey) as typeof variables;
    if (definitions.length === 0) {
      setVariableResults({});
      return;
    }
    let cancelled = false;
    const timer = window.setTimeout(
      () => {
        evaluateVariables(variableRequests(definitions, resolved))
          .then((results) => {
            if (!cancelled) setVariableResults(Object.fromEntries(results.map((r) => [r.name, r])));
          })
          .catch((cause) => {
            if (cancelled) return;
            const error = cause instanceof ApiClientError ? cause.message : "The variables could not be computed.";
            setVariableResults(Object.fromEntries(definitions.map((d) => [d.name, { name: d.name, type: null, value: null, error }])));
          });
      },
      editing ? 400 : 0,
    );
    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [editing, refreshToken, resolved, variablesKey]);
  const layout: LayoutItem[] = useMemo(() => widgets.map((w) => ({ id: w.id, ...w.position })), [widgets]);
  const editingWidget = widgets.find((w) => w.id === editingWidgetId) ?? null;

  const updateDocument = (change: (doc: DashboardDocument) => DashboardDocument) =>
    setDraft((current) => (current ? { ...current, document: change(current.document) } : current));

  const setWidget = (next: DashboardWidget) =>
    updateDocument((doc) => ({ ...doc, widgets: doc.widgets.map((w) => (w.id === next.id ? next : w)) }));

  const setLayout = (next: LayoutItem[]) =>
    updateDocument((doc) => ({
      ...doc,
      widgets: doc.widgets.map((w) => {
        const item = next.find((i) => i.id === w.id);
        return item ? { ...w, position: { x: item.x, y: item.y, w: item.w, h: item.h } } : w;
      }),
    }));

  const setRange = (next: DashboardDateRange) => {
    setSearchParams(rangeToSearch(next, searchParams), { replace: true });
  };

  const startEditing = () => dashboard && setDraft({ name: dashboard.name, isShared: dashboard.isShared, document: dashboard.dashboard });

  const save = async () => {
    if (!dashboard || !draft) return;
    setSaving(true);
    setError(null);
    try {
      // The range in use becomes the dashboard's default.
      const document = { ...draft.document, dateRange: range };
      const saved = await updateDashboard(dashboard.id, { name: draft.name.trim() || dashboard.name, isShared: draft.isShared, dashboard: document });
      setDashboard(saved);
      setDraft(null);
      setEditingWidgetId(null);
      setSearchParams(new URLSearchParams(), { replace: true });
    } catch (cause) {
      setError(message(cause, "The dashboard could not be saved."));
    } finally {
      setSaving(false);
    }
  };

  const remove = async () => {
    if (!dashboard || !window.confirm(`Delete "${dashboard.name}"? Its widgets go with it; the data stays.`)) return;
    try {
      await deleteDashboard(dashboard.id);
      navigate("/dashboards");
    } catch (cause) {
      setError(message(cause, "The dashboard could not be deleted."));
    }
  };

  const addWidget = (type: WidgetType) => {
    // The table the dashboard already shows, else one with dates and amounts.
    const candidates = dataTables(tables);
    const used = [...widgets].reverse().map((w) => w.data?.query?.source).find(Boolean);
    const table = candidates.find((t) => t.name === used) ?? candidates.find(isFinanceTable) ?? candidates[0];
    const widget = newWidget(type, table, layout);
    updateDocument((doc) => ({ ...doc, widgets: [...doc.widgets, widget] }));
    setAddOpen(false);
    setEditingWidgetId(widget.id);
  };

  if (error && !dashboard) {
    return (
      <div className="dashboard-page">
        <Alert tone="error" message={error} />
      </div>
    );
  }
  if (!dashboard || !document) {
    return (
      <div className="dashboard-page">
        <Spinner label="Loading dashboard" />
      </div>
    );
  }

  const customRange = "from" in range ? range : null;

  return (
    <div className="dashboard-page">
      <header className="dashboard-page__header">
        <Link className="dashboard-page__back" to="/dashboards">
          <Icon name="chevron-left" size={14} />
          Dashboards
        </Link>
        <div className="dashboard-page__heading">
          {editing ? (
            <input
              className="dashboard-page__title-input"
              aria-label="Dashboard name"
              value={draft.name}
              onChange={(event) => setDraft({ ...draft, name: event.target.value })}
            />
          ) : (
            <h1 className="dashboard-page__title">{dashboard.name}</h1>
          )}
          <div className="dashboard-page__controls">
            <select
              className="field__control field__control--compact"
              aria-label="Date range"
              value={customRange ? "custom" : (range as { preset: string }).preset}
              onChange={(event) => {
                const value = event.target.value;
                if (value === "custom") {
                  const r = resolveRange(range);
                  const today = new Date().toISOString().slice(0, 10);
                  setRange({ from: r.from ?? today.slice(0, 8) + "01", to: today });
                } else setRange({ preset: value as never });
              }}
            >
              {DATE_RANGE_PRESETS.map((preset) => (
                <option key={preset.value} value={preset.value}>
                  {preset.label}
                </option>
              ))}
              <option value="custom">Custom…</option>
            </select>
            {customRange ? (
              <span className="dashboard-page__custom">
                <input
                  type="date"
                  className="field__control field__control--compact"
                  aria-label="From"
                  value={customRange.from}
                  onChange={(event) => event.target.value && setRange({ ...customRange, from: event.target.value })}
                />
                <span aria-hidden="true">–</span>
                <input
                  type="date"
                  className="field__control field__control--compact"
                  aria-label="To"
                  value={customRange.to}
                  onChange={(event) => event.target.value && setRange({ ...customRange, to: event.target.value })}
                />
              </span>
            ) : null}
            {editing ? (
              <select
                className="field__control field__control--compact"
                aria-label="Automatic refresh"
                value={refreshSeconds}
                onChange={(event) => updateDocument((doc) => ({ ...doc, refreshSeconds: Number(event.target.value) }))}
              >
                {REFRESH_OPTIONS.map((option) => (
                  <option key={option.value} value={option.value}>
                    {option.label}
                  </option>
                ))}
              </select>
            ) : (
              <Button type="button" variant="ghost" size="sm" onClick={refresh} aria-label="Refresh" title="Refresh">
                <Icon name="refresh" size={14} />
              </Button>
            )}
            {dashboard.isOwner ? (
              editing ? (
                <>
                  <label className="dashboard-page__share">
                    <input type="checkbox" checked={draft.isShared} onChange={(event) => setDraft({ ...draft, isShared: event.target.checked })} />
                    Shared
                  </label>
                  <Button type="button" variant="ghost" size="sm" onClick={() => void remove()} aria-label="Delete dashboard">
                    <Icon name="trash" size={14} />
                  </Button>
                  <Button
                    type="button"
                    variant="ghost"
                    onClick={() => {
                      setDraft(null);
                      setEditingWidgetId(null);
                      setVariablesOpen(false);
                    }}
                  >
                    Cancel
                  </Button>
                  <Button type="button" variant="primary" onClick={() => void save()} disabled={saving}>
                    {saving ? "Saving…" : "Save"}
                  </Button>
                </>
              ) : (
                <Button type="button" variant="ghost" leadingIcon={<Icon name="pencil" size={13} />} onClick={startEditing}>
                  Edit
                </Button>
              )
            ) : null}
          </div>
        </div>
        {refreshSeconds > 0 && !editing ? (
          <p className="dashboard-page__note">Refreshes {REFRESH_OPTIONS.find((o) => o.value === refreshSeconds)?.label.toLowerCase()}.</p>
        ) : null}
      </header>

      {error ? <Alert tone="error" message={error} /> : null}

      {editing ? (
        <div className="dashboard-page__toolbar">
          <div className="dashboard-page__add">
            <Button type="button" variant="ghost" leadingIcon={<Icon name="plus" size={13} />} aria-expanded={addOpen} onClick={() => setAddOpen((open) => !open)}>
              Add widget
            </Button>
            {addOpen ? (
              <div className="dashboard-page__add-menu" role="menu">
                {(Object.keys(WIDGET_LABELS) as WidgetType[]).map((type) => (
                  <button key={type} type="button" role="menuitem" onClick={() => addWidget(type)}>
                    {WIDGET_LABELS[type]}
                  </button>
                ))}
              </div>
            ) : null}
          </div>
          <Button
            type="button"
            variant="ghost"
            leadingIcon={<Icon name="sparkles" size={13} />}
            aria-expanded={variablesOpen}
            onClick={() => {
              setVariablesOpen((open) => !open);
              setEditingWidgetId(null);
            }}
          >
            Variables{variables.length > 0 ? ` (${variables.length})` : ""}
          </Button>
          <span className="dashboard-page__hint">Drag a widget by its title, resize it by its corner.</span>
        </div>
      ) : null}

      {widgets.length === 0 ? (
        <div className="dashboard-page__empty">
          <p>{editing ? "Add a widget to start." : "This dashboard has no widgets yet."}</p>
          {!editing && dashboard.isOwner ? (
            <Button type="button" variant="primary" onClick={startEditing}>
              Add widgets
            </Button>
          ) : null}
        </div>
      ) : (
        <DashboardGrid
          layout={layout}
          editing={editing}
          onLayoutChange={setLayout}
          renderItem={(widgetId) => {
            const widget = widgets.find((w) => w.id === widgetId)!;
            return (
              <article className={editingWidgetId === widget.id ? "widget-frame widget-frame--selected" : "widget-frame"} aria-label={widget.title || widget.type}>
                <header className="widget-frame__header" data-drag-handle={editing ? "" : undefined}>
                  <h2 className="widget-frame__title">{widget.title}</h2>
                  {editing ? (
                    <span className="widget-frame__actions">
                      <Button type="button" variant="ghost" size="sm" aria-label={`Configure ${widget.title || widget.type}`} onClick={() => { setEditingWidgetId(widget.id); setVariablesOpen(false); }}>
                        <Icon name="settings" size={13} />
                      </Button>
                      <Button
                        type="button"
                        variant="ghost"
                        size="sm"
                        aria-label={`Remove ${widget.title || widget.type}`}
                        onClick={() => {
                          updateDocument((doc) => ({ ...doc, widgets: doc.widgets.filter((w) => w.id !== widget.id) }));
                          if (editingWidgetId === widget.id) setEditingWidgetId(null);
                        }}
                      >
                        <Icon name="trash" size={13} />
                      </Button>
                    </span>
                  ) : null}
                </header>
                <div className={`widget-frame__body widget-frame__body--${widget.type}`}>
                  <WidgetContent widget={widget} range={resolved} refreshToken={refreshToken} tokens={tokens} variables={variableResults} />
                </div>
                {editing ? <span className="widget-frame__resize" data-resize-handle="" aria-hidden="true" /> : null}
              </article>
            );
          }}
        />
      )}

      {editing && editingWidget ? (
        <LensEditor
          widget={editingWidget}
          tables={tables}
          snapshot={snapshot}
          variables={variables}
          variableResults={variableResults}
          range={resolved}
          refreshToken={refreshToken}
          tokens={tokens}
          onChange={setWidget}
          onClose={() => setEditingWidgetId(null)}
        />
      ) : null}
      {editing && variablesOpen ? (
        <VariablesEditor
          variables={variables}
          tables={dataTables(tables)}
          snapshot={snapshot}
          results={variableResults}
          onChange={(next) => updateDocument((doc) => ({ ...doc, variables: next.length > 0 ? next : undefined }))}
          onClose={() => setVariablesOpen(false)}
        />
      ) : null}
    </div>
  );
}
