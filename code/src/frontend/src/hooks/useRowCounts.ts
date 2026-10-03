import { useEffect, useState } from "react";
import { fetchTableRows } from "../api/data";
import { useRowCountStore } from "../stores/rowCountStore";

/**
 * Unfiltered row counts per table, fetched one lightweight page at a time.
 * Counts are decorative: a failed count stays null (unknown). Changing
 * `refreshKey` fetches them again (e.g. after rows were added or deleted).
 */
export function useRowCounts(
  tableNames: string[],
  refreshKey?: unknown,
): Record<string, number | null> {
  const [counts, setCounts] = useState<Record<string, number | null>>({});
  const key = tableNames.join("\u0000");

  useEffect(() => {
    if (tableNames.length === 0) {
      setCounts({});
      return;
    }
    let cancelled = false;
    setCounts((prev) => Object.fromEntries(tableNames.map((name) => [name, prev[name] ?? null])));
    for (const name of tableNames) {
      fetchTableRows(name, { page: 1, perPage: 1, sort: [], search: "", filters: [] })
        .then((response) => {
          useRowCountStore.getState().setCount(name, response.meta.total);
          if (!cancelled) setCounts((prev) => ({ ...prev, [name]: response.meta.total }));
        })
        .catch(() => {
          // Unknown count; nothing to show.
        });
    }
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, refreshKey]);

  return counts;
}
