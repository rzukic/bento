import { useEffect } from "react";
import { createPortal } from "react-dom";
import { Link } from "react-router-dom";
import type { DashboardDocument } from "../../api/dashboards";
import type { SavedForm } from "../../api/forms";
import { Icon } from "../../components/common/Icon";
import { Button } from "../../components/ui/Button";
import { automaticForms } from "../forms/quickAdd";

type Mode = "auto" | "chosen" | "none";

/**
 * Which form buttons a dashboard shows (Step 4.5): automatically the forms
 * in Quick add for the tables it shows, the forms chosen here, or none.
 */
export function QuickAddEditor({
  document,
  forms,
  onChange,
  onClose,
}: {
  document: DashboardDocument;
  forms: SavedForm[];
  onChange: (quickAdd: DashboardDocument["quickAdd"]) => void;
  onClose: () => void;
}) {
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => event.key === "Escape" && onClose();
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const value = document.quickAdd;
  const mode: Mode = value === "none" ? "none" : Array.isArray(value) ? "chosen" : "auto";
  const chosen = Array.isArray(value) ? value : [];
  const automatic = automaticForms(forms, document);

  const setMode = (next: Mode) => {
    if (next === "auto") onChange(undefined);
    else if (next === "none") onChange("none");
    // Choosing starts from what shows now.
    else onChange(automatic.map((f) => f.id));
  };
  const toggle = (id: number, on: boolean) => onChange(on ? [...chosen, id] : chosen.filter((c) => c !== id));

  const panel = (
    <aside className="widget-editor" aria-label="Quick add on this dashboard">
      <header className="widget-editor__header">
        <h2>Quick add</h2>
        <Button type="button" variant="ghost" size="sm" onClick={onClose} aria-label="Close quick add">
          <Icon name="x" size={14} />
        </Button>
      </header>
      <div className="widget-editor__body">
        <p className="widget-editor__hint">Buttons in the dashboard's header that open a form, e.g. Add transaction. Viewers do not see them.</p>
        <section className="widget-editor__section" role="radiogroup" aria-label="Form buttons">
          <label className="quick-add__choice">
            <input type="radio" name="quick-add" checked={mode === "auto"} onChange={() => setMode("auto")} />
            <span>
              Automatic
              <span className="widget-editor__hint">
                {" "}
                — forms in Quick add for the tables this dashboard shows{automatic.length > 0 ? `: ${automatic.map((f) => f.name).join(", ")}` : " (none now)"}
              </span>
            </span>
          </label>
          <label className="quick-add__choice">
            <input type="radio" name="quick-add" checked={mode === "chosen"} onChange={() => setMode("chosen")} />
            <span>These forms</span>
          </label>
          {mode === "chosen" ? (
            forms.length === 0 ? (
              <p className="widget-editor__hint">
                No forms yet — make one under <Link to="/forms">Forms</Link>.
              </p>
            ) : (
              <ul className="quick-add__forms" aria-label="Forms">
                {forms.map((form) => (
                  <li key={form.id}>
                    <label className="quick-add__choice">
                      <input type="checkbox" checked={chosen.includes(form.id)} onChange={(event) => toggle(form.id, event.target.checked)} />
                      <span>{form.name}</span>
                    </label>
                  </li>
                ))}
              </ul>
            )
          ) : null}
          <label className="quick-add__choice">
            <input type="radio" name="quick-add" checked={mode === "none"} onChange={() => setMode("none")} />
            <span>None</span>
          </label>
        </section>
      </div>
      <footer className="widget-editor__footer">
        <Button type="button" variant="primary" onClick={onClose}>
          Done
        </Button>
      </footer>
    </aside>
  );
  return createPortal(panel, window.document.body);
}
