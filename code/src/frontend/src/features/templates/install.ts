import { createDashboard } from "../../api/dashboards";
import { createRow } from "../../api/data";
import { createForm } from "../../api/forms";
import { createFunction, listFunctionTemplates, listFunctions } from "../../api/functions";
import { applySchemaDraft, getCanonicalSchemaSnapshot, previewSchemaDraft } from "../../api/schema";
import { recordTemplate } from "../../api/templates";
import { useFormsStore } from "../../stores/formsStore";
import { useSchemaStore } from "../../stores/schemaStore";
import type { SchemaModelDocument } from "../../types/api";
import {
  ensureCostFunction,
  financeCashFlow,
  seedExamples,
  transactionForm,
  wealthDashboard,
  withFinanceTables,
  type FinanceNames,
} from "../dashboards/financeTemplate";
import { templateConflicts, uniqueNamer, type TemplateNames } from "./names";
import type { SimpleTemplate } from "./types";

export type InstallOptions = {
  examples: boolean;
  progress: (step: string) => void;
};

/** Checks the names against the model as it is now and applies the template's tables. */
async function addTables(
  names: TemplateNames,
  draftOf: (current: SchemaModelDocument) => SchemaModelDocument,
  progress: InstallOptions["progress"],
  before?: () => Promise<void>,
) {
  progress("Checking the new tables");
  const current = await getCanonicalSchemaSnapshot();
  const taken = templateConflicts(names, current.tables);
  if (taken.length > 0) throw new Error(`These tables exist already: ${taken.join(", ")}. Choose another prefix.`);
  await before?.();
  const draft = draftOf(current);
  const preview = await previewSchemaDraft(draft);
  if (!preview.isValid) throw new Error(preview.errors.map((issue) => issue.message).join(" "));
  progress("Creating the tables");
  await applySchemaDraft(draft);
  return useSchemaStore.getState().load({ force: true });
}

/** Notes the install, so the AI and MCP clients know the template's tables; the tables are there either way. */
async function record(id: string, title: string, names: TemplateNames) {
  try {
    await recordTemplate(id, title, names);
  } catch {
    // An older server without templates: nothing to note.
  }
}

/** Sets up a template that is only data; returns the dashboard to open. */
export async function installSimple(template: SimpleTemplate, names: TemplateNames, { examples, progress }: InstallOptions): Promise<number | undefined> {
  await addTables(
    names,
    (current) => {
      const unique = uniqueNamer(current.relations);
      return {
        ...current,
        tables: [...current.tables, ...template.tables(names)],
        relations: [...current.relations, ...template.relations(names).map((relation) => ({ ...relation, name: unique(relation.name) }))],
      };
    },
    progress,
  );
  if (examples) {
    progress("Adding example rows");
    await template.seed(names, createRow, new Date());
  }
  progress("Creating the dashboards");
  let open: number | undefined;
  for (const { name, dashboard } of template.dashboards(names)) open ??= (await createDashboard(name, dashboard)).id;
  const forms = template.forms(names);
  if (forms.length > 0) {
    progress(forms.length === 1 ? "Creating the form" : "Creating the forms");
    for (const { name, form } of forms) await createForm(name, form, true);
    await useFormsStore.getState().load({ force: true });
  }
  await record(template.id, template.title, names);
  return open;
}

/**
 * The finance template (Step 3.9): its tables, example rows, the cash-flow
 * and wealth dashboards and the Add transaction form. Realised gains call the
 * function avg_cost, created from its template when missing.
 */
export async function installFinance(names: FinanceNames, { examples, progress }: InstallOptions): Promise<number | undefined> {
  // The trades' realised gains call the function avg_cost (Step 4.3b); it must exist before the tables check.
  const snapshot = await addTables(
    names,
    (current) => withFinanceTables(current, names),
    progress,
    () =>
      ensureCostFunction({
        list: listFunctions,
        templates: listFunctionTemplates,
        create: createFunction,
      }),
  );
  if (examples) {
    progress("Adding example rows");
    await seedExamples(names, createRow);
  }
  progress("Creating the dashboards");
  const transactions = snapshot?.tables.find((t) => t.name === names.transactions);
  if (transactions) await createDashboard("Cash flow", financeCashFlow(transactions));
  const wealth = await createDashboard("Wealth", wealthDashboard(names));
  // Step 4.4: quick entry for the phone, in the sidebar and on the cash-flow dashboard.
  progress("Creating the form");
  await createForm("Add transaction", transactionForm(names), true);
  await useFormsStore.getState().load({ force: true });
  await record("finance", "Finance", names);
  return wealth.id;
}
