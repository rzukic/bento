import type { SankeyLabels, SankeyMode } from "../../api/dashboards";
import type { AggregationQuery, QueryResult, QueryRow } from "../../api/query";
import { toNumber, type NumberFormatKind } from "../../lib/format";
import type { GroupPick } from "./drill";
import { labelOf, otherKeyOf } from "./widgetData";

// Step 3.7 (Part 9 R9.5): flows from an aggregation over one to three groups.
// "levels" draws a flow between each pair of consecutive groups (region →
// product → channel); one group flows from the total into its parts.
// "balance" splits the first group by sign: positive values flow into the
// centre, negative ones out of it, with what is left or missing shown — for
// money: income → expenses, the rest saved (labels chosen per widget).

export type SankeyNode = {
  id: string;
  label: string;
  /** What a click on it filters by; null = nothing to open (e.g. "Other", the rest). */
  picks: GroupPick[] | null;
  /** Balance: rows above 0 (in) or below 0 (out) only. */
  sign?: "positive" | "negative";
  kind?: "center" | "rest" | "shortfall";
};

export type SankeyLink = { source: string; target: string; value: number; picks: GroupPick[] | null; sign?: "positive" | "negative" };

export type SankeyModel = { nodes: SankeyNode[]; links: SankeyLink[]; format: NumberFormatKind; mode: SankeyMode };

/** The mode a Sankey widget uses: chosen, else levels. */
export function sankeyMode(mode: SankeyMode | undefined): SankeyMode {
  return mode === "balance" ? "balance" : "levels";
}

/** The names of a balance's centre, rest and shortfall: chosen, else neutral ones. */
export function sankeyLabels(labels: SankeyLabels | undefined): Required<SankeyLabels> {
  return { center: labels?.center || "Total", rest: labels?.rest || "Left over", shortfall: labels?.shortfall || "Shortfall" };
}

type Level = { output: string; column: string; label?: string; other?: string };

function levelsOf(result: QueryResult, query: AggregationQuery | undefined): Level[] {
  return result.meta.columns
    .filter((c) => c.role === "group")
    .map((c) => ({
      output: c.name,
      column: query?.groupBy?.find((g) => (g.as ?? g.column) === c.name)?.column ?? c.name,
      label: result.meta.columns.find((l) => l.role === "label" && l.labelOf === c.name)?.name,
      other: otherKeyOf(result, c.name),
    }));
}

function pick(row: QueryRow, level: Level): GroupPick {
  return { column: level.column, value: row[level.output] ?? null, other: level.other ? row[level.other] === true : false };
}

const picksOrNull = (picks: GroupPick[]) => (picks.some((p) => p.other) ? null : picks);

export function toSankeyModel(
  result: QueryResult,
  query: AggregationQuery | undefined,
  mode: SankeyMode,
  format: NumberFormatKind,
  labels?: SankeyLabels,
): SankeyModel {
  const names = sankeyLabels(labels);
  const levels = levelsOf(result, query);
  const measure = result.meta.columns.find((c) => c.role === "measure");
  const empty: SankeyModel = { nodes: [], links: [], format, mode };
  if (!measure || levels.length === 0) return empty;
  const value = (row: QueryRow) => toNumber(row[measure.name]) ?? 0;
  const label = (row: QueryRow, level: Level) => labelOf(row, level.output, level.label, level.other);

  const nodes = new Map<string, SankeyNode>();
  const links = new Map<string, SankeyLink>();
  const node = (n: SankeyNode) => {
    if (!nodes.has(n.id)) nodes.set(n.id, n);
    return n.id;
  };
  const flow = (source: string, target: string, amount: number, picks: GroupPick[] | null, sign?: SankeyLink["sign"]) => {
    const key = `${source}\u0000${target}`;
    const link = links.get(key) ?? { source, target, value: 0, picks, sign };
    link.value += amount;
    links.set(key, link);
  };

  if (mode === "levels" && levels.length === 1) {
    // One level: the total flows into its parts.
    const [only] = levels;
    const total = node({ id: "center", label: names.center, picks: [], kind: "center" });
    for (const row of result.data) {
      const amount = value(row);
      if (amount === 0) continue;
      const part = node({ id: `0:${label(row, only)}`, label: label(row, only), picks: picksOrNull([pick(row, only)]) });
      flow(total, part, amount, picksOrNull([pick(row, only)]));
    }
  } else if (mode === "levels") {
    for (const row of result.data) {
      const amount = value(row);
      if (amount === 0) continue;
      for (let i = 0; i + 1 < levels.length; i++) {
        const a = levels[i];
        const b = levels[i + 1];
        const from = node({ id: `${i}:${label(row, a)}`, label: label(row, a), picks: picksOrNull([pick(row, a)]) });
        const to = node({ id: `${i + 1}:${label(row, b)}`, label: label(row, b), picks: picksOrNull([pick(row, b)]) });
        flow(from, to, amount, picksOrNull([pick(row, a), pick(row, b)]));
      }
    }
  } else {
    // Balance: the first group's totals decide the side; a second group details what flows out.
    const [first, second] = levels;
    const totals = new Map<string, { amount: number; row: QueryRow }>();
    for (const row of result.data) {
      const key = label(row, first);
      const entry = totals.get(key) ?? { amount: 0, row };
      entry.amount += value(row);
      totals.set(key, entry);
    }
    const income = [...totals.entries()].filter(([, t]) => t.amount > 0).sort((a, b) => b[1].amount - a[1].amount);
    const expenses = [...totals.entries()].filter(([, t]) => t.amount < 0).sort((a, b) => a[1].amount - b[1].amount);
    const earned = income.reduce((sum, [, t]) => sum + t.amount, 0);
    const spent = -expenses.reduce((sum, [, t]) => sum + t.amount, 0);
    if (earned === 0 && spent === 0) return empty;

    const center = node({ id: "center", label: names.center, picks: [], kind: "center" });
    for (const [name, total] of income) {
      const id = node({ id: `in:${name}`, label: name, picks: picksOrNull([pick(total.row, first)]), sign: "positive" });
      flow(id, center, total.amount, picksOrNull([pick(total.row, first)]), "positive");
    }
    if (spent > earned) flow(node({ id: "shortfall", label: names.shortfall, picks: null, kind: "shortfall" }), center, spent - earned, null);
    for (const [name, total] of expenses) {
      const id = node({ id: `out:${name}`, label: name, picks: picksOrNull([pick(total.row, first)]), sign: "negative" });
      flow(center, id, -total.amount, picksOrNull([pick(total.row, first)]), "negative");
    }
    if (earned > spent) flow(center, node({ id: "rest", label: names.rest, picks: null, kind: "rest" }), earned - spent, null);

    if (second) {
      const expenseNames = new Set(expenses.map(([name]) => name));
      for (const row of result.data) {
        const parent = label(row, first);
        const amount = value(row);
        if (!expenseNames.has(parent) || amount >= 0) continue;
        const picks = picksOrNull([pick(row, first), pick(row, second)]);
        const id = node({ id: `sub:${parent}\u0000${label(row, second)}`, label: label(row, second), picks, sign: "negative" });
        flow(`out:${parent}`, id, -amount, picks, "negative");
      }
    }
  }

  const kept = [...links.values()].map((link) => ({ ...link, value: Math.abs(link.value) })).filter((link) => link.value > 0);
  const used = new Set(kept.flatMap((link) => [link.source, link.target]));
  return { nodes: [...nodes.values()].filter((n) => used.has(n.id)), links: kept, format, mode };
}
