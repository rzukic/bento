import { useState } from "react";
import type { CategoriseRow, CategoryOption } from "../../../api/categorise";
import { Icon } from "../../../components/common/Icon";
import { Alert } from "../../../components/ui/Alert";
import { Button } from "../../../components/ui/Button";
import type { ModelColumn } from "../../../types/api";
import { SURE, type Categorising, type CategoryChoice } from "./useCategorise";
import "./Categorise.css";

function formatCost(usd: number): string {
  return usd > 0 && usd < 0.01 ? "< $0.01" : `$${usd.toFixed(2)}`;
}

const SOURCE_LABEL: Record<CategoryChoice["source"], string> = { rule: "Rule", history: "Earlier", ai: "AI", you: "You" };

/**
 * Above the rows (Step 4.8): which column to fill, how many rows have a value
 * and from where, ✨ for the rest, and the table's rules.
 */
export function CategoriseBar({
  columns,
  column,
  onColumnChange,
  categorising,
  rows,
  aiEnabled,
}: {
  columns: ModelColumn[];
  column: string;
  onColumnChange: (column: string) => void;
  categorising: Categorising;
  /** The rows that can get a value (new rows of the import, uncategorised rows of the table). */
  rows: CategoriseRow[];
  aiEnabled: boolean;
}) {
  const [showRules, setShowRules] = useState(false);
  const { choices, busy, error, aiCost, rules } = categorising;
  const open = rows.filter((r) => !choices[r.key]);
  const counts = { rule: 0, history: 0, ai: 0, you: 0 };
  for (const row of rows) {
    const choice = choices[row.key];
    if (choice) counts[choice.source] += 1;
  }
  const decided = rows.length - open.length;
  const parts = [
    counts.rule ? `${counts.rule} by rules` : null,
    counts.history ? `${counts.history} like earlier rows` : null,
    counts.ai ? `${counts.ai} by AI${aiCost > 0 ? ` (${formatCost(aiCost)})` : ""}` : null,
    counts.you ? `${counts.you} by you` : null,
  ].filter(Boolean);

  return (
    <div className="categorise-bar" role="group" aria-label="Categories">
      <div className="categorise-bar__row">
        <label className="categorise-bar__column">
          <span>Fill in</span>
          <select className="field__control field__control--compact" value={column} onChange={(event) => onColumnChange(event.target.value)} aria-label="Column to categorise">
            <option value="">Nothing</option>
            {columns.map((c) => (
              <option key={c.name} value={c.name}>
                {c.displayName ?? c.name}
              </option>
            ))}
          </select>
        </label>
        {column ? (
          <>
            <span className="categorise-bar__status" role="status">
              {busy === "rules"
                ? "Applying rules…"
                : rows.length === 0
                  ? ""
                  : `${decided} of ${rows.length} ${rows.length === 1 ? "row" : "rows"} filled${parts.length ? ` — ${parts.join(", ")}` : ""}`}
            </span>
            {aiEnabled && open.length > 0 ? (
              <Button
                type="button"
                variant="primary"
                size="sm"
                leadingIcon={<Icon name="sparkles" size={13} />}
                disabled={busy !== null}
                onClick={() => void categorising.suggestRest(open)}
              >
                {busy === "ai" ? "Suggesting…" : `Suggest the rest (${open.length})`}
              </Button>
            ) : null}
            <Button type="button" variant="ghost" size="sm" aria-expanded={showRules} onClick={() => setShowRules((v) => !v)}>
              Rules ({rules.length})
            </Button>
          </>
        ) : null}
      </div>
      {column && counts.ai > 0 ? (
        <label className="categorise-bar__remember">
          <input type="checkbox" checked={categorising.rememberAi} onChange={(event) => categorising.setRememberAi(event.target.checked)} />
          <span>Remember the AI's sure choices ({Math.round(SURE * 100)} % and up) as rules, so the same payees need no AI next time</span>
        </label>
      ) : null}
      {column && !aiEnabled && open.length > 0 && busy === null ? (
        <p className="categorise-bar__hint">Choose a value per row; your choices become rules for the next import. With Bento's AI set up (AI page), ✨ suggests the rest.</p>
      ) : null}
      {error ? <Alert tone="info" message={error} /> : null}
      {showRules && column ? <RuleList categorising={categorising} /> : null}
    </div>
  );
}

function RuleList({ categorising }: { categorising: Categorising }) {
  const { rules } = categorising;
  if (rules.length === 0)
    return <p className="categorise-bar__hint">No rules yet. When you change a suggested value — or keep a sure AI choice — Bento remembers the payee as a rule and uses it next time without asking the AI.</p>;
  return (
    <ul className="categorise-rules" aria-label="Rules">
      {rules.map((rule) => (
        <li key={rule.id}>
          <span>
            <code>{rule.matchColumn}</code> contains <strong>“{rule.pattern}”</strong> → {rule.label ?? rule.value}
          </span>
          <Button type="button" variant="ghost" size="sm" aria-label={`Delete rule ${rule.pattern}`} onClick={() => void categorising.removeRule(rule.id)}>
            <Icon name="trash" size={13} />
          </Button>
        </li>
      ))}
    </ul>
  );
}

/** One row's value: a choice from the options with where it came from; a correction can be remembered as a rule. */
export function CategoryCell({
  rowKey,
  label,
  options,
  choice,
  categorising,
}: {
  rowKey: string;
  label: string;
  options: CategoryOption[];
  choice: CategoryChoice | undefined;
  categorising: Categorising;
}) {
  const groups = new Map<string, CategoryOption[]>();
  for (const option of options) {
    const group = option.group ?? "";
    groups.set(group, [...(groups.get(group) ?? []), option]);
  }
  const unsure = choice?.source === "ai" && (choice.confidence ?? 0) < 0.6;
  return (
    <div className={`category-cell${unsure ? " category-cell--unsure" : ""}`}>
      <div className="category-cell__line">
        <select
          className="field__control field__control--compact category-cell__select"
          aria-label={`${label} for row ${rowKey}`}
          value={choice?.value ?? ""}
          onChange={(event) => categorising.choose(rowKey, event.target.value)}
        >
          <option value="">—</option>
          {[...groups.entries()].map(([group, items]) =>
            group ? (
              <optgroup key={group} label={group}>
                {items.map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </optgroup>
            ) : (
              items.map((o) => (
                <option key={o.value} value={o.value}>
                  {o.label}
                </option>
              ))
            ),
          )}
          {choice && !options.some((o) => o.value === choice.value) ? <option value={choice.value}>{choice.label}</option> : null}
        </select>
        {choice ? (
          <span
            className={`category-cell__source category-cell__source--${choice.source}`}
            title={choice.source === "rule" && choice.keyword ? `Rule: ${choice.matchColumn} contains “${choice.keyword}”` : undefined}
          >
            {SOURCE_LABEL[choice.source]}
            {choice.source === "ai" && choice.confidence !== null ? ` ${Math.round(choice.confidence * 100)} %` : ""}
          </span>
        ) : null}
      </div>
      {choice?.source === "you" && choice.keyword && choice.matchColumn ? (
        <label className="category-cell__remember">
          <input type="checkbox" checked={choice.remember} onChange={(event) => categorising.setRemember(rowKey, event.target.checked)} />
          <span>
            Rule for “{choice.keyword.length > 40 ? `${choice.keyword.slice(0, 40)}…` : choice.keyword}”
          </span>
        </label>
      ) : null}
    </div>
  );
}
