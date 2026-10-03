import { useEffect, useState } from "react";
import { fetchTableRows } from "../../../api/data";
import { useSchemaStore } from "../../../stores/schemaStore";
import { DEFAULT_QUERY } from "../../../types/explorer";
import type { ModelColumn, ModelTable } from "../../../types/api";
import {
  findTable,
  getDisplayColumnForTable,
  getReferencePrimaryKey,
  getSecondaryColumnForTable,
  getReferenceTableName,
} from "./editors/referenceMetadata";

/** Where the secondary label values sit inside a label map. */
const SECONDARY = Symbol("secondary-labels");

/**
 * Column name → id → label. The record label's secondary values (when the
 * referenced table sets one) ride along under a private key; read them with
 * lookupReferenceSecondary.
 */
export type ReferenceLabelMap = Record<string, Record<string, string>> & {
  [SECONDARY]?: Record<string, Record<string, string>>;
};

type FkPlan = {
  columnName: string;
  refTable: string;
  pkColumn: string;
  displayColumn: string;
  secondaryColumn: string | null;
  ids: string[];
};

function planFkLookups(
  table: ModelTable,
  rows: Record<string, unknown>[],
  snapshot: ReturnType<typeof useSchemaStore.getState>["snapshot"],
): FkPlan[] {
  const plans: FkPlan[] = [];
  for (const column of table.columns) {
    if (!column.references && column.type !== "relation") continue;
    const refTable = getReferenceTableName(column);
    if (!refTable) continue;
    const refTableModel = findTable(snapshot, refTable);
    const displayColumn = getDisplayColumnForTable(refTableModel);
    const secondaryColumn = getSecondaryColumnForTable(refTableModel);
    const pkColumn = getReferencePrimaryKey(column);
    const ids = new Set<string>();
    for (const row of rows) {
      const v = row[column.name];
      if (v === null || v === undefined || v === "") continue;
      ids.add(String(v));
    }
    if (ids.size === 0) continue;
    plans.push({
      columnName: column.name,
      refTable,
      pkColumn,
      displayColumn,
      secondaryColumn,
      ids: [...ids],
    });
  }
  return plans;
}

/**
 * Resolve display labels for FK ids visible in the grid. For each FK column,
 * batches a single `filter[pk][in]=id1,id2,…` request to the referenced
 * table and indexes the results by id. Returns a map keyed by column name
 * then id; missing entries fall back to the raw id at the call site.
 */
export function useReferenceLabels(
  table: ModelTable,
  rows: Record<string, unknown>[],
): ReferenceLabelMap {
  const snapshot = useSchemaStore((s) => s.snapshot);
  const [labels, setLabels] = useState<ReferenceLabelMap>({});

  // Stable signature so we re-fetch only when the visible id-set changes.
  const plans = planFkLookups(table, rows, snapshot);
  const signature = plans
    .map(
      (p) =>
        `${p.columnName}:${p.refTable}:${p.displayColumn}:${p.secondaryColumn ?? ""}:${p.ids.slice().sort().join(",")}`,
    )
    .join("|");

  useEffect(() => {
    if (plans.length === 0) {
      setLabels((prev) => (Object.keys(prev).length === 0 ? prev : {}));
      return;
    }
    let cancelled = false;
    // Each FK column on its own: one failed lookup must not drop the others.
    void Promise.allSettled(
      plans.map(async (plan) => {
        const res = await fetchTableRows(plan.refTable, {
          ...DEFAULT_QUERY,
          perPage: Math.max(plan.ids.length, 1),
          filters: [{ column: plan.pkColumn, op: "in", value: plan.ids.join(",") }],
        });
        const byId: Record<string, string> = {};
        const secondaryById: Record<string, string> = {};
        for (const row of res.data) {
          const pk = row[plan.pkColumn];
          if (pk === null || pk === undefined) continue;
          const labelRaw = row[plan.displayColumn];
          byId[String(pk)] =
            labelRaw === null || labelRaw === undefined ? String(pk) : String(labelRaw);
          const secondaryRaw = plan.secondaryColumn ? row[plan.secondaryColumn] : null;
          if (secondaryRaw !== null && secondaryRaw !== undefined && secondaryRaw !== "") {
            secondaryById[String(pk)] = String(secondaryRaw);
          }
        }
        return [plan.columnName, byId, secondaryById] as const;
      }),
    )
      .then((results) => {
        if (cancelled) return;
        const fulfilled = results.flatMap((result) =>
          result.status === "fulfilled" ? [result.value] : [],
        );
        const next: ReferenceLabelMap = Object.fromEntries(
          fulfilled.map(([column, byId]) => [column, byId]),
        );
        next[SECONDARY] = Object.fromEntries(
          fulfilled.map(([column, , secondaryById]) => [column, secondaryById]),
        );
        setLabels(next);
      })
      .catch(() => {
        /* ignore — chip falls back to id */
      });
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [signature]);

  return labels;
}

export function lookupReferenceLabel(
  labels: ReferenceLabelMap,
  column: ModelColumn,
  value: unknown,
): string | null {
  if (value === null || value === undefined || value === "") return null;
  const id = String(value);
  return labels[column.name]?.[id] ?? null;
}

/** The record label's secondary value for an FK value, when there is one. */
export function lookupReferenceSecondary(
  labels: ReferenceLabelMap,
  column: ModelColumn,
  value: unknown,
): string | null {
  if (value === null || value === undefined || value === "") return null;
  return labels[SECONDARY]?.[column.name]?.[String(value)] ?? null;
}
