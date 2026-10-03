import { useEffect, useState } from "react";
import type { DashboardWidget } from "../../api/dashboards";
import { ApiClientError } from "../../api/client";
import { getSavedQuery, runQuery, runSavedQuery, type AggregationQuery, type QueryResult } from "../../api/query";
import type { ResolvedRange } from "./dateRange";
import { rangeFilter, withFilter } from "./widgetData";

export type WidgetResultState = {
  status: "loading" | "ready" | "error";
  /** Kept while a refresh runs, so the widget does not flicker. */
  result: QueryResult | null;
  /** The query that ran (a saved query's definition included) — for running flags and buckets. */
  query: AggregationQuery | null;
  error: string | null;
};

/**
 * Loads a widget's numbers: its own query or a saved one, narrowed to the
 * dashboard's date range on the widget's date column. Re-runs when the
 * widget's data, the range or the refresh token change.
 */
export function useWidgetResult(widget: DashboardWidget, range: ResolvedRange, refreshToken: number): WidgetResultState {
  const [state, setState] = useState<WidgetResultState>({ status: "loading", result: null, query: null, error: null });
  const key = JSON.stringify([widget.type, widget.data, range, refreshToken]);

  useEffect(() => {
    const data = widget.data;
    if (widget.type === "text" || !data || (!data.query && data.savedQueryId === undefined)) {
      setState({ status: "ready", result: null, query: null, error: null });
      return;
    }

    let cancelled = false;
    setState((current) => ({ ...current, status: current.result ? current.status : "loading", error: null }));
    void (async () => {
      try {
        let query = data.query ?? null;
        if (data.savedQueryId !== undefined) query = (await getSavedQuery(data.savedQueryId)).query;
        const dateColumn = data.dateColumn !== undefined ? data.dateColumn : (query?.groupBy?.find((g) => g.bucket)?.column ?? null);
        const extra = rangeFilter(dateColumn, range);
        const result =
          data.savedQueryId !== undefined ? await runSavedQuery(data.savedQueryId, extra) : await runQuery(withFilter(query!, extra));
        if (!cancelled) setState({ status: "ready", result, query, error: null });
      } catch (cause) {
        if (!cancelled) {
          setState((current) => ({
            ...current,
            status: "error",
            error: cause instanceof ApiClientError ? cause.message : "The numbers could not be loaded.",
          }));
        }
      }
    })();
    return () => {
      cancelled = true;
    };
    // `key` covers widget data, range and refresh token.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);

  return state;
}
