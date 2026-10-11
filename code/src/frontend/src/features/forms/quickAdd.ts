import type { DashboardDocument } from "../../api/dashboards";
import type { SavedForm } from "../../api/forms";

/**
 * The form buttons of a dashboard (Step 4.4, chosen per dashboard since 4.5):
 * by default the pinned forms adding rows to a table the dashboard shows — as
 * a widget's source, or through a link in a measure (`SUM(transactions.amount)`
 * on accounts); or the forms the dashboard names; or none.
 */
export function formsForDashboard(forms: SavedForm[] | null, document: DashboardDocument | undefined): SavedForm[] {
  if (!forms || !document) return [];
  // The dashboard's own choice wins: none, or these forms in this order.
  if (document.quickAdd === "none") return [];
  if (Array.isArray(document.quickAdd)) return document.quickAdd.flatMap((id) => forms.filter((f) => f.id === id));
  return automaticForms(forms, document);
}

/** "auto": pinned forms adding rows to a table the dashboard shows. */
export function automaticForms(forms: SavedForm[], document: DashboardDocument): SavedForm[] {
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
