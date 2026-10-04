import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import type { DashboardWidget } from "../../../api/dashboards";
import type { QueryResult, VariableValue } from "../../../api/query";
import { Icon } from "../../../components/common/Icon";
import { formatKindForType, formatNumber, toNumber, type NumberFormatKind } from "../../../lib/format";
import { useAppStore } from "../../../stores/appStore";
import { EChart } from "../charts/EChart";
import { buildChartOption, formatBucketLabel, readChartTokens, type ChartTokens } from "../charts/chartOptions";
import type { ResolvedRange } from "../dateRange";
import { runnableQuery, variableValues, type VariableResults } from "../formulaQueries";
import { kpiExtraQueries, useQueryRun, useRows } from "../useExtraQueries";
import { useWidgetResult } from "../useWidgetResult";
import { labelOf, otherKeyOf, toChartModel, toKpiModel } from "../widgetData";
import { applyMeasureShows, resolveReferenceLines, shiftedRange, withShifted, withTotal } from "../chartTransforms";
import { renderCellValue } from "../../data-explorer/components/cellValue";
import { useSchemaStore } from "../../../stores/schemaStore";

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

export function WidgetContent({
  widget,
  range,
  refreshToken,
  tokens,
  variables,
}: {
  widget: DashboardWidget;
  range: ResolvedRange;
  refreshToken: number;
  tokens: ChartTokens;
  /** The dashboard's variables, for KPI cards showing one and for widget formulas. */
  variables?: VariableResults;
}) {
  const values = useMemo(() => variableValues(variables), [variables]);
  const state = useWidgetResult(widget, range, refreshToken, values);

  if (widget.type === "text") return <TextWidget text={widget.display?.text ?? ""} />;
  if (widget.type === "kpi" && widget.data?.variable) {
    return <VariableKpi widget={widget} name={widget.data.variable} variables={variables} />;
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
        <Icon name="activity" size={13} /> {state.error}
      </p>
    );
  }
  if (!state.result) return <div className="widget-loading" aria-label="Loading" />;

  switch (widget.type) {
    case "kpi":
      return <KpiWidget result={state.result} widget={widget} range={range} refreshToken={refreshToken} values={values} variables={variables} />;
    case "table":
      return <TableWidget result={state.result} widget={widget} range={range} refreshToken={refreshToken} values={values} />;
    default:
      return (
        <ChartWidget widget={widget} result={state.result} query={state.query} range={range} tokens={tokens} refreshToken={refreshToken} values={values} />
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
}: {
  widget: DashboardWidget;
  result: QueryResult;
  query: Parameters<typeof toChartModel>[1] | null;
  range: ResolvedRange;
  tokens: ChartTokens;
  refreshToken: number;
  values: Record<string, VariableValue>;
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
    let next = applyMeasureShows(toChartModel(result, query ?? undefined, range, format), measures, hasBreakdown);
    if (shift && earlier && shiftedResult) {
      next = withShifted(next, applyMeasureShows(toChartModel(shiftedResult, query ?? undefined, earlier, format), measures, false), shift);
    }
    if (stacked && display?.totalLine) next = withTotal(next);
    return next;
  }, [result, query, range, widget, shift, earlier, shiftedResult, stacked, hasBreakdown, display?.totalLine]);

  // The series under the pointer, read by the tooltip when it renders.
  const hovered = useRef<number | null>(null);
  const referenceLines = useMemo(() => resolveReferenceLines(display?.referenceLines, values), [display?.referenceLines, values]);
  const option = useMemo(
    () =>
      buildChartOption(widget.type, model, tokens, {
        stacked,
        hovered: () => hovered.current,
        referenceLines,
        colorBySign: display?.colorBySign ?? false,
      }),
    [widget.type, stacked, model, tokens, referenceLines, display?.colorBySign],
  );
  const onHoverSeries = useCallback((index: number | null) => {
    hovered.current = index;
  }, []);
  if (model.categories.length === 0) return <p className="widget-note">No data in this range.</p>;
  return <EChart option={option} label={widget.title || "Chart"} onHoverSeries={onHoverSeries} />;
}

function KpiWidget({
  result,
  widget,
  range,
  refreshToken,
  values,
  variables,
}: {
  result: QueryResult;
  widget: DashboardWidget;
  range: ResolvedRange;
  refreshToken: number;
  values: Record<string, VariableValue>;
  variables?: VariableResults;
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

  return (
    <div className="widget-kpi">
      <span className={model.value < 0 && model.format === "currency" ? "widget-kpi__value widget-kpi__value--negative" : "widget-kpi__value"}>
        {formatNumber(model.value, model.format)}
      </span>
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
      <span className="widget-kpi__since">{name}</span>
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
  widget,
  range,
  refreshToken,
  values,
}: {
  result: QueryResult;
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
  const cell = (row: QueryResult["data"][number], column: QueryResult["meta"]["columns"][number]) => {
    const value = row[column.name];
    const number = column.role === "measure" ? toNumber(value) : null;
    if (number !== null) return formatNumber(number, format ?? formatKindForType(column.type));
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
                {column.role === "label" ? column.labelOf : column.name}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {result.data.map((row, index) => (
            <tr key={index}>
              {columns.map((column) => (
                <td key={column.name} className={column.role === "measure" ? "widget-table__number" : undefined}>
                  {cell(row, column)}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
        {total && total.data[0] ? (
          <tfoot>
            <tr>
              {columns.map((column, index) => {
                const number = column.role === "measure" ? toNumber(total.data[0][column.name]) : null;
                return (
                  <td key={column.name} className={column.role === "measure" ? "widget-table__number" : undefined}>
                    {number !== null ? formatNumber(number, format ?? formatKindForType(column.type)) : index === 0 ? "Total" : ""}
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
            <tr key={index}>
              {columns.map((column) => (
                <td key={column.name}>{renderCellValue(column, row[column.name])}</td>
              ))}
            </tr>
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
