import { useCallback, useEffect, useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { ApiClientError } from "../../api/client";
import type { CustomFunction } from "../../api/functions";
import {
  createPriceSource,
  deletePriceSource,
  listPriceSourceRuns,
  listPriceSources,
  runPriceSource,
  suggestSymbols,
  updatePriceSource,
  type PriceSource,
  type PriceSourceConfig,
  type PriceSourceRun,
} from "../../api/priceSources";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { ConfirmDialog } from "../../components/ui/Dialog";
import { Spinner } from "../../components/ui/Spinner";
import { useSchemaStore } from "../../stores/schemaStore";
import { quoteTime } from "../dashboards/priceRefresh";
import type { ModelTable } from "../../types/api";

const NEW = -1;

function message(cause: unknown, fallback: string): string {
  return cause instanceof ApiClientError ? cause.message : fallback;
}


type Form = {
  name: string;
  enabled: boolean;
  config: PriceSourceConfig;
};

/** A first guess from the model: a table with a date, a price and a key column (the wealth template's finance_prices). */
function suggestConfig(tables: ModelTable[], functions: CustomFunction[]): PriceSourceConfig {
  const fetcher = functions.find((f) => f.resultType === "any" && f.arguments.length === 3);
  const lookup = functions.find((f) => f.resultType === "any" && f.arguments.length === 1);
  const prices = tables.find(
    (t) => t.columns.some((c) => c.type === "date") && t.columns.some((c) => c.type === "currency" || c.type === "float") && /price/i.test(t.name),
  );
  const key = prices?.columns.find((c) => /isin|ticker|symbol/i.test(c.name)) ?? prices?.columns.find((c) => c.type === "text");
  const holdings = key
    ? tables.find((t) => t.name !== prices?.name && t.columns.some((c) => c.name === key.name) && /trade|holding|position|depot/i.test(t.name))
    : undefined;
  return {
    function: fetcher?.name ?? "",
    lookupFunction: lookup?.name ?? null,
    table: prices?.name ?? "",
    keyColumn: key?.name ?? "",
    dateColumn: prices?.columns.find((c) => c.type === "date")?.name ?? "",
    priceColumn: prices?.columns.find((c) => /price|close/i.test(c.name) && (c.type === "currency" || c.type === "float"))?.name ?? "",
    keysFrom: holdings && key ? `${holdings.name}.${key.name}` : null,
    symbols: {},
    freshMinutes: 15,
    dailyAt: "22:30",
    timeZone: "Europe/Berlin",
    maxRequestsPerDay: null,
  };
}

/** Price sources (Step 4.2): a function fetches prices; Bento keeps them in your table, current when a dashboard opens. */
export function PriceSourcesPanel({ functions, isAdmin }: { functions: CustomFunction[]; isAdmin: boolean }) {
  const snapshot = useSchemaStore((state) => state.snapshot);
  const loadSchema = useSchemaStore((state) => state.load);
  const [sources, setSources] = useState<PriceSource[] | null>(null);
  const [selected, setSelected] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);

  const reload = useCallback(async () => {
    try {
      const list = await listPriceSources();
      setSources(list);
      setError(null);
      return list;
    } catch (cause) {
      setSources((previous) => previous ?? []);
      setError(message(cause, "Price sources could not be loaded."));
      return null;
    }
  }, []);

  useEffect(() => {
    void loadSchema();
    void reload();
  }, [loadSchema, reload]);

  useEffect(() => {
    if (selected === null && sources && sources.length > 0) setSelected(sources[0].id);
  }, [selected, sources]);

  const tables = snapshot?.tables ?? [];
  const current = sources?.find((s) => s.id === selected) ?? null;

  return (
    <div className="price-sources">
      <p className="price-sources__intro">
        A price source is one of your functions that fetches prices from an API you choose. Bento keeps them as history in your
        table: once a day for closing prices, and again when a dashboard that uses the table opens and its prices are older than a
        few minutes. Formulas such as <code>LOOKUP_ASOF</code> then see current prices.
      </p>
      {error ? <Alert tone="error" message={error} /> : null}

      <div className="functions-layout">
        <aside className="functions-list" aria-label="Price sources">
          <div className="functions-list__head">
            <span className="section-label">Sources</span>
            {isAdmin ? (
              <Button type="button" size="sm" variant="ghost" leadingIcon={<Icon name="plus" size={13} />} onClick={() => setSelected(NEW)}>
                New
              </Button>
            ) : null}
          </div>
          {sources === null ? (
            <Spinner label="Loading price sources" />
          ) : sources.length === 0 ? (
            <p className="functions-list__empty">No price sources yet.</p>
          ) : (
            <ul className="functions-list__items">
              {sources.map((source) => (
                <li key={source.id}>
                  <button
                    type="button"
                    className="functions-list__item"
                    aria-current={source.id === selected ? "true" : undefined}
                    onClick={() => setSelected(source.id)}
                  >
                    <span className="functions-list__name">{source.name}</span>
                    <span className="functions-list__signature mono">
                      {source.config.function} → {source.config.table}
                    </span>
                    <span className="functions-list__meta">
                      {!source.enabled ? "Paused" : `Prices as of ${quoteTime(source.status.lastQuoteAt)}`}
                      {source.status.lastError ? <span className="functions-list__failed"> · last run failed</span> : null}
                    </span>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </aside>

        <section className="functions-main">
          {selected === NEW || current ? (
            <PriceSourceEditor
              key={selected ?? "none"}
              source={selected === NEW ? null : current}
              tables={tables}
              functions={functions}
              readOnly={!isAdmin}
              onSaved={async (id) => {
                await reload();
                setSelected(id);
              }}
              onDeleted={async () => {
                setSelected(null);
                await reload();
              }}
              onChanged={() => void reload()}
            />
          ) : (
            <div className="functions-intro">
              <h2>Live prices, your way</h2>
              <p>
                1. Under <strong>Functions</strong>, create a function from the template <em>Prices from Yahoo Finance</em> (no key
                needed) — or write one for any API, with its key under <strong>Credentials</strong>.
              </p>
              <p>
                2. Here, add a source: the function, your prices table (for the wealth template: <code>finance_prices</code>) and
                where your securities come from (<code>finance_trades.isin</code>). Give each ISIN the provider's symbol, e.g.{" "}
                <code>VWCE.DE</code>; the template <em>Xetra symbols for ISINs</em> suggests them.
              </p>
              <p>3. Fetch the history once; from then on prices follow by themselves.</p>
              {isAdmin ? (
                <Button type="button" variant="primary" leadingIcon={<Icon name="plus" size={14} />} onClick={() => setSelected(NEW)}>
                  Add a price source
                </Button>
              ) : null}
            </div>
          )}
        </section>
      </div>
    </div>
  );
}

type EditorProps = {
  source: PriceSource | null;
  tables: ModelTable[];
  functions: CustomFunction[];
  readOnly: boolean;
  onSaved: (id: number) => Promise<void>;
  onDeleted: () => Promise<void>;
  onChanged: () => void;
};

function PriceSourceEditor({ source, tables, functions, readOnly, onSaved, onDeleted, onChanged }: EditorProps) {
  const initial = useMemo<Form>(
    () =>
      source
        ? { name: source.name, enabled: source.enabled, config: { ...source.config, symbols: { ...(source.config.symbols ?? {}) } } }
        : { name: "", enabled: true, config: suggestConfig(tables, functions) },
    [source, tables, functions],
  );
  const [form, setForm] = useState<Form>(initial);
  const [saving, setSaving] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [runs, setRuns] = useState<PriceSourceRun[] | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);

  const loadRuns = useCallback(() => {
    if (source) void listPriceSourceRuns(source.id).then(setRuns).catch(() => setRuns([]));
  }, [source]);
  useEffect(loadRuns, [loadRuns]);

  const config = form.config;
  const set = (changes: Partial<PriceSourceConfig>) => setForm((f) => ({ ...f, config: { ...f.config, ...changes } }));
  const dirty = JSON.stringify(form) !== JSON.stringify(initial);
  const table = tables.find((t) => t.name === config.table);
  const fetchers = functions.filter((f) => f.resultType === "any" && f.arguments.length === 3);
  const lookups = functions.filter((f) => f.resultType === "any" && f.arguments.length === 1);
  const keyOptions = tables.flatMap((t) =>
    t.columns.filter((c) => ["text", "enum", "uuid"].includes(c.type)).map((c) => `${t.name}.${c.name}`),
  );

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      const cleaned: PriceSourceConfig = {
        ...config,
        symbols: Object.fromEntries(Object.entries(config.symbols ?? {}).filter(([, v]) => v.trim())),
        lookupFunction: config.lookupFunction || null,
        keysFrom: config.keysFrom || null,
        historyFrom: config.historyFrom || null,
      };
      const saved = source
        ? await updatePriceSource(source.id, { name: form.name.trim(), enabled: form.enabled, config: cleaned })
        : await createPriceSource(form.name.trim(), cleaned, form.enabled);
      await onSaved(saved.id);
    } catch (cause) {
      setError(message(cause, "The source could not be saved."));
    } finally {
      setSaving(false);
    }
  };

  const run = async (kind: "latest" | "history") => {
    if (!source) return;
    setBusy(kind);
    setError(null);
    setNotice(null);
    try {
      const result = await runPriceSource(source.id, kind);
      if (result.error) setError(result.error);
      else setNotice(`${result.rowsWritten} ${result.rowsWritten === 1 ? "price" : "prices"} written · ${result.requests} ${result.requests === 1 ? "request" : "requests"}.`);
      loadRuns();
      onChanged();
    } catch (cause) {
      setError(message(cause, "The run failed."));
    } finally {
      setBusy(null);
    }
  };

  const suggest = async () => {
    if (!source) return;
    setBusy("suggest");
    setError(null);
    try {
      const suggestions = await suggestSymbols(source.id);
      if (suggestions.length === 0) setNotice("No new suggestions: every key has a symbol, or the lookup found none.");
      set({ symbols: { ...(config.symbols ?? {}), ...Object.fromEntries(suggestions.map((s) => [s.key, s.symbol])) } });
    } catch (cause) {
      setError(message(cause, "No symbols could be suggested."));
    } finally {
      setBusy(null);
    }
  };

  const remove = async () => {
    setConfirmDelete(false);
    if (!source) return;
    try {
      await deletePriceSource(source.id);
      await onDeleted();
    } catch (cause) {
      setError(message(cause, "The source could not be deleted."));
    }
  };

  const select = (label: string, value: string, options: string[], onChange: (value: string) => void, optional = false) => (
    <label className="function-editor__field">
      <span className="function-editor__label">{label}</span>
      <select className="field__control field__control--compact" value={value} aria-label={label} disabled={readOnly} onChange={(e) => onChange(e.target.value)}>
        {optional || !options.includes(value) ? <option value="">{optional ? "None" : "Choose…"}</option> : null}
        {options.map((option) => (
          <option key={option} value={option}>
            {option}
          </option>
        ))}
      </select>
    </label>
  );

  return (
    <div className="function-editor">
      <header className="function-editor__header">
        <div className="price-source__title">
          <label className="function-editor__field">
            <span className="function-editor__label">Name</span>
            <input
              className="field__control"
              value={form.name}
              placeholder="Yahoo Finance"
              aria-label="Source name"
              disabled={readOnly}
              onChange={(e) => setForm((f) => ({ ...f, name: e.target.value }))}
            />
          </label>
          <label className="price-source__enabled">
            <input type="checkbox" checked={form.enabled} disabled={readOnly} onChange={(e) => setForm((f) => ({ ...f, enabled: e.target.checked }))} />
            Active
          </label>
        </div>
        {source ? (
          <p className={source.status.lastError ? "price-source__status price-source__status--error" : "price-source__status"} role="status">
            {source.status.lastError
              ? `Last run failed: ${source.status.lastError}`
              : `Prices as of ${quoteTime(source.status.lastQuoteAt)} · last fetch ${quoteTime(source.status.lastSuccessAt)}`}
            {` · ${source.status.requestsToday} ${source.status.requestsToday === 1 ? "request" : "requests"} today`}
          </p>
        ) : null}
      </header>

      <section className="function-editor__section" aria-label="Fetching">
        <div className="function-editor__section-head">
          <span className="section-label">Fetching</span>
          <span className="function-editor__hint">
            The function gets (symbols, from, to) and returns [{"{ symbol, date, price }"}]. <Link to="/functions">Functions</Link> has templates.
          </span>
        </div>
        <div className="price-source__grid">
          {select("Function", config.function, fetchers.map((f) => f.name), (v) => set({ function: v }))}
          {select("Symbol lookup", config.lookupFunction ?? "", lookups.map((f) => f.name), (v) => set({ lookupFunction: v || null }), true)}
        </div>
        {fetchers.length === 0 ? (
          <p className="function-editor__hint">No function fits yet: create one from the template "Prices from Yahoo Finance" under Functions.</p>
        ) : null}
      </section>

      <section className="function-editor__section" aria-label="Where prices go">
        <div className="function-editor__section-head">
          <span className="section-label">Where prices go</span>
          <span className="function-editor__hint">One row per security and day; today's row is updated during the day.</span>
        </div>
        <div className="price-source__grid">
          {select("Table", config.table, tables.map((t) => t.name), (v) => {
            const next = tables.find((t) => t.name === v);
            set({
              table: v,
              keyColumn: next?.columns.find((c) => /isin|ticker|symbol/i.test(c.name))?.name ?? "",
              dateColumn: next?.columns.find((c) => c.type === "date")?.name ?? "",
              priceColumn: next?.columns.find((c) => c.type === "currency" || c.type === "float")?.name ?? "",
            });
          })}
          {select("Key column", config.keyColumn, (table?.columns ?? []).filter((c) => ["text", "enum", "uuid"].includes(c.type)).map((c) => c.name), (v) => set({ keyColumn: v }))}
          {select("Date column", config.dateColumn, (table?.columns ?? []).filter((c) => c.type === "date").map((c) => c.name), (v) => set({ dateColumn: v }))}
          {select("Price column", config.priceColumn, (table?.columns ?? []).filter((c) => c.type === "currency" || c.type === "float").map((c) => c.name), (v) => set({ priceColumn: v }))}
          {select("Securities from", config.keysFrom ?? "", keyOptions, (v) => set({ keysFrom: v || null }), true)}
        </div>
      </section>

      <section className="function-editor__section" aria-label="When">
        <div className="function-editor__section-head">
          <span className="section-label">When</span>
        </div>
        <div className="price-source__grid">
          <label className="function-editor__field">
            <span className="function-editor__label">Fresh for (minutes)</span>
            <input className="field__control field__control--compact" type="number" min={1} max={1440} value={config.freshMinutes ?? 15} aria-label="Fresh for (minutes)" disabled={readOnly}
              onChange={(e) => set({ freshMinutes: Number(e.target.value) || 15 })} />
          </label>
          <label className="function-editor__field">
            <span className="function-editor__label">Daily run at</span>
            <input className="field__control field__control--compact" type="time" value={config.dailyAt ?? "22:30"} aria-label="Daily run at" disabled={readOnly}
              onChange={(e) => set({ dailyAt: e.target.value })} />
          </label>
          <label className="function-editor__field">
            <span className="function-editor__label">Time zone</span>
            <input className="field__control field__control--compact" value={config.timeZone ?? "Europe/Berlin"} aria-label="Time zone" disabled={readOnly}
              onChange={(e) => set({ timeZone: e.target.value })} />
          </label>
          <label className="function-editor__field">
            <span className="function-editor__label">History from</span>
            <input className="field__control field__control--compact" type="date" value={config.historyFrom ?? ""} aria-label="History from" disabled={readOnly}
              onChange={(e) => set({ historyFrom: e.target.value || null })} />
          </label>
          <label className="function-editor__field">
            <span className="function-editor__label">Max requests a day</span>
            <input className="field__control field__control--compact" type="number" min={1} value={config.maxRequestsPerDay ?? ""} placeholder="No limit" aria-label="Max requests a day" disabled={readOnly}
              onChange={(e) => set({ maxRequestsPerDay: e.target.value ? Number(e.target.value) : null })} />
          </label>
        </div>
        <p className="function-editor__hint">
          Opening or refreshing a dashboard that reads the table fetches again when the prices are older than the window. The daily
          run fetches closing prices and the history of new securities (empty: five years). A limit protects free API tiers.
        </p>
      </section>

      {source ? (
        <section className="function-editor__section" aria-label="Securities">
          <div className="function-editor__section-head">
            <span className="section-label">Securities</span>
            {!readOnly && config.lookupFunction ? (
              <Button type="button" size="sm" variant="ghost" disabled={busy !== null} onClick={suggest}>
                {busy === "suggest" ? "Looking up…" : "Suggest symbols"}
              </Button>
            ) : null}
          </div>
          {source.keys.length === 0 ? (
            <p className="function-editor__hint">No securities yet: they come from {config.keysFrom ?? "the symbols below"}.</p>
          ) : (
            <table className="price-source__keys">
              <thead>
                <tr>
                  <th>Key</th>
                  <th>Provider symbol</th>
                  <th>Latest</th>
                </tr>
              </thead>
              <tbody>
                {source.keys.map((key) => (
                  <tr key={key.key}>
                    <td className="mono">{key.key}</td>
                    <td>
                      <input
                        className="field__control field__control--compact mono"
                        value={config.symbols?.[key.key] ?? ""}
                        placeholder={key.key}
                        aria-label={`Symbol for ${key.key}`}
                        disabled={readOnly}
                        onChange={(e) => set({ symbols: { ...(config.symbols ?? {}), [key.key]: e.target.value } })}
                      />
                    </td>
                    <td className="price-source__latest">
                      {key.lastDate ? `${key.lastPrice?.toLocaleString(undefined, { maximumFractionDigits: 4 }) ?? "—"} · ${key.lastDate}` : "no prices yet"}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </section>
      ) : null}

      {notice ? <Alert tone="success" message={notice} /> : null}
      {error ? <Alert tone="error" message={error} /> : null}

      {source && runs && runs.length > 0 ? (
        <section className="function-editor__section" aria-label="Runs">
          <div className="function-editor__section-head">
            <span className="section-label">Recent runs</span>
          </div>
          <ol className="price-source__runs">
            {runs.slice(0, 8).map((r) => (
              <li key={r.id} className={r.error ? "price-source__run price-source__run--error" : "price-source__run"}>
                <span className="mono">{r.kind}</span>
                <span>{quoteTime(r.startedAt)}</span>
                <span>
                  {r.error ?? `${r.rowsWritten} written · ${r.requests} ${r.requests === 1 ? "request" : "requests"}`}
                </span>
              </li>
            ))}
          </ol>
        </section>
      ) : null}

      {!readOnly ? (
        <footer className="function-editor__footer">
          {source ? (
            <>
              <Button type="button" variant="ghost" className="function-editor__delete" leadingIcon={<Icon name="trash" size={13} />} onClick={() => setConfirmDelete(true)}>
                Delete
              </Button>
              <Button type="button" variant="ghost" disabled={busy !== null || dirty} onClick={() => run("history")}>
                {busy === "history" ? "Fetching…" : "Fetch history"}
              </Button>
              <Button type="button" variant="ghost" disabled={busy !== null || dirty} onClick={() => run("latest")}>
                {busy === "latest" ? "Fetching…" : "Fetch latest now"}
              </Button>
              {dirty ? <span className="function-editor__hint">Save to fetch with the changes.</span> : null}
            </>
          ) : null}
          <span className="function-editor__spacer" />
          <Button type="button" variant="primary" disabled={saving || !dirty || !form.name.trim() || !config.function || !config.table} onClick={save}>
            {saving ? "Saving…" : source ? "Save" : "Create source"}
          </Button>
        </footer>
      ) : null}

      <ConfirmDialog
        open={confirmDelete}
        title={`Delete ${source?.name ?? "price source"}?`}
        description="Prices already fetched stay in your table; no new ones are fetched."
        confirmLabel="Delete"
        confirmVariant="danger"
        onConfirm={remove}
        onCancel={() => setConfirmDelete(false)}
      />
    </div>
  );
}
