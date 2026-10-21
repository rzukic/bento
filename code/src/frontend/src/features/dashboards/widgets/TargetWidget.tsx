import { useEffect, useMemo, useState } from "react";
import type { TargetConfig } from "../../../api/dashboards";
import { fetchTableRows } from "../../../api/data";
import { formatKindForType, formatNumber, type NumberFormatKind } from "../../../lib/format";
import { effectiveLocale } from "../../../lib/preferences";
import { useSchemaStore } from "../../../stores/schemaStore";
import { DEFAULT_QUERY, type FilterClause } from "../../../types/explorer";
import { getDisplayColumnForTable } from "../../data-explorer/components/editors/referenceMetadata";
import { periodName, targetPeriod, targetRows, targetState, targetTable, valueQuery } from "../target";
import type { ResolvedRange } from "../dateRange";
import type { DrillTarget } from "../drill";
import { globalFilterClauses } from "../dashboardFilters";
import { useQueryRun } from "../useExtraQueries";

const percent = (share: number) => `${Math.round(share * 100)} %`;

/**
 * Targets: per group its value against its target (times the periods of the
 * range), a bar and the share. A limit turns amber from 80 % and red when over;
 * a goal turns green once reached. A row opens the rows behind it.
 */
export function TargetWidget({
  config,
  range,
  refreshToken,
  onDrill,
}: {
  config: TargetConfig;
  range: ResolvedRange;
  refreshToken: number;
  onDrill?: (target: DrillTarget) => void;
}) {
  const snapshot = useSchemaStore((s) => s.snapshot);
  const groupsTable = targetTable(config, snapshot);
  const query = useMemo(() => (config.target ? valueQuery(config, range) : null), [config, range]);
  const result = useQueryRun(query, refreshToken);
  const [groups, setGroups] = useState<Record<string, unknown>[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const key = groupsTable?.columns.find((c) => c.primaryKey)?.name ?? "id";
  const display = getDisplayColumnForTable(groupsTable ?? null);

  useEffect(() => {
    if (!groupsTable) return;
    let cancelled = false;
    fetchTableRows(groupsTable.name, { ...DEFAULT_QUERY, perPage: 200, sort: [{ column: display, dir: "asc" }] })
      .then((r) => !cancelled && setGroups(r.data))
      .catch(() => !cancelled && setError("The targets could not be loaded."));
    return () => {
      cancelled = true;
    };
  }, [groupsTable, display, refreshToken]);

  const groupsName = groupsTable?.displayName ?? groupsTable?.name ?? "";
  if (!groupsTable) return <p className="widget-note">Choose the table, its date and its link to the table with the targets in the widget's settings.</p>;
  if (!config.target) {
    return <p className="widget-note">Add a number column for the targets to {groupsName}, fill them in, and choose it in the widget's settings.</p>;
  }
  if (error) return <p className="widget-note widget-note--error">{error}</p>;
  if (!groups || !result) return <div className="widget-loading" aria-label="Loading" />;

  const per = config.per ?? "month";
  const kind = config.kind ?? "limit";
  const { count, start, from, to } = targetPeriod(range, per, config.period);
  const { rows, untargeted, total } = targetRows(result, groups, config, display, key, count);
  if (rows.length === 0) return <p className="widget-note">No row of {groupsName} has a target yet.</p>;

  const measureType = result.meta.columns.find((c) => c.role === "measure")?.type;
  const format: NumberFormatKind = config.format && config.format !== "auto" ? config.format : formatKindForType(measureType);
  const amount = (value: number) => formatNumber(value, format);
  const noun = kind === "goal" ? "target" : "limit";

  const open = (id: unknown) => {
    if (!onDrill) return;
    const filters: FilterClause[] = [
      { column: config.group, op: "eq", value: String(id) },
      { column: config.date, op: "gte", value: from },
      { column: config.date, op: "lt", value: to },
      ...globalFilterClauses(config.source, range),
    ];
    onDrill({ table: config.source, filters });
  };

  return (
    <div className="target">
      <ul className="target__rows" aria-label="Targets">
        {rows.map((row) => {
          const state = targetState(row.share, kind);
          const content = (
            <>
              <span className="target__name">{row.label}</span>
              <span className="target__amounts">
                {amount(row.value)} <span className="target__of">of {amount(row.target)}</span>
              </span>
              <span className={`target__share target__share--${state}`}>{percent(row.share)}</span>
              <span
                className="target__bar"
                role="progressbar"
                aria-label={`${row.label}: ${percent(row.share)} of the ${noun}`}
                aria-valuemin={0}
                aria-valuemax={100}
                aria-valuenow={Math.round(row.share * 100)}
              >
                <span className={`target__fill target__fill--${state}`} style={{ width: `${Math.max(0, Math.min(100, row.share * 100))}%` }} />
              </span>
            </>
          );
          return (
            <li key={String(row.id)}>
              {onDrill ? (
                <button type="button" className="target__row" onClick={() => open(row.id)}>
                  {content}
                </button>
              ) : (
                <div className="target__row">{content}</div>
              )}
            </li>
          );
        })}
      </ul>
      <p className="target__footer">
        {amount(total.value)} of {amount(total.target)}
        {start ? ` · ${periodName(start, per, effectiveLocale())}` : ` · ${count} ${count === 1 ? per : `${per}s`}`}
        {untargeted.count > 0 ? ` · ${amount(untargeted.value)} in ${untargeted.count} without a ${noun}` : ""}
      </p>
    </div>
  );
}
