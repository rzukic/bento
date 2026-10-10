import { useEffect, useRef, useState } from "react";
import type { DashboardWidget } from "../../api/dashboards";
import { ApiClientError } from "../../api/client";
import { getSavedQuery, runQuery, runSavedQuery, type AggregationQuery, type QueryResult, type VariableValue } from "../../api/query";
import { loadCached, peekCached } from "../../api/queryCache";
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
 *
 * Results are kept in the browser (queryCache): a request answered before
 * shows at once — switching M → Q → M or between dashboards does not wait —
 * and is only asked again when it is older than 30 s or data changed since;
 * the refresh token always asks again.
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
  const lastToken = useRef(refreshToken);

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

    const force = refreshToken !== lastToken.current;
    lastToken.current = refreshToken;
    const saved = data.savedQueryId;
    const savedFilter = (query: AggregationQuery | null) => {
      const dateColumn = data.dateColumn !== undefined ? data.dateColumn : (query?.groupBy?.find((g) => g.bucket)?.column ?? null);
      return mergeFilters(rangeFilter(dateColumn, range), globalFilterFor(query?.source, range));
    };
    const requestKey =
      saved !== undefined
        ? JSON.stringify(["saved", saved, data.dateColumn ?? null, range])
        : JSON.stringify(["query", runnableQuery(data.query!, data, range, variables)]);
    type Loaded = { result: QueryResult; query: AggregationQuery | null };
    const load = async (): Promise<Loaded> => {
      if (saved !== undefined) {
        const query = (await getSavedQuery(saved)).query;
        return { result: await runSavedQuery(saved, savedFilter(query)), query };
      }
      return { result: await runQuery(runnableQuery(data.query!, data, range, variables)), query: data.query ?? null };
    };

    const cached = peekCached<Loaded>(requestKey);
    if (cached) {
      setState({ status: "ready", result: cached.value.result, query: cached.value.query, error: null, refreshing: !cached.fresh || force });
      if (cached.fresh && !force) return;
    } else {
      setState((current) => ({ ...current, status: current.result ? current.status : "loading", error: null, refreshing: current.result !== null }));
    }

    let cancelled = false;
    loadCached(requestKey, load, force).then(
      ({ result, query }) => {
        if (!cancelled) setState({ status: "ready", result, query, error: null, refreshing: false });
      },
      (cause) => {
        if (!cancelled) {
          setState((current) => ({
            ...current,
            status: "error",
            refreshing: false,
            error: cause instanceof ApiClientError ? cause.message : "The numbers could not be loaded.",
          }));
        }
      },
    );
    return () => {
      cancelled = true;
    };
    // `key` covers widget data, range and refresh token.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);

  return state;
}
