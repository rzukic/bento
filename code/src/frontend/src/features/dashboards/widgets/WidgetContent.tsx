import { useEffect, useMemo, useState, type ReactNode } from "react";
import type { DashboardWidget } from "../../../api/dashboards";
import type { QueryResult } from "../../../api/query";
import { Icon } from "../../../components/common/Icon";
import { formatKindForType, formatNumber, toNumber, type NumberFormatKind } from "../../../lib/format";
import { useAppStore } from "../../../stores/appStore";
import { EChart } from "../charts/EChart";
import { buildChartOption, formatBucketLabel, readChartTokens, type ChartTokens } from "../charts/chartOptions";
import type { ResolvedRange } from "../dateRange";
import { variableValues, type VariableResults } from "../formulaQueries";
import { useWidgetResult } from "../useWidgetResult";
import { toChartModel, toKpiModel } from "../widgetData";

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
      return <KpiWidget result={state.result} widget={widget} />;
    case "table":
      return <TableWidget result={state.result} format={overrideFormat(widget)} />;
    default:
      return <ChartWidget widget={widget} result={state.result} query={state.query} range={range} tokens={tokens} />;
  }
}

function ChartWidget({
  widget,
  result,
  query,
  range,
  tokens,
}: {
  widget: DashboardWidget;
  result: QueryResult;
  query: Parameters<typeof toChartModel>[1] | null;
  range: ResolvedRange;
  tokens: ChartTokens;
}) {
  const model = useMemo(() => toChartModel(result, query ?? undefined, range, overrideFormat(widget)), [result, query, range, widget]);
  const option = useMemo(
    () => buildChartOption(widget.type, model, tokens, widget.display?.stacked ?? false),
    [widget.type, widget.display?.stacked, model, tokens],
  );
  if (model.categories.length === 0) return <p className="widget-note">No data in this range.</p>;
  return <EChart option={option} label={widget.title || "Chart"} />;
}

function KpiWidget({ result, widget }: { result: QueryResult; widget: DashboardWidget }) {
  const model = toKpiModel(result, overrideFormat(widget));
  if (model.value === null) return <p className="widget-note">No data in this range.</p>;
  const compare = widget.display?.compare !== false && model.previous !== null;
  const delta = compare ? model.value - model.previous! : 0;
  const percent = compare && model.previous !== 0 ? delta / Math.abs(model.previous!) : null;
  const bucket = widget.data?.query?.groupBy?.find((g) => g.bucket)?.bucket;
  return (
    <div className="widget-kpi">
      <span className={model.value < 0 && model.format === "currency" ? "widget-kpi__value widget-kpi__value--negative" : "widget-kpi__value"}>
        {formatNumber(model.value, model.format)}
      </span>
      {compare ? (
        <span className={delta >= 0 ? "widget-kpi__delta widget-kpi__delta--up" : "widget-kpi__delta widget-kpi__delta--down"}>
          <Icon name={delta >= 0 ? "chevron-up" : "chevron-down"} size={12} />
          {percent !== null ? formatNumber(percent, "percent") : formatNumber(delta, model.format)}
          <span className="widget-kpi__since">
            vs. {model.label ? formatBucketLabel(previousLabel(result) ?? model.label, bucket) : "before"}
          </span>
        </span>
      ) : model.label ? (
        <span className="widget-kpi__since">{formatBucketLabel(model.label, bucket)}</span>
      ) : null}
    </div>
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

function TableWidget({ result, format }: { result: QueryResult; format?: NumberFormatKind }) {
  // A linked record shows by its name; the id column then stays hidden.
  const labelled = new Set(result.meta.columns.filter((c) => c.role === "label").map((c) => c.labelOf));
  const columns = result.meta.columns.filter((c) => !(c.role === "group" && labelled.has(c.name)));
  if (result.data.length === 0) return <p className="widget-note">No data in this range.</p>;
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
              {columns.map((column) => {
                const value = row[column.name];
                const number = column.role === "measure" ? toNumber(value) : null;
                return (
                  <td key={column.name} className={column.role === "measure" ? "widget-table__number" : undefined}>
                    {number !== null
                      ? formatNumber(number, format ?? formatKindForType(column.type))
                      : value === null || value === ""
                        ? "—"
                        : String(value)}
                  </td>
                );
              })}
            </tr>
          ))}
        </tbody>
      </table>
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
