import { useEffect, useMemo, useRef, useState } from "react";
import { listPriceSources, refreshPrices, type PriceSource } from "../../api/priceSources";

/** "14:32" today, otherwise the date and time. */
export function quoteTime(value: string | null): string {
  if (!value) return "—";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  const today = new Date().toDateString() === date.toDateString();
  return today
    ? date.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
    : date.toLocaleString([], { dateStyle: "medium", timeStyle: "short" });
}

export type PriceState = {
  /** The newest quote the sources hold, or null before the first one. */
  lastQuoteAt: string | null;
  /** Sources this dashboard reads (by their table). */
  sources: string[];
  fetching: boolean;
  error: string | null;
};

/** The price sources whose table a dashboard reads: its widgets, variables or formulas name the table. */
export function sourcesForDashboard(sources: PriceSource[], dashboard: unknown): PriceSource[] {
  const text = JSON.stringify(dashboard ?? {});
  return sources.filter((s) => s.enabled && s.config.table && new RegExp(`\\b${s.config.table.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}\\b`).test(text));
}

/**
 * Step 4.2: when a dashboard opens or refreshes, prices older than their
 * source's freshness window are fetched first; when any changed, the widgets
 * load again (onChanged). Dashboards that read no price table do nothing.
 */
export function usePriceRefresh(dashboard: unknown, refreshToken: number, enabled: boolean, onChanged: () => void): PriceState | null {
  const [sources, setSources] = useState<PriceSource[] | null>(null);
  const [state, setState] = useState<PriceState | null>(null);
  const onChangedRef = useRef(onChanged);
  onChangedRef.current = onChanged;

  useEffect(() => {
    let cancelled = false;
    listPriceSources()
      .then((list) => !cancelled && setSources(list))
      .catch(() => !cancelled && setSources([]));
    return () => {
      cancelled = true;
    };
  }, []);

  const relevant = useMemo(() => (sources ? sourcesForDashboard(sources, dashboard) : []), [sources, dashboard]);
  const tablesKey = [...new Set(relevant.map((s) => s.config.table))].sort().join(",");

  useEffect(() => {
    if (!enabled || !tablesKey) return;
    let cancelled = false;
    const tables = tablesKey.split(",");
    setState((s) => ({
      lastQuoteAt: s?.lastQuoteAt ?? relevant.map((r) => r.status.lastQuoteAt).filter(Boolean).sort().pop() ?? null,
      sources: relevant.map((r) => r.name),
      fetching: true,
      error: s?.error ?? null,
    }));
    refreshPrices(tables)
      .then((result) => {
        if (cancelled) return;
        const failed = result.sources.find((s) => s.error);
        setState({
          lastQuoteAt: result.lastQuoteAt,
          sources: result.sources.map((s) => s.name),
          fetching: false,
          error: failed ? `${failed.name}: ${failed.error}` : null,
        });
        if (result.changed) onChangedRef.current();
      })
      .catch(() => {
        if (!cancelled) setState((s) => (s ? { ...s, fetching: false, error: "Prices could not be updated." } : s));
      });
    return () => {
      cancelled = true;
    };
    // relevant is derived from tablesKey and sources; refetch per refresh.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [enabled, tablesKey, refreshToken]);

  return tablesKey ? state : null;
}
