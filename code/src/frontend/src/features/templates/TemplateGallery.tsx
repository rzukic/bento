import { useEffect, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { ApiClientError } from "../../api/client";
import { listInstalledTemplates } from "../../api/templates";
import { Button } from "../../components/ui/Button";
import type { ModelTable } from "../../types/api";
import { suggestTemplatePrefix, templateConflicts, templateNames } from "./names";
import { TEMPLATES, type TemplateEntry } from "./registry";
import "./Templates.css";

/**
 * Ready-made setups (projects & hours, habits & health, appointments,
 * finance): new tables, dashboards and forms, optionally with example rows.
 * Nothing existing changes; a taken table name gets a prefix.
 */
export function TemplateGallery({ tables, onError }: { tables: ModelTable[]; onError: (message: string | null) => void }) {
  const [installed, setInstalled] = useState<Set<string>>(new Set());
  useEffect(() => {
    let active = true;
    listInstalledTemplates()
      .then((list) => active && setInstalled(new Set(list.map((t) => t.id))))
      .catch(() => {});
    return () => {
      active = false;
    };
  }, []);
  return (
    <div className="templates" aria-label="Templates">
      {TEMPLATES.map((template) => (
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
  template: TemplateEntry;
  tables: ModelTable[];
  installed: boolean;
  onError: (message: string | null) => void;
}) {
  const navigate = useNavigate();
  const [prefix, setPrefix] = useState(() => suggestTemplatePrefix(template.base, template.prefixes, tables));
  const [examples, setExamples] = useState(true);
  const [progress, setProgress] = useState<string | null>(null);
  const names = useMemo(() => templateNames(template.base, prefix), [template.base, prefix]);
  const conflicts = templateConflicts(names, tables);
  const needsPrefix = templateConflicts(template.base, tables).length > 0;

  const setUp = async () => {
    onError(null);
    try {
      const open = await template.install(names, {
        examples,
        progress: setProgress,
      });
      navigate(open !== undefined ? `/dashboards/${open}` : "/data");
    } catch (cause) {
      onError(cause instanceof ApiClientError || cause instanceof Error ? cause.message : `${template.title} could not be set up.`);
    } finally {
      setProgress(null);
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
        {progress ? (
          <span className="templates__description" role="status">
            {progress}…
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
        <label className="templates__check">
          <input type="checkbox" checked={examples} onChange={(event) => setExamples(event.target.checked)} />
          Example rows
        </label>
        <Button type="button" variant="primary" disabled={progress !== null || conflicts.length > 0} onClick={() => void setUp()}>
          Set up
        </Button>
      </div>
      {conflicts.length > 0 ? <span className="templates__description">Taken: {conflicts.join(", ")}</span> : null}
    </section>
  );
}
