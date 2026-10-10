import { useEffect, useState } from "react";
import type { DashboardWidget } from "../../api/dashboards";
import { ApiClientError } from "../../api/client";
import { getSavedQuery, runQuery, runSavedQuery, type AggregationQuery, type QueryResult, type VariableValue } from "../../api/query";
import type { ResolvedRange } from "./dateRange";
import { globalFilterFor, mergeFilters } from "./dashboardFilters";
import { runnableQuery } from "./formulaQueries";
import { rangeFilter } from "./widgetData";

const noVariables: Record<string, VariableValue> = {};

export type WidgetResultState = {
  status: "loading" | "ready" | "error";
  /** Kept while a refresh runs, so the widget does not flicker. */
  result: QueryResult | null;
  /** The query that ran (a saved query's definition included) — for running flags and buckets. */
  query: AggregationQuery | null;
  error: string | null;
  /** A new result is on its way while the previous one is still shown (e.g. after switching M → Q). */
  refreshing: boolean;
};

/**
 * Loads a widget's numbers: its own query or a saved one, narrowed to the
 * dashboard's date range on the widget's date column (or computed at the
 * end of each period of the range), with the dashboard's variables for its
 * formulas. Re-runs when the widget's data, the range, the variables or the
 * refresh token change.
 */
export function useWidgetResult(
  widget: DashboardWidget,
  range: ResolvedRange,
  refreshToken: number,
  variables: Record<string, VariableValue> = noVariables,
  /** False while the dashboard's variables are still being computed and the widget's formulas may use them. */
  ready = true,
): WidgetResultState {
  const [state, setState] = useState<WidgetResultState>({ status: "loading", result: null, query: null, error: null, refreshing: false });
  const key = JSON.stringify([widget.type, widget.data, range, refreshToken, variables, ready]);

  useEffect(() => {
    const data = widget.data;
    if (widget.type === "text" || !data || data.variable || data.rows || (!data.query && data.savedQueryId === undefined)) {
      setState({ status: "ready", result: null, query: null, error: null, refreshing: false });
      return;
    }

    // Wait for the variables: without them a formula like "depot" reads as a column and fails.
    if (!ready) {
      setState((current) => ({ ...current, status: current.result ? current.status : "loading", error: null, refreshing: current.result !== null }));
      return;
    }

    let cancelled = false;
    setState((current) => ({ ...current, status: current.result ? current.status : "loading", error: null, refreshing: current.result !== null }));
    void (async () => {
      try {
        let query = data.query ?? null;
        if (data.savedQueryId !== undefined) query = (await getSavedQuery(data.savedQueryId)).query;
        let result: QueryResult;
        if (data.savedQueryId !== undefined) {
          const dateColumn = data.dateColumn !== undefined ? data.dateColumn : (query?.groupBy?.find((g) => g.bucket)?.column ?? null);
          result = await runSavedQuery(data.savedQueryId, mergeFilters(rangeFilter(dateColumn, range), globalFilterFor(query?.source, range)));
        } else {
          result = await runQuery(runnableQuery(query!, data, range, variables));
        }
        if (!cancelled) setState({ status: "ready", result, query, error: null, refreshing: false });
      } catch (cause) {
        if (!cancelled) {
          setState((current) => ({
            ...current,
            status: "error",
            refreshing: false,
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
