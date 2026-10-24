import { useEffect, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { ApiClientError } from "../../api/client";
import { installTemplate, listInstalledTemplates, listTemplateCatalog, type TemplateSummary } from "../../api/templates";
import { Button } from "../../components/ui/Button";
import { useFormsStore } from "../../stores/formsStore";
import { useSchemaStore } from "../../stores/schemaStore";
import type { ModelTable } from "../../types/api";
import { suggestTemplatePrefix, templateConflicts, templateNames } from "./names";
import "./Templates.css";

/**
 * Ready-made setups (projects & hours, habits & health, appointments,
 * finance): new tables, dashboards and forms, optionally with example rows.
 * Nothing existing changes; a taken table name gets a prefix. The recipes
 * and the install live on the server (Step 5.15) — Claude installs the same way.
 */
export function TemplateGallery({ tables, onError }: { tables: ModelTable[]; onError: (message: string | null) => void }) {
  const [catalog, setCatalog] = useState<TemplateSummary[] | null>(null);
  const [installed, setInstalled] = useState<Set<string>>(new Set());
  useEffect(() => {
    let active = true;
    listTemplateCatalog()
      .then((list) => active && setCatalog(list))
      .catch(() => active && setCatalog([]));
    listInstalledTemplates()
      .then((list) => active && setInstalled(new Set(list.map((t) => t.id))))
      .catch(() => {});
    return () => {
      active = false;
    };
  }, []);
  if (!catalog || catalog.length === 0) return null;
  return (
    <div className="templates" aria-label="Templates">
      {catalog.map((template) => (
        <TemplateCard key={template.id} template={template} tables={tables} installed={installed.has(template.id)} onError={onError} />
      ))}
    </div>
  );
}

function TemplateCard({
  template,
  tables,
  installed,
  onError,
}: {
  template: TemplateSummary;
  tables: ModelTable[];
  installed: boolean;
  onError: (message: string | null) => void;
}) {
  const navigate = useNavigate();
  const [prefix, setPrefix] = useState(() => suggestTemplatePrefix(template.base, template.prefixes, tables));
  const [examples, setExamples] = useState(true);
  const [busy, setBusy] = useState(false);
  const names = useMemo(() => templateNames(template.base, prefix), [template.base, prefix]);
  const conflicts = templateConflicts(names, tables);
  const needsPrefix = templateConflicts(template.base, tables).length > 0;

  const setUp = async () => {
    onError(null);
    setBusy(true);
    try {
      const result = await installTemplate(template.id, { prefix: prefix || undefined, examples });
      await useSchemaStore.getState().load({ force: true });
      await useFormsStore.getState().load({ force: true });
      navigate(result.open !== null ? `/dashboards/${result.open}` : "/data");
    } catch (cause) {
      onError(cause instanceof ApiClientError || cause instanceof Error ? cause.message : `${template.title} could not be set up.`);
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="templates__card" aria-label={template.title}>
      <div className="templates__text">
        <span className="templates__title">
          {template.title}
          {installed ? <span className="templates__installed">Installed</span> : null}
        </span>
        <span className="templates__description">{template.description}</span>
        {busy ? (
          <span className="templates__description" role="status">
            Setting up {template.title}…
          </span>
        ) : null}
      </div>
      <div className="templates__actions">
        {needsPrefix ? (
          <label className="templates__prefix">
            <span>Table names start with</span>
            <input
              className="field__control field__control--compact mono"
              aria-label="Table name prefix"
              value={prefix}
              onChange={(event) => setPrefix(event.target.value)}
            />
          </label>
        ) : null}
        {template.exampleRows > 0 ? (
          <label className="templates__check">
            <input type="checkbox" checked={examples} onChange={(event) => setExamples(event.target.checked)} />
            Example rows
          </label>
        ) : null}
        <Button type="button" variant="primary" disabled={busy || conflicts.length > 0} onClick={() => void setUp()}>
          Set up
        </Button>
      </div>
      {conflicts.length > 0 ? <span className="templates__description">Taken: {conflicts.join(", ")}</span> : null}
    </section>
  );
}
