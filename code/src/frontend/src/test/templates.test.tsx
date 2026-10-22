import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { DashboardWidget } from "../api/dashboards";
import type { FormField } from "../api/forms";
import { appointmentsTemplate } from "../features/templates/appointmentsTemplate";
import { habitsTemplate } from "../features/templates/habitsTemplate";
import { suggestTemplatePrefix, templateConflicts, templateNames } from "../features/templates/names";
import { projectsTemplate } from "../features/templates/projectsTemplate";
import { TEMPLATES } from "../features/templates/registry";
import type { SimpleTemplate } from "../features/templates/types";
import type { ModelTable, SchemaModelDocument, SchemaTable } from "../types/api";

const schemaApi = vi.hoisted(() => ({ getCanonicalSchemaSnapshot: vi.fn(), previewSchemaDraft: vi.fn(), applySchemaDraft: vi.fn() }));
const dashboardsApi = vi.hoisted(() => ({ createDashboard: vi.fn() }));
const dataApi = vi.hoisted(() => ({ createRow: vi.fn() }));
const formsApi = vi.hoisted(() => ({ createForm: vi.fn(), listForms: vi.fn() }));
const templatesApi = vi.hoisted(() => ({ listInstalledTemplates: vi.fn(), recordTemplate: vi.fn() }));
vi.mock("../api/schema", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/schema")>()), ...schemaApi }));
vi.mock("../api/dashboards", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/dashboards")>()), ...dashboardsApi }));
vi.mock("../api/data", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/data")>()), ...dataApi }));
vi.mock("../api/forms", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/forms")>()), ...formsApi }));
vi.mock("../api/templates", () => templatesApi);

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

const SIMPLE: SimpleTemplate[] = [projectsTemplate, habitsTemplate, appointmentsTemplate];

/** Every column a widget names, with the table it must be in. */
function widgetColumns(widget: DashboardWidget): { table: string; column: string }[] {
  const data = widget.data;
  if (!data) return [];
  if (data.target) {
    const t = data.target;
    return [
      { table: t.source, column: t.date },
      { table: t.source, column: t.group },
    ];
  }
  const source = data.query!.source;
  return [
    ...(data.query!.groupBy ?? []).map((g) => g.column),
    ...(data.query!.measures ?? []).flatMap((m) => (m.column ? [m.column] : [])),
    ...Object.keys(data.query!.filter ?? {}),
    ...(data.rows?.columns ?? []),
    ...(data.dateColumn ? [data.dateColumn] : []),
  ].map((column) => ({ table: source, column }));
}

describe.each(SIMPLE.map((t) => [t.id, t] as const))("template %s", (_id, template) => {
  const names = templateNames(template.base, "x_");
  const tables = template.tables(names);
  const byName = new Map(tables.map((t) => [t.name, t]));
  const has = (table: string, column: string) => byName.get(table)?.columns.some((c) => c.name === column) ?? false;

  it("makes a table per name, each with an id, and links only its own tables", () => {
    expect(tables.map((t) => t.name).sort()).toEqual(Object.values(names).sort());
    for (const table of tables) expect(table.columns[0]).toMatchObject({ name: "id", primaryKey: true });
    for (const column of tables.flatMap((t) => t.columns)) {
      if (column.type === "reference") expect(Object.values(names)).toContain(column.references);
      // A computed column is not named like a table its formula reads: the name would point at the column itself.
      for (const table of Object.values(names))
        if (column.formula && new RegExp(`(^|[^_a-z])${table}([^_a-z]|$)`).test(column.formula))
          expect(`${column.name}: ${(column.displayName ?? "").toLowerCase()}`).not.toBe(`${column.name}: ${table.replace(/^x_/, "").replace(/_/g, " ")}`);
      // Formulas over related rows name the prefixed tables.
      for (const plain of Object.values(template.base)) expect(column.formula ?? "").not.toMatch(new RegExp(`(^|[^_a-z])${plain}\\.`));
    }
    for (const relation of template.relations(names)) {
      expect(has(relation.to, relation.column!)).toBe(true);
      expect(byName.has(relation.from)).toBe(true);
    }
  });

  it("builds dashboards and forms from columns that exist", () => {
    for (const { dashboard } of template.dashboards(names)) {
      for (const widget of dashboard.widgets) {
        for (const { table, column } of widgetColumns(widget))
          expect(`${widget.id}: ${table}.${column} ${has(table, column)}`).toBe(`${widget.id}: ${table}.${column} true`);
        expect(widget.position.x + widget.position.w).toBeLessThanOrEqual(12);
      }
      for (const filter of dashboard.filters ?? []) expect(tables.some((t) => has(t.name, filter.column))).toBe(true);
      expect(new Set(dashboard.widgets.map((w) => w.id)).size).toBe(dashboard.widgets.length);
    }
    for (const { form } of template.forms(names)) {
      const fields = form.fields.filter((f): f is FormField => "column" in f).map((f) => f.column);
      for (const column of fields) expect(has(form.table, column)).toBe(true);
      // Every required column is in the form (the server refuses one without).
      const required = (byName.get(form.table) as SchemaTable).columns.filter(
        (c) => c.nullable === false && !c.primaryKey && c.type !== "computed" && c.default === undefined,
      );
      for (const column of required) expect(fields).toContain(column.name);
    }
  });

  it("seeds example rows into its own columns, linked by the ids it gets", async () => {
    let next = 100;
    const created: [string, Record<string, unknown>][] = [];
    await template.seed(
      names,
      async (table, row) => {
        created.push([table, row]);
        return { ...row, id: next++ };
      },
      new Date(Date.UTC(2026, 9, 21)),
    );
    expect(created.length).toBeGreaterThan(5);
    for (const [table, row] of created) {
      for (const column of Object.keys(row)) expect(`${table}.${column} ${has(table, column)}`).toBe(`${table}.${column} true`);
      const definition = byName.get(table)!;
      for (const column of definition.columns.filter((c) => c.type === "reference" && c.name in row)) expect(row[column.name]).toBeGreaterThanOrEqual(100);
    }
  });
});

describe("templates", () => {
  it("are listed with finance as one of several", () => {
    expect(TEMPLATES.map((t) => t.id)).toEqual(["projects-hours", "habits-health", "appointments", "finance"]);
  });

  it("prefix the names only when one is taken", () => {
    const base = projectsTemplate.base;
    expect(suggestTemplatePrefix(base, projectsTemplate.prefixes, [{ name: "customers" }])).toBe("");
    expect(suggestTemplatePrefix(base, projectsTemplate.prefixes, [{ name: "Projects" }])).toBe("work_");
    expect(suggestTemplatePrefix(base, ["a_"], [{ name: "projects" }, { name: "a_projects" }])).toBe("a_2_");
    expect(templateConflicts(templateNames(base, "work_"), [{ name: "work_time_entries" }])).toEqual(["work_time_entries"]);
  });

  it("sets one up from the gallery, notes it for the workspace and opens its dashboard", async () => {
    const { TemplateGallery } = await import("../features/templates/TemplateGallery");
    const { useSchemaStore } = await import("../stores/schemaStore");
    const { useFormsStore } = await import("../stores/formsStore");
    const existing: SchemaModelDocument = {
      tables: [{ name: "projects", columns: [{ name: "id", type: "integer", primaryKey: true }] }],
      relations: [{ name: "work_projects_work_time_entries", type: "1:N", from: "a", to: "b", column: "c" }],
    } as SchemaModelDocument;
    templatesApi.listInstalledTemplates.mockResolvedValue([{ id: "habits-health", title: "Habits & health", installedAt: "", tables: {} }]);
    templatesApi.recordTemplate.mockResolvedValue({});
    schemaApi.getCanonicalSchemaSnapshot.mockResolvedValue(existing);
    schemaApi.previewSchemaDraft.mockResolvedValue({ isValid: true, errors: [] });
    schemaApi.applySchemaDraft.mockResolvedValue({});
    dataApi.createRow.mockImplementation(async (_t: string, row: Record<string, unknown>) => ({ ...row, id: 1 }));
    dashboardsApi.createDashboard.mockImplementation(async (name: string) => ({ id: 12, name }));
    formsApi.createForm.mockResolvedValue({ id: 3 });
    useSchemaStore.setState({ load: vi.fn(async () => existing as never) });
    useFormsStore.setState({ load: vi.fn(async () => undefined as never) });
    const user = userEvent.setup();
    render(
      <MemoryRouter initialEntries={["/"]}>
        <Routes>
          <Route path="/" element={<TemplateGallery tables={existing.tables as unknown as ModelTable[]} onError={() => {}} />} />
          <Route path="/dashboards/:id" element={<p>opened</p>} />
        </Routes>
      </MemoryRouter>,
    );
    expect(await within(screen.getByRole("region", { name: "Habits & health" })).findByText("Installed")).toBeInTheDocument();
    const card = within(screen.getByRole("region", { name: "Projects & hours" }));
    expect(card.getByLabelText("Table name prefix")).toHaveValue("work_");
    await user.click(card.getByLabelText("Example rows"));
    await user.click(card.getByRole("button", { name: "Set up" }));
    expect(await screen.findByText("opened")).toBeInTheDocument();

    const draft = schemaApi.applySchemaDraft.mock.calls[0][0] as SchemaModelDocument;
    expect(draft.tables.map((t) => t.name)).toEqual(["projects", "work_projects", "work_time_entries"]);
    // A relation name that is taken gets a number.
    expect(draft.relations.at(-1)?.name).toBe("work_projects_work_time_entries_2");
    expect(dataApi.createRow).not.toHaveBeenCalled();
    expect(dashboardsApi.createDashboard).toHaveBeenCalledWith("Hours", expect.objectContaining({ filters: [{ column: "project_id", label: "Project" }] }));
    expect(formsApi.createForm).toHaveBeenCalledWith("Log hours", expect.objectContaining({ table: "work_time_entries", pinned: true }), true);
    await waitFor(() =>
      expect(templatesApi.recordTemplate).toHaveBeenCalledWith("projects-hours", "Projects & hours", {
        projects: "work_projects",
        time_entries: "work_time_entries",
      }),
    );
  });
});
