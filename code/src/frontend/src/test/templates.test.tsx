import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { TemplateSummary } from "../api/templates";
import { suggestTemplatePrefix, templateConflicts, templateNames } from "../features/templates/names";
import { useFormsStore } from "../stores/formsStore";
import { useSchemaStore } from "../stores/schemaStore";
import type { ModelTable } from "../types/api";

const templatesApi = vi.hoisted(() => ({ listTemplateCatalog: vi.fn(), listInstalledTemplates: vi.fn(), installTemplate: vi.fn() }));
vi.mock("../api/templates", () => templatesApi);

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

const projects: TemplateSummary = {
  id: "projects-hours",
  title: "Projects & hours",
  description: "Projects and the time spent on them.",
  base: { projects: "projects", time_entries: "time_entries" },
  prefixes: ["work_", "my_"],
  dashboards: ["Hours"],
  forms: ["Log hours"],
  exampleRows: 41,
};
const habits: TemplateSummary = { ...projects, id: "habits-health", title: "Habits & health", base: { habits: "habits" }, prefixes: ["health_"] };

describe("templates", () => {
  it("prefix the names only when one is taken", () => {
    const base = projects.base;
    expect(suggestTemplatePrefix(base, projects.prefixes, [{ name: "customers" }])).toBe("");
    expect(suggestTemplatePrefix(base, projects.prefixes, [{ name: "Projects" }])).toBe("work_");
    expect(suggestTemplatePrefix(base, ["a_"], [{ name: "projects" }, { name: "a_projects" }])).toBe("a_2_");
    expect(templateConflicts(templateNames(base, "work_"), [{ name: "work_time_entries" }])).toEqual(["work_time_entries"]);
  });

  it("lists the server's templates and sets one up there, with a prefix, then opens its dashboard", async () => {
    const { TemplateGallery } = await import("../features/templates/TemplateGallery");
    templatesApi.listTemplateCatalog.mockResolvedValue([projects, habits]);
    templatesApi.listInstalledTemplates.mockResolvedValue([{ id: "habits-health", title: "Habits & health", installedAt: "", tables: {} }]);
    templatesApi.installTemplate.mockResolvedValue({ template: "projects-hours", tables: {}, dashboards: [{ id: 12, name: "Hours" }], forms: [], exampleRows: 0, open: 12 });
    const loadSchema = vi.fn(async () => null as never);
    const loadForms = vi.fn(async () => undefined as never);
    useSchemaStore.setState({ load: loadSchema });
    useFormsStore.setState({ load: loadForms });
    const user = userEvent.setup();
    const tables = [{ name: "projects", columns: [] }] as unknown as ModelTable[];
    render(
      <MemoryRouter initialEntries={["/"]}>
        <Routes>
          <Route path="/" element={<TemplateGallery tables={tables} onError={() => {}} />} />
          <Route path="/dashboards/:id" element={<p>opened</p>} />
        </Routes>
      </MemoryRouter>,
    );
    expect(await within(await screen.findByRole("region", { name: "Habits & health" })).findByText("Installed")).toBeInTheDocument();
    const card = within(screen.getByRole("region", { name: "Projects & hours" }));
    expect(card.getByLabelText("Table name prefix")).toHaveValue("work_");
    await user.click(card.getByLabelText("Example rows"));
    await user.click(card.getByRole("button", { name: "Set up" }));
    expect(await screen.findByText("opened")).toBeInTheDocument();
    expect(templatesApi.installTemplate).toHaveBeenCalledWith("projects-hours", { prefix: "work_", examples: false });
    // The new tables and forms show up at once.
    await waitFor(() => expect(loadSchema).toHaveBeenCalledWith({ force: true }));
    expect(loadForms).toHaveBeenCalledWith({ force: true });
  });

  it("says why a template could not be set up", async () => {
    const { TemplateGallery } = await import("../features/templates/TemplateGallery");
    const { ApiClientError } = await import("../api/client");
    templatesApi.listTemplateCatalog.mockResolvedValue([projects]);
    templatesApi.listInstalledTemplates.mockResolvedValue([]);
    templatesApi.installTemplate.mockRejectedValue(new ApiClientError("These tables exist already: projects.", 409, "CONFLICT"));
    const onError = vi.fn();
    const user = userEvent.setup();
    render(
      <MemoryRouter>
        <TemplateGallery tables={[]} onError={onError} />
      </MemoryRouter>,
    );
    await user.click(await screen.findByRole("button", { name: "Set up" }));
    await waitFor(() => expect(onError).toHaveBeenLastCalledWith("These tables exist already: projects."));
  });
});
