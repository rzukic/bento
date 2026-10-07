import { useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { createDashboard } from "../../api/dashboards";
import { ApiClientError } from "../../api/client";
import { createRow } from "../../api/data";
import { createFunction, listFunctionTemplates, listFunctions } from "../../api/functions";
import { applySchemaDraft, getCanonicalSchemaSnapshot, previewSchemaDraft } from "../../api/schema";
import { Button } from "../../components/ui/Button";
import { useSchemaStore } from "../../stores/schemaStore";
import type { ModelTable } from "../../types/api";
import {
  ensureCostFunction,
  financeCashFlow,
  financeConflicts,
  financeNames,
  seedExamples,
  suggestedPrefix,
  wealthDashboard,
  withFinanceTables,
} from "./financeTemplate";

/**
 * The "Wealth & cash flow" starter (Step 3.9): new tables for accounts,
 * categories, transactions, trades, prices and dividends — nothing existing
 * changes — plus a cash-flow and a wealth dashboard, optionally with
 * example rows. Realised gains use the function avg_cost, created from its
 * template when missing.
 */
export function FinanceSetup({ tables, onError }: { tables: ModelTable[]; onError: (message: string | null) => void }) {
  const navigate = useNavigate();
  const loadSchema = useSchemaStore((state) => state.load);
  const [prefix, setPrefix] = useState(() => suggestedPrefix(tables));
  const [examples, setExamples] = useState(true);
  const [progress, setProgress] = useState<string | null>(null);
  const names = useMemo(() => financeNames(prefix), [prefix]);
  const conflicts = financeConflicts(names, tables);
  const needsPrefix = financeConflicts(financeNames(""), tables).length > 0;

  const setUp = async () => {
    onError(null);
    try {
      setProgress("Checking the new tables");
      const current = await getCanonicalSchemaSnapshot();
      const taken = financeConflicts(names, current.tables);
      if (taken.length > 0) throw new Error(`These tables exist already: ${taken.join(", ")}. Choose another prefix.`);
      // The trades' realised gains call the function avg_cost (Step 4.3b); it must exist before the tables check.
      await ensureCostFunction({ list: listFunctions, templates: listFunctionTemplates, create: createFunction });
      const draft = withFinanceTables(current, names);
      const preview = await previewSchemaDraft(draft);
      if (!preview.isValid) throw new Error(preview.errors.map((issue) => issue.message).join(" "));

      setProgress("Creating the tables");
      await applySchemaDraft(draft);
      const snapshot = await loadSchema({ force: true });
      if (examples) {
        setProgress("Adding example rows");
        await seedExamples(names, createRow);
      }

      setProgress("Creating the dashboards");
      const transactions = snapshot?.tables.find((t) => t.name === names.transactions);
      if (transactions) await createDashboard("Cash flow", financeCashFlow(transactions));
      const wealth = await createDashboard("Wealth", wealthDashboard(names));
      navigate(`/dashboards/${wealth.id}`);
    } catch (cause) {
      onError(cause instanceof ApiClientError || cause instanceof Error ? cause.message : "The finance tables could not be set up.");
    } finally {
      setProgress(null);
    }
  };

  return (
    <div className="dashboards-new__starter">
      <div className="dashboards-new__starter-text">
        <span className="dashboards-new__starter-title">Wealth & cash flow</span>
        <span className="dashboards-new__starter-description">
          New tables for accounts, categories, transactions, depot trades, prices and dividends, and two dashboards: cash flow with an account filter, and
          wealth — net worth over time, depot value, realised and unrealised gains, returns, allocation, positions and dividends. Existing tables stay as
          they are; the function avg_cost is added for realised gains unless you have it.
        </span>
        {progress ? <span className="dashboards-new__starter-description" role="status">{progress}…</span> : null}
      </div>
      <div className="dashboards-new__starter-actions">
        {needsPrefix ? (
          <label className="dashboards-new__prefix">
            <span>Table names start with</span>
            <input
              className="field__control field__control--compact mono"
              aria-label="Table name prefix"
              value={prefix}
              onChange={(event) => setPrefix(event.target.value)}
            />
          </label>
        ) : null}
        <label className="dashboards-new__check">
          <input type="checkbox" checked={examples} onChange={(event) => setExamples(event.target.checked)} />
          Example rows
        </label>
        <Button type="button" variant="primary" disabled={progress !== null || conflicts.length > 0} onClick={() => void setUp()}>
          Set up
        </Button>
      </div>
      {conflicts.length > 0 ? <span className="dashboards-new__starter-description">Taken: {conflicts.join(", ")}</span> : null}
    </div>
  );
}
