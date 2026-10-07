import type { DashboardDocument } from "../../api/dashboards";
import type { SavedForm } from "../../api/forms";

/**
 * Pinned forms for a dashboard (Step 4.4): those adding rows to a table the
 * dashboard shows — as a widget's source, or through a link in a measure
 * (`SUM(transactions.amount)` on accounts).
 */
export function formsForDashboard(forms: SavedForm[] | null, document: DashboardDocument | undefined): SavedForm[] {
  if (!forms || !document) return [];
  const sources = new Set<string>();
  const formulas: string[] = [];
  for (const widget of document.widgets) {
    const query = widget.data?.query;
    if (!query) continue;
    sources.add(query.source.toLowerCase());
    for (const measure of query.measures ?? []) if (measure.formula) formulas.push(measure.formula.toLowerCase());
  }
  const mentioned = (table: string) => formulas.some((f) => new RegExp(`(^|[^a-z0-9_])${table.replace(/[^a-z0-9_]/g, "")}\\.`).test(f));
  return forms.filter((form) => form.form.pinned && (sources.has(form.table.toLowerCase()) || mentioned(form.table.toLowerCase())));
}
