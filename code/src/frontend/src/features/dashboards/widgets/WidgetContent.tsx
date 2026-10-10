import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import type { DashboardAnnotation, DashboardWidget } from "../../../api/dashboards";
import type { AggregationQuery, QueryResult, VariableValue } from "../../../api/query";
import { Icon } from "../../../components/common/Icon";
import { formatKindForType, formatNumber, toNumber, type NumberFormatKind } from "../../../lib/format";
import { useAppStore } from "../../../stores/appStore";
import { EChart, type ChartClick } from "../charts/EChart";
import { buildChartOption, buildSankeyOption, formatBucketLabel, formatBucketTitle, readChartTokens, type ChartTokens } from "../charts/chartOptions";
import { drillTarget, signFilter, type DrillTarget, type GroupPick } from "../drill";
import { sankeyMode, toSankeyModel } from "../sankey";
import { rangeKey, resolveRange, type ResolvedRange } from "../dateRange";
import { runnableQuery, variableValues, type VariableResults } from "../formulaQueries";
import { kpiExtraQueries, useQueryRun, useRows } from "../useExtraQueries";
import { useWidgetResult } from "../useWidgetResult";
import { labelOf, otherKeyOf, toChartModel, toKpiModel } from "../widgetData";
import { applyMeasureShows, placeAnnotations, withMeasureColors, resolveReferenceLines, shiftedRange, withShifted, withTotal } from "../chartTransforms";
import { renderCellValue } from "../../data-explorer/components/cellValue";
import { valueTypeOf } from "../../../lib/columns";
import { useSchemaStore } from "../../../stores/schemaStore";
import { BudgetWidget } from "./BudgetWidget";
import { INTERVAL_LABELS } from "../interval";

/** Re-reads the chart colors whenever the light/dark theme changes. */
export function useChartTokens(): ChartTokens {
  const themeMode = useAppStore((state) => state.themeMode);
  const [systemFlip, setSystemFlip] = useState(0);
  useEffect(() => {
    const media = typeof window !== "undefined" ? window.matchMedia?.("(prefers-color-scheme: light)") : undefined;
    if (!media) return;
    const onChange = () => setSystemFlip((n) => n + 1);
    media.addEventListener?.("change", onChange);
    return () => media.removeEventListener?.("change", onChange);
  }, []);
  // themeMode and systemFlip only trigger the re-read.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  return useMemo(() => readChartTokens(), [themeMode, systemFlip]);
}

function overrideFormat(widget: DashboardWidget): NumberFormatKind | undefined {
  const format = widget.display?.format;
  return format && format !== "auto" ? format : undefined;
}

/**
 * A table cell's number as its column is set to show it (display.columns):
 * its own format, a + for positive values, green or red by sign.
 */
function numberCell(widget: DashboardWidget, column: string, value: number, fallback: NumberFormatKind): { text: string; className: string } {
  const own = widget.display?.columns?.[column];
  const kind = own?.format && own.format !== "auto" ? own.format : fallback;
  const sign = own?.colorBySign && value !== 0 ? (value > 0 ? " widget-table__number--positive" : " widget-table__number--negative") : "";
  return { text: formatNumber(value, kind, undefined, own?.signed ?? false), className: `widget-table__number${sign}` };
}

const LOADING_DELAY_MS = 150;

/**
 * "Column 'depot' not found …" for a formula naming a dashboard variable that
 * could not be computed: say what is wrong with the variable instead.
 */
export function variableErrorFor(error: string | null, variables: VariableResults | undefined): string | null {
  const name = error?.match(/Column '([^']+)' not found/)?.[1];
  const variable = name ? variables?.[name] : undefined;
  return variable?.error ? `The variable ${name} could not be computed: ${variable.error}` : null;
}

export function WidgetContent({
  widget,
  range: dashboardRange,
  refreshToken,
  tokens,
  variables,
  annotations,
  onDrill,
  onResult,
  onLoading,
  variablesPending = false,
}: {
  widget: DashboardWidget;
  range: ResolvedRange;
  refreshToken: number;
  tokens: ChartTokens;
  /** The dashboard's variables, for KPI cards showing one and for widget formulas. */
  variables?: VariableResults;
  /** The dashboard's events, drawn on charts by date. */
  annotations?: DashboardAnnotation[];
  /** Opens the rows behind a click (view mode only). */
  onDrill?: (target: DrillTarget) => void;
  /** The rows the widget shows, for "Explain this" (Step 4.7). */
  onResult?: (result: QueryResult | null) => void;
  /** Whether numbers are on their way — first load or a refresh behind the shown ones (for the interval switch's spinner). */
  onLoading?: (loading: boolean) => void;
  /** The dashboard is still computing its variables; widgets with formulas wait for them. */
  variablesPending?: boolean;
}) {
  const values = useMemo(() => variableValues(variables), [variables]);
  // A widget with its own range (a forecast to 2045) ignores the dashboard's; the dashboard's filters still apply.
  const ownRange = widget.data?.range;
  const ownRangeKey = ownRange ? rangeKey(ownRange) : null;
  const range = useMemo(
    () => (ownRange ? { ...resolveRange(ownRange), filters: dashboardRange.filters } : dashboardRange),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [ownRangeKey, dashboardRange],
  );
  // Step 4.5: a donut with "drill into" shows the clicked slice's next level inside itself.
  const [level, setLevel] = useState<{ value: unknown; label: string } | null>(null);
  const shown = useMemo(() => (level ? drilledWidget(widget, level.value) : widget), [widget, level]);
  // Formulas may use the dashboard's variables: while those are computed, wait for them.
  const usesFormulas = (shown.data?.query?.measures ?? []).some((m) => m.formula);
  const state = useWidgetResult(shown, range, refreshToken, values, !(variablesPending && usesFormulas));
  useEffect(() => {
    onResult?.(state.result);
  }, [onResult, state.result]);
  const loading = state.status === "loading" || state.refreshing;
  useEffect(() => {
    if (!loading) {
      onLoading?.(false);
      return;
    }
    // Only a load that takes a moment shows: a quick one would just flicker.
    const timer = window.setTimeout(() => onLoading?.(true), LOADING_DELAY_MS);
    return () => window.clearTimeout(timer);
  }, [onLoading, loading]);

  if (widget.type === "text") return <TextWidget text={widget.display?.text ?? ""} />;
  if (widget.type === "kpi" && widget.data?.variable) {
    return <VariableKpi widget={widget} name={widget.data.variable} variables={variables} />;
  }
  if (widget.type === "budget") {
    return widget.data?.budget ? (
      <BudgetWidget config={widget.data.budget} range={range} refreshToken={refreshToken} onDrill={onDrill} />
    ) : (
      <p className="widget-note">Choose the spending table and the limits in the widget's settings.</p>
    );
  }
  if (widget.type === "table" && widget.data?.rows) {
    return <RowsTable widget={widget} range={range} refreshToken={refreshToken} />;
  }
  if (!widget.data?.query && widget.data?.savedQueryId === undefined) {
    return <p className="widget-note">Choose what this widget shows.</p>;
  }
  if (state.status === "error") {
    return (
      <p className="widget-note widget-note--error" role="alert">
        <Icon name="activity" size={13} /> {variableErrorFor(state.error, variables) ?? state.error}
      </p>
    );
  }
  if (!state.result) return <div className="widget-loading" aria-label="Loading" />;

  switch (widget.type) {
    case "kpi":
      return (
        <KpiWidget
          result={state.result}
          widget={widget}
          query={state.query}
          range={range}
          refreshToken={refreshToken}
          values={values}
          variables={variables}
          onDrill={onDrill}
        />
      );
    case "table":
      return <TableWidget result={state.result} query={state.query} widget={widget} range={range} refreshToken={refreshToken} values={values} />;
    case "sankey":
      return <SankeyWidget widget={widget} result={state.result} query={state.query} range={range} tokens={tokens} onDrill={onDrill} />;
    default:
      if (widget.type === "donut" && widget.display?.drill) {
        return (
          <div className="widget-drill">
            <nav className="widget-drill__path" aria-label="Level">
              {level ? (
                <>
                  <button type="button" className="widget-drill__up" onClick={() => setLevel(null)}>
                    All
                  </button>
                  <span aria-hidden="true">›</span>
                  <span aria-current="true">{level.label}</span>
                </>
              ) : (
                <span className="widget-drill__hint">Click a slice to see what is in it</span>
              )}
            </nav>
            <ChartWidget
              onDrill={onDrill}
              onDrillDown={level ? undefined : (value, label) => setLevel({ value, label })}
              widget={shown}
              result={state.result}
              query={state.query}
              range={range}
              tokens={tokens}
              refreshToken={refreshToken}
              values={values}
              annotations={annotations}
              stale={state.refreshing && state.forRange !== JSON.stringify([range.from, range.to])}
            />
          </div>
        );
      }
      return (
        <ChartWidget
          onDrill={onDrill}
          widget={widget}
          result={state.result}
          query={state.query}
          range={range}
          tokens={tokens}
          refreshToken={refreshToken}
          values={values}
          annotations={annotations}
          stale={state.refreshing && state.forRange !== JSON.stringify([range.from, range.to])}
        />
      );
  }
}

function ChartWidget({
  widget,
  result,
  query,
  range,
  tokens,
  refreshToken,
  values,
  annotations,
  onDrill,
  onDrillDown,
  stale = false,
}: {
  widget: DashboardWidget;
  result: QueryResult;
  query: Parameters<typeof toChartModel>[1] | null;
  range: ResolvedRange;
  tokens: ChartTokens;
  refreshToken: number;
  values: Record<string, VariableValue>;
  annotations?: DashboardAnnotation[];
  onDrill?: (target: DrillTarget) => void;
  /** Donuts with "drill into": a slice opens its next level instead of its rows. */
  onDrillDown?: (value: unknown, label: string) => void;
  /** A new result is loading (1y → 3y): periods the shown one does not cover stay empty, not 0. */
  stale?: boolean;
}) {
  const display = widget.display;
  const stacked = display?.stacked ?? false;
  const hasBreakdown = (query?.groupBy ?? []).length > 1;

  // The same measures over an earlier period, for a dashed comparison line.
  const shift = widget.data?.timeShift;
  const earlier = useMemo(() => (shift ? shiftedRange(range, shift) : null), [shift, range]);
  const shiftedQuery = useMemo(
    () => (earlier && widget.data?.query && !hasBreakdown ? runnableQuery(widget.data.query, widget.data, earlier, values) : null),
    [earlier, widget.data, hasBreakdown, values],
  );
  const shiftedResult = useQueryRun(shiftedQuery, refreshToken);

  const model = useMemo(() => {
    const format = overrideFormat(widget);
    const measures = query?.measures ?? [];
    const empty = { emptyPeriods: display?.emptyPeriods, stale };
    let next = withMeasureColors(applyMeasureShows(toChartModel(result, query ?? undefined, range, format, undefined, empty), measures, hasBreakdown), measures, hasBreakdown);
    if (shift && earlier && shiftedResult) {
      next = withShifted(next, applyMeasureShows(toChartModel(shiftedResult, query ?? undefined, earlier, format, undefined, empty), measures, false), shift);
    }
    if (stacked && display?.totalLine) next = withTotal(next);
    return next;
  }, [result, query, range, widget, shift, earlier, shiftedResult, stacked, hasBreakdown, display?.totalLine, display?.emptyPeriods, stale]);

  // The series under the pointer, read by the tooltip when it renders.
  const hovered = useRef<number | null>(null);
  const referenceLines = useMemo(() => resolveReferenceLines(display?.referenceLines, values), [display?.referenceLines, values]);
  const showAnnotations = display?.annotations !== false;
  const placed = useMemo(
    () => placeAnnotations(showAnnotations ? annotations : undefined, model.categories, model.bucket),
    [showAnnotations, annotations, model.categories, model.bucket],
  );
  const option = useMemo(
    () =>
      buildChartOption(widget.type, model, tokens, {
        stacked,
        hovered: () => hovered.current,
        referenceLines,
        colorBySign: display?.colorBySign ?? false,
        annotations: placed,
        yMin: display?.yAxis?.min,
        connectNulls: display?.emptyPeriods === "connect",
      }),
    [widget.type, stacked, model, tokens, referenceLines, display?.colorBySign, placed, display?.yAxis?.min, display?.emptyPeriods],
  );
  const onHoverSeries = useCallback((index: number | null) => {
    hovered.current = index;
  }, []);
  // A bar, point or slice opens its rows: the period or category, and the breakdown's value.
  const onItemClick = useCallback(
    (click: ChartClick) => {
      if (click.componentType !== "series") return;
      if (widget.type === "donut" && onDrillDown) {
        const pick = model.categoryPicks?.[model.categories.indexOf(String(click.name))];
        if (pick && !pick.other) return onDrillDown(pick.value, String(click.name));
      }
      if (!onDrill) return;
      let picks: (GroupPick | null | undefined)[];
      if (widget.type === "donut") {
        picks = [model.categoryPicks?.[model.categories.indexOf(String(click.name))]];
      } else {
        const series = model.series[click.seriesIndex ?? -1];
        if (!series || series.kind === "shifted") return;
        picks = [model.categoryPicks ? model.categoryPicks[click.dataIndex ?? -1] : undefined, series.kind ? undefined : series.pick];
      }
      if (picks.some((p) => p === null)) return;
      const target = drillTarget(query, widget.data, range, picks.filter((p): p is GroupPick => Boolean(p)));
      if (target) onDrill(target);
    },
    [onDrill, onDrillDown, widget.type, widget.data, model, query, range],
  );
  if (model.categories.length === 0) return <p className="widget-note">No data in this range.</p>;
  return <EChart option={option} label={widget.title || "Chart"} onHoverSeries={onHoverSeries} onItemClick={onDrill || onDrillDown ? onItemClick : undefined} />;
}

function SankeyWidget({
  widget,
  result,
  query,
  range,
  tokens,
  onDrill,
}: {
  widget: DashboardWidget;
  result: QueryResult;
  query: Parameters<typeof toChartModel>[1] | null;
  range: ResolvedRange;
  tokens: ChartTokens;
  onDrill?: (target: DrillTarget) => void;
}) {
  const groupCount = result.meta.columns.filter((c) => c.role === "group").length;
  const mode = sankeyMode(widget.display?.sankey, groupCount);
  const model = useMemo(() => toSankeyModel(result, query ?? undefined, mode, overrideFormat(widget) ?? formatKindForType(result.meta.columns.find((c) => c.role === "measure")?.type)), [result, query, mode, widget]);
  const shares = widget.display?.shares ?? false;
  const option = useMemo(() => buildSankeyOption(model, tokens, { shares }), [model, tokens, shares]);
  // A node or a flow opens its rows; cash flow sides keep their sign (income above 0, expenses below).
  const onItemClick = useCallback(
    (click: ChartClick) => {
      if (!onDrill) return;
      const data = click.data as { source?: string; target?: string } | undefined;
      const item =
        click.dataType === "edge"
          ? model.links.find((l) => l.source === data?.source && l.target === data?.target)
          : model.nodes.find((n) => n.id === click.name);
      if (!item?.picks) return;
      const target = drillTarget(query, widget.data, range, item.picks, signFilter(query, item.sign));
      if (target) onDrill(target);
    },
    [onDrill, model, query, widget.data, range],
  );
  if (model.links.length === 0) {
    return (
      <p className="widget-note">
        {mode === "levels" && groupCount < 2 ? "Add a second level, or show it as income → expenses under Style." : "No flows in this range."}
      </p>
    );
  }
  return <EChart option={option} label={widget.title || "Sankey"} onItemClick={onDrill ? onItemClick : undefined} />;
}

function KpiWidget({
  result,
  widget,
  query,
  range,
  refreshToken,
  values,
  variables,
  onDrill,
}: {
  result: QueryResult;
  widget: DashboardWidget;
  query: Parameters<typeof toChartModel>[1] | null;
  range: ResolvedRange;
  refreshToken: number;
  values: Record<string, VariableValue>;
  variables?: VariableResults;
  onDrill?: (target: DrillTarget) => void;
}) {
  const extra = useMemo(() => kpiExtraQueries(widget, range, values), [widget, range, values]);
  const previousResult = useQueryRun(extra.previous, refreshToken);
  const trendResult = useQueryRun(extra.trend, refreshToken);
  const model = toKpiModel(result, overrideFormat(widget));
  if (model.value === null) return <p className="widget-note">No data in this range.</p>;

  const measures = result.meta.columns.filter((c) => c.role === "measure");
  const bucket = widget.data?.query?.groupBy?.find((g) => g.bucket)?.bucket;
  const wholeRange = !bucket;
  const previous = wholeRange
    ? previousResult && measures[0]
      ? toNumber(previousResult.data[0]?.[measures[0].name])
      : null
    : model.previous;
  const compare = widget.display?.compare !== false && previous !== null;
  const delta = compare ? model.value - previous! : 0;
  const percent = compare && previous !== 0 ? delta / Math.abs(previous!) : null;
  const good = (widget.display?.higherIsBetter ?? true) ? delta >= 0 : delta <= 0;

  // Secondary value: the second measure of the same (latest) row.
  const secondary = measures[1];
  const lastRow = toKpiRow(result);
  const secondaryValue = secondary && lastRow ? toNumber(lastRow[secondary.name]) : null;

  // Trend: the value per period — from the rows themselves (latest period) or an extra query.
  const trendSource = wholeRange ? trendResult : result;
  const trendValues = widget.display?.trend && trendSource && measures[0] ? sortedValues(trendSource, measures[0].name) : [];

  // Goal: a number or a variable's value.
  const goalSetting = widget.display?.goal;
  const goal = typeof goalSetting === "number" ? goalSetting : typeof goalSetting === "string" ? toNumber(variables?.[goalSetting]?.value) : null;

  const valueClass = model.value < 0 && model.format === "currency" ? "widget-kpi__value widget-kpi__value--negative" : "widget-kpi__value";
  // The rows behind the figure: the whole range, or its latest period.
  const dateGroup = query?.groupBy?.find((g) => g.bucket);
  const drill = onDrill
    ? drillTarget(query, widget.data, range, dateGroup && model.label ? [{ column: dateGroup.column, bucket: dateGroup.bucket, value: model.label }] : [])
    : null;

  return (
    <div className="widget-kpi">
      {drill && onDrill ? (
        <button type="button" className={`${valueClass} widget-kpi__drill`} title="Show the rows behind this number" onClick={() => onDrill(drill)}>
          {formatNumber(model.value, model.format)}
        </button>
      ) : (
        <span className={valueClass}>{formatNumber(model.value, model.format)}</span>
      )}
      {compare ? (
        <span className={good ? "widget-kpi__delta widget-kpi__delta--up" : "widget-kpi__delta widget-kpi__delta--down"}>
          <Icon name={delta >= 0 ? "chevron-up" : "chevron-down"} size={12} />
          {percent !== null ? formatNumber(percent, "percent") : formatNumber(delta, model.format)}
          <span className="widget-kpi__since">
            vs. {wholeRange ? "the range before" : model.label ? formatBucketLabel(previousLabel(result) ?? model.label, bucket) : "before"}
          </span>
        </span>
      ) : model.label ? (
        <span className="widget-kpi__since">{formatBucketLabel(model.label, bucket)}</span>
      ) : null}
      {secondary && secondaryValue !== null ? (
        <span className="widget-kpi__secondary">
          {secondary.name}: {formatNumber(secondaryValue, formatKindForType(secondary.type))}
        </span>
      ) : null}
      {trendValues.length > 1 ? <Sparkline values={trendValues} /> : null}
      {goal !== null && goal !== 0 ? (
        <span className="widget-kpi__goal" title={`Goal ${formatNumber(goal, model.format)}`}>
          <span className="widget-kpi__goal-bar">
            <span style={{ width: `${Math.max(0, Math.min(100, (model.value / goal) * 100))}%` }} />
          </span>
          {formatNumber(model.value / goal, "percent")} of {formatNumber(goal, model.format)}
        </span>
      ) : null}
    </div>
  );
}

/** The row a key figure shows: the latest period, or the only row. */
function toKpiRow(result: QueryResult): QueryResult["data"][number] | null {
  const group = result.meta.columns.find((c) => c.role === "group");
  if (result.data.length === 0) return null;
  if (!group) return result.data[0];
  return [...result.data].sort((a, b) => String(a[group.name]).localeCompare(String(b[group.name]))).at(-1) ?? null;
}

function sortedValues(result: QueryResult, measure: string): number[] {
  const group = result.meta.columns.find((c) => c.role === "group");
  const rows = group ? [...result.data].sort((a, b) => String(a[group.name]).localeCompare(String(b[group.name]))) : result.data;
  return rows.map((row) => toNumber(row[measure])).filter((v): v is number => v !== null);
}

/** A small line of the values, scaled to fit. */
function Sparkline({ values }: { values: number[] }) {
  const min = Math.min(...values);
  const max = Math.max(...values);
  const span = max - min || 1;
  const points = values.map((v, i) => `${(i / (values.length - 1)) * 100},${28 - ((v - min) / span) * 26}`).join(" ");
  return (
    <svg className="widget-kpi__trend" viewBox="0 0 100 30" preserveAspectRatio="none" role="img" aria-label="Trend">
      <polyline points={points} fill="none" stroke="currentColor" strokeWidth="1.5" vectorEffect="non-scaling-stroke" />
    </svg>
  );
}

/** A KPI card showing a dashboard variable (runway, savings rate, …). */
function VariableKpi({ widget, name, variables }: { widget: DashboardWidget; name: string; variables?: VariableResults }) {
  if (!variables) return <div className="widget-loading" aria-label="Loading" />;
  const result = variables[name];
  if (!result) return <p className="widget-note">The variable "{name}" does not exist.</p>;
  if (result.error) {
    return (
      <p className="widget-note widget-note--error" role="alert">
        <Icon name="activity" size={13} /> {name}: {result.error}
      </p>
    );
  }
  const number = toNumber(result.value);
  const format = overrideFormat(widget) ?? formatKindForType(result.type ?? undefined);
  return (
    <div className="widget-kpi">
      <span className={number !== null && number < 0 && format === "currency" ? "widget-kpi__value widget-kpi__value--negative" : "widget-kpi__value"}>
        {number !== null ? formatNumber(number, format) : result.value === null ? "—" : String(result.value)}
      </span>
      {/* The variable's label, not its formula name (plan_net); the card's title says the rest. */}
      {result.label ? <span className="widget-kpi__since">{result.label}</span> : null}
    </div>
  );
}

function previousLabel(result: QueryResult): string | null {
  const group = result.meta.columns.find((c) => c.role === "group");
  if (!group || result.data.length < 2) return null;
  const values = result.data.map((row) => String(row[group.name] ?? "")).sort();
  return values[values.length - 2] ?? null;
}

function TableWidget({
  result,
  query: shownQuery,
  widget,
  range,
  refreshToken,
  values,
}: {
  result: QueryResult;
  /** The query behind the shown result — while another interval loads, still the previous one. */
  query: AggregationQuery | null;
  widget: DashboardWidget;
  range: ResolvedRange;
  refreshToken: number;
  values: Record<string, VariableValue>;
}) {
  const format = overrideFormat(widget);
  // The total row: the same query without groups, so every measure is exact.
  const totalQuery = useMemo(() => {
    const query = widget.data?.query;
    if (!widget.display?.summary || !query || !widget.data || (query.groupBy ?? []).length === 0) return null;
    return runnableQuery({ ...query, groupBy: [], sort: undefined, limit: undefined }, { ...widget.data, asOf: undefined }, range, values);
  }, [widget, range, values]);
  const total = useQueryRun(totalQuery, refreshToken);

  // A linked record shows by its name; the id column then stays hidden. "Other" flags never show.
  const labelled = new Set(result.meta.columns.filter((c) => c.role === "label").map((c) => c.labelOf));
  const columns = result.meta.columns.filter((c) => c.role !== "other" && !(c.role === "group" && labelled.has(c.name)));
  if (result.data.length === 0) return <p className="widget-note">No data in this range.</p>;
  // The date group reads as periods ("September 2026", "Q3 2026"), headed by its interval.
  // Label the periods by the query that produced these rows, not by an interval still loading.
  const dateGroup = (shownQuery ?? widget.data?.query)?.groupBy?.find((g) => g.bucket);
  const dateKey = dateGroup ? (dateGroup.as ?? dateGroup.column) : null;
  const cell = (row: QueryResult["data"][number], column: QueryResult["meta"]["columns"][number]) => {
    const value = row[column.name];
    if (dateGroup?.bucket && column.name === dateKey && typeof value === "string") return formatBucketTitle(value.slice(0, 10), dateGroup.bucket);
    const number = column.role === "measure" ? toNumber(value) : null;
    if (number !== null) return numberCell(widget, column.name, number, format ?? formatKindForType(column.type)).text;
    const group = column.role === "label" ? column.labelOf! : column.name;
    if (column.role === "group" || column.role === "label") {
      const shown = labelOf(row, group, column.role === "label" ? column.name : undefined, otherKeyOf(result, group));
      return shown === "(none)" ? "—" : shown;
    }
    return value === null || value === "" ? "—" : String(value);
  };
  return (
    <div className="widget-table">
      <table>
        <thead>
          <tr>
            {columns.map((column) => (
              <th key={column.name} className={column.role === "measure" ? "widget-table__number" : undefined}>
                {dateGroup?.bucket && column.name === dateKey ? INTERVAL_LABELS[dateGroup.bucket].long : column.role === "label" ? column.labelOf : column.name}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {result.data.map((row, index) => (
            <TableRow key={index} widget={widget} value={linkValue(widget, row, otherKeyOf(result, widget.display?.link?.column ?? ""))}>
              {columns.map((column) => {
                const number = column.role === "measure" ? toNumber(row[column.name]) : null;
                const className =
                  number !== null ? numberCell(widget, column.name, number, format ?? formatKindForType(column.type)).className : column.role === "measure" ? "widget-table__number" : undefined;
                return (
                  <td key={column.name} className={className}>
                    {cell(row, column)}
                  </td>
                );
              })}
            </TableRow>
          ))}
        </tbody>
        {total && total.data[0] ? (
          <tfoot>
            <tr>
              {columns.map((column, index) => {
                const number = column.role === "measure" ? toNumber(total.data[0][column.name]) : null;
                const shown = number !== null ? numberCell(widget, column.name, number, format ?? formatKindForType(column.type)) : null;
                return (
                  <td key={column.name} className={shown?.className ?? (column.role === "measure" ? "widget-table__number" : undefined)}>
                    {shown ? shown.text : index === 0 ? "Total" : ""}
                  </td>
                );
              })}
            </tr>
          </tfoot>
        ) : null}
      </table>
    </div>
  );
}

/** The rows as they are: the widget's columns, sorted and limited, with the explorer's formatting. */
function RowsTable({ widget, range, refreshToken }: { widget: DashboardWidget; range: ResolvedRange; refreshToken: number }) {
  const state = useRows(widget, range, refreshToken);
  const snapshot = useSchemaStore((s) => s.snapshot);
  const table = snapshot?.tables.find((t) => t.name === widget.data?.query?.source);
  const columns = (widget.data?.rows?.columns ?? []).map((name) => table?.columns.find((c) => c.name === name) ?? { name, type: "text" });
  if (state.status === "error") {
    return (
      <p className="widget-note widget-note--error" role="alert">
        <Icon name="activity" size={13} /> {state.error}
      </p>
    );
  }
  if (state.status === "loading") return <div className="widget-loading" aria-label="Loading" />;
  if (state.rows.length === 0) return <p className="widget-note">No rows in this range.</p>;
  return (
    <div className="widget-table">
      <table>
        <thead>
          <tr>
            {columns.map((column) => (
              <th key={column.name}>{column.displayName || column.name}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {state.rows.map((row, index) => (
            <TableRow key={index} widget={widget} value={linkValue(widget, row)}>
              {columns.map((column) => {
                // A column set to a format (display.columns) writes its number that way; others as in the data view.
                const number = widget.display?.columns?.[column.name] ? toNumber(row[column.name]) : null;
                const shown = number !== null ? numberCell(widget, column.name, number, formatKindForType(valueTypeOf(column))) : null;
                return (
                  <td key={column.name} className={shown?.className}>
                    {shown ? shown.text : renderCellValue(column, row[column.name])}
                  </td>
                );
              })}
            </TableRow>
          ))}
        </tbody>
      </table>
      {state.total > state.rows.length ? (
        <p className="widget-note">
          {state.rows.length} of {state.total} rows
        </p>
      ) : null}
    </div>
  );
}

/** The value a row passes to the linked dashboard (display.link); none for "Other" and empty values. */
function linkValue(widget: DashboardWidget, row: Record<string, unknown>, otherKey?: string): string | null {
  const column = widget.display?.link?.column;
  if (!column || (otherKey && row[otherKey] === true)) return null;
  const value = row[column];
  return value === null || value === undefined || value === "" ? null : String(value);
}

/** A table row; with display.link, a click (or Enter) opens the other dashboard filtered by the row's value. */
function TableRow({ widget, value, children }: { widget: DashboardWidget; value: string | null; children: ReactNode }) {
  const link = widget.display?.link;
  if (!link || value === null) return <tr>{children}</tr>;
  return (
    <LinkedRow dashboard={link.dashboard} filter={link.filter || link.column} value={value}>
      {children}
    </LinkedRow>
  );
}

/** Kept apart so tables without a link need no router. The dashboard's range goes along. */
function LinkedRow({ dashboard, filter, value, children }: { dashboard: number; filter: string; value: string; children: ReactNode }) {
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const open = () => {
    const next = new URLSearchParams();
    for (const key of ["range", "from", "to"]) {
      const kept = params.get(key);
      if (kept) next.set(key, kept);
    }
    next.set(`filter.${filter}`, value);
    navigate(`/dashboards/${dashboard}?${next.toString()}`);
  };
  return (
    <tr
      className="widget-table__link"
      tabIndex={0}
      title="Open in the linked dashboard"
      onClick={open}
      onKeyDown={(event) => {
        if (event.key === "Enter") open();
      }}
    >
      {children}
    </tr>
  );
}

/** Headings (#), list items (-) and paragraphs; text only, no HTML. */
function TextWidget({ text }: { text: string }) {
  const blocks: ReactNode[] = [];
  let list: string[] = [];
  const flush = () => {
    if (list.length > 0) {
      blocks.push(
        <ul key={`l${blocks.length}`}>
          {list.map((item, index) => (
            <li key={index}>{item}</li>
          ))}
        </ul>,
      );
      list = [];
    }
  };
  for (const line of text.split("\n")) {
    const trimmed = line.trim();
    if (trimmed.startsWith("- ")) {
      list.push(trimmed.slice(2));
      continue;
    }
    flush();
    if (!trimmed) continue;
    if (trimmed.startsWith("#")) blocks.push(<h3 key={blocks.length}>{trimmed.replace(/^#+\s*/, "")}</h3>);
    else blocks.push(<p key={blocks.length}>{trimmed}</p>);
  }
  flush();
  return <div className="widget-text">{blocks.length > 0 ? blocks : <p className="widget-note">Add some text.</p>}</div>;
}

/**
 * A donut one level down (Step 4.5): the rows of the clicked slice, grouped
 * by the "drill into" column — e.g. the subcategories of "Food".
 */
export function drilledWidget(widget: DashboardWidget, value: unknown): DashboardWidget {
  const query = widget.data?.query;
  const first = query?.groupBy?.[0];
  const drill = widget.display?.drill;
  if (!query || !first || !drill) return widget;
  return {
    ...widget,
    data: {
      ...widget.data,
      query: { ...query, filter: { ...query.filter, [first.column]: value === undefined || value === "" ? null : value }, groupBy: [{ ...first, column: drill, as: undefined }] },
    },
  };
}
