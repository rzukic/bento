import { useEffect } from "react";
import { createPortal } from "react-dom";
import { ANNOTATION_COLORS, type AnnotationColor, type DashboardAnnotation } from "../../api/dashboards";
import { Icon } from "../../components/common/Icon";
import { Button } from "../../components/ui/Button";

const COLOR_LABELS: Record<AnnotationColor, string> = {
  gray: "Ink",
  blue: "Blue",
  teal: "Teal",
  amber: "Amber",
  rose: "Rose",
  violet: "Violet",
};

function today(): string {
  const now = new Date();
  return new Date(Date.UTC(now.getFullYear(), now.getMonth(), now.getDate())).toISOString().slice(0, 10);
}

/**
 * Dashboard annotations (Step 3.6c, Lens' annotation layers): events such
 * as "Salary raise" on a day, or "Vacation" over a period, drawn on every
 * chart by date — a line in the period of the day, a shaded span for a
 * period. Each chart can hide them under Style.
 */
export function AnnotationsEditor({
  annotations,
  onChange,
  onClose,
}: {
  annotations: DashboardAnnotation[];
  onChange: (annotations: DashboardAnnotation[]) => void;
  onClose: () => void;
}) {
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const update = (index: number, patch: Partial<DashboardAnnotation>) =>
    onChange(annotations.map((annotation, i) => (i === index ? { ...annotation, ...patch } : annotation)));

  const panel = (
    <aside className="widget-editor" aria-label="Dashboard annotations">
      <header className="widget-editor__header">
        <h2>Annotations</h2>
        <Button type="button" variant="ghost" size="sm" onClick={onClose} aria-label="Close annotations">
          <Icon name="x" size={14} />
        </Button>
      </header>
      <div className="widget-editor__body">
        <p className="widget-editor__hint">Events on every chart by date: a day (a salary raise, a move) or a period (a vacation). A chart can hide them under Style.</p>
        {annotations.map((annotation, index) => {
          const label = annotation.label || `annotation ${index + 1}`;
          return (
            <section key={index} className="widget-editor__section">
              <div className="widget-editor__row">
                <label className="widget-editor__field">
                  <span className="widget-editor__label">Label</span>
                  <input
                    className="field__control field__control--compact"
                    value={annotation.label}
                    placeholder="e.g. New release"
                    aria-label={`Label of annotation ${index + 1}`}
                    onChange={(event) => update(index, { label: event.target.value })}
                  />
                </label>
                <Button type="button" variant="ghost" size="sm" aria-label={`Remove ${label}`} onClick={() => onChange(annotations.filter((_, i) => i !== index))}>
                  <Icon name="trash" size={13} />
                </Button>
              </div>
              <div className="widget-editor__row">
                <label className="widget-editor__field">
                  <span className="widget-editor__label">On</span>
                  <input
                    className="field__control field__control--compact"
                    type="date"
                    value={annotation.date}
                    aria-label={`Date of ${label}`}
                    onChange={(event) => update(index, { date: event.target.value })}
                  />
                </label>
                <label className="widget-editor__field">
                  <span className="widget-editor__label">Until (optional)</span>
                  <input
                    className="field__control field__control--compact"
                    type="date"
                    min={annotation.date}
                    value={annotation.to ?? ""}
                    aria-label={`End of ${label}`}
                    onChange={(event) => update(index, { to: event.target.value || undefined })}
                  />
                </label>
                <label className="widget-editor__field widget-editor__field--narrow">
                  <span className="widget-editor__label">Colour</span>
                  <select
                    className="field__control field__control--compact"
                    value={annotation.color ?? "gray"}
                    aria-label={`Colour of ${label}`}
                    onChange={(event) => update(index, { color: event.target.value === "gray" ? undefined : (event.target.value as AnnotationColor) })}
                  >
                    {ANNOTATION_COLORS.map((color) => (
                      <option key={color} value={color}>
                        {COLOR_LABELS[color]}
                      </option>
                    ))}
                  </select>
                </label>
              </div>
            </section>
          );
        })}
        <Button type="button" variant="ghost" size="sm" leadingIcon={<Icon name="plus" size={13} />} onClick={() => onChange([...annotations, { date: today(), label: "" }])}>
          Add annotation
        </Button>
      </div>
      <footer className="widget-editor__footer">
        <Button type="button" variant="primary" onClick={onClose}>
          Done
        </Button>
      </footer>
    </aside>
  );
  return createPortal(panel, document.body);
}
