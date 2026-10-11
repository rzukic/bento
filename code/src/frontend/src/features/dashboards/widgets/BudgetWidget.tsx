import { useEffect, useMemo, useState } from "react";
import type { BudgetConfig } from "../../../api/dashboards";
import { fetchTableRows } from "../../../api/data";
import { formatNumber } from "../../../lib/format";
import { useSchemaStore } from "../../../stores/schemaStore";
import { DEFAULT_QUERY, type FilterClause } from "../../../types/explorer";
import { getDisplayColumnForTable } from "../../data-explorer/components/editors/referenceMetadata";
import { budgetMonths, budgetRows, budgetState, limitTable, spentQuery } from "../budget";
import type { ResolvedRange } from "../dateRange";
import type { DrillTarget } from "../drill";
import { globalFilterClauses } from "../dashboardFilters";
import { useQueryRun } from "../useExtraQueries";

const percent = (share: number) => `${Math.round(share * 100)} %`;

/** "2026-10" → "October 2026" in the user's language. */
function monthLabel(month: string): string {
  const [year, m] = month.split("-").map(Number);
  return new Date(year!, m! - 1, 1).toLocaleDateString(undefined, { month: "long", year: "numeric" });
}

/**
 * Budgets (Step 4.5): per category the monthly limit times the months of
 * the range, what was spent, a bar, and the share — amber from 80 %, red
 * when over. A row opens the bookings behind it.
 */
export function BudgetWidget({
  config,
  range,
  refreshToken,
  onDrill,
}: {
  config: BudgetConfig;
  range: ResolvedRange;
  refreshToken: number;
  onDrill?: (target: DrillTarget) => void;
}) {
  const snapshot = useSchemaStore((s) => s.snapshot);
  const categoriesTable = limitTable(config, snapshot);
  const query = useMemo(() => (config.limit ? spentQuery(config, range) : null), [config, range]);
  const result = useQueryRun(query, refreshToken);
  const [categories, setCategories] = useState<Record<string, unknown>[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const key = categoriesTable?.columns.find((c) => c.primaryKey)?.name ?? "id";
  const display = getDisplayColumnForTable(categoriesTable ?? null);

  useEffect(() => {
    if (!categoriesTable) return;
    let cancelled = false;
    fetchTableRows(categoriesTable.name, { ...DEFAULT_QUERY, perPage: 200, sort: [{ column: display, dir: "asc" }] })
      .then((r) => !cancelled && setCategories(r.data))
      .catch(() => !cancelled && setError("The limits could not be loaded."));
    return () => {
      cancelled = true;
    };
  }, [categoriesTable, display, refreshToken]);

  if (!categoriesTable) return <p className="widget-note">Choose the spending table and its link to categories in the widget's settings.</p>;
  if (!config.limit) {
    return (
      <p className="widget-note">
        Add a number column for the monthly limit to {categoriesTable.displayName ?? categoriesTable.name} (e.g. <code>budget</code>, an amount), fill in the
        limits, and choose it in the widget's settings.
      </p>
    );
  }
  if (error) return <p className="widget-note widget-note--error">{error}</p>;
  if (!categories || !result) return <div className="widget-loading" aria-label="Loading" />;

  const { months, month, from, to } = budgetMonths(range, config.period);
  const { rows, unbudgeted, total } = budgetRows(result, categories, config, display, key, months);
  if (rows.length === 0) {
    return <p className="widget-note">No category has a limit yet. Enter monthly limits in {categoriesTable.displayName ?? categoriesTable.name}.</p>;
  }

  const open = (id: unknown) => {
    if (!onDrill) return;
    const filters: FilterClause[] = [
      { column: config.category, op: "eq", value: String(id) },
      { column: config.amount, op: config.expenses === "positive" ? "gt" : "lt", value: "0" },
      { column: config.date, op: "gte", value: from },
      { column: config.date, op: "lt", value: to },
      ...globalFilterClauses(config.source, range),
    ];
    onDrill({ table: config.source, filters });
  };

  return (
    <div className="budget">
      <ul className="budget__rows" aria-label="Budgets">
        {rows.map((row) => {
          const state = budgetState(row.share);
          const content = (
            <>
              <span className="budget__name">{row.label}</span>
              <span className="budget__amounts">
                {formatNumber(row.spent, "currency")} <span className="budget__of">of {formatNumber(row.limit ?? 0, "currency")}</span>
              </span>
              <span className={`budget__share budget__share--${state}`}>{percent(row.share ?? 0)}</span>
              <span
                className="budget__bar"
                role="progressbar"
                aria-label={`${row.label}: ${percent(row.share ?? 0)} of the budget`}
                aria-valuemin={0}
                aria-valuemax={100}
                aria-valuenow={Math.round((row.share ?? 0) * 100)}
              >
                <span className={`budget__fill budget__fill--${state}`} style={{ width: `${Math.min(100, (row.share ?? 0) * 100)}%` }} />
              </span>
            </>
          );
          return (
            <li key={String(row.id)}>
              {onDrill ? (
                <button type="button" className="budget__row" onClick={() => open(row.id)}>
                  {content}
                </button>
              ) : (
                <div className="budget__row">{content}</div>
              )}
            </li>
          );
        })}
      </ul>
      <p className="budget__footer">
        {formatNumber(total.spent, "currency")} of {formatNumber(total.limit, "currency")}
        {month ? ` · ${monthLabel(month)}` : ` · ${months} ${months === 1 ? "month" : "months"}`}
        {unbudgeted.count > 0 ? ` · ${formatNumber(unbudgeted.spent, "currency")} in ${unbudgeted.count} ${unbudgeted.count === 1 ? "category" : "categories"} without a limit` : ""}
      </p>
    </div>
  );
}
