import { useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { ApiClientError } from "../../api/client";
import { createRow } from "../../api/data";
import { isSection, type FormDefinition } from "../../api/forms";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import type { ModelTable } from "../../types/api";
import { FormFieldInput } from "./FormFields";
import { buildRow, columnLabel, fieldKind, formFields, initialState, isFormWritable, missingRequired, readLastValues, writeLastValues, type FormSigns, type FormValues } from "./formModel";
import "./Forms.css";

const RECENT_KEY = (formKey: string) => `bento.form.recent.${formKey}`;
const RECENT_MAX = 20;

function readRecent(formKey: string): Record<string, string[]> {
  try {
    const parsed: unknown = JSON.parse(window.localStorage.getItem(RECENT_KEY(formKey)) ?? "{}");
    return parsed && typeof parsed === "object" ? (parsed as Record<string, string[]>) : {};
  } catch {
    return {};
  }
}

function rememberRecent(formKey: string, current: Record<string, string[]>, row: Record<string, unknown>, textColumns: string[]): Record<string, string[]> {
  const next = { ...current };
  for (const column of textColumns) {
    const value = row[column];
    if (typeof value !== "string" || !value) continue;
    next[column] = [value, ...(next[column] ?? []).filter((v) => v !== value)].slice(0, RECENT_MAX);
  }
  try {
    window.localStorage.setItem(RECENT_KEY(formKey), JSON.stringify(next));
  } catch {
    // Storage unavailable: no suggestions next time.
  }
  return next;
}

type FormFillProps = {
  /** Where "last used" values are kept: the form's id, or the table for a form not saved yet. */
  formKey: string;
  form: FormDefinition;
  table: ModelTable;
  /** Viewers see the form but cannot add rows. */
  readOnly?: boolean;
  /** The builder's preview: fillable, never saved. */
  preview?: boolean;
  /** "reset": stay for the next entry (the page); "close": hand back (a dialog). */
  afterSave?: "reset" | "close";
  onSaved?: (row: Record<string, unknown>) => void;
  onCancel?: () => void;
  /**
   * A public form (Step 5.6): nothing kept in the browser — no "last used"
   * values, no suggestions of earlier entries (the next visitor of a shared
   * computer would see them) — and no link into the app after saving.
   */
  publicForm?: boolean;
};

/**
 * Fills a form and adds the row through the data API (Step 4.4). After a
 * save the form starts over for the next entry: dates and "last used" values
 * as defaults, the rest empty, and the cursor in the first field.
 */
export function FormFill({ formKey, form, table, readOnly = false, preview = false, afterSave = "reset", onSaved, onCancel, publicForm = false }: FormFillProps) {
  const start = () => initialState(form, table, publicForm ? {} : readLastValues(formKey));
  const [state, setState] = useState<{ values: FormValues; signs: FormSigns }>(start);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState<{ row: Record<string, unknown>; summary: string } | null>(null);
  const [round, setRound] = useState(0);
  const [recent, setRecent] = useState(() => (publicForm ? {} : readRecent(formKey)));

  const fields = useMemo(() => formFields(form, table), [form, table]);
  const missing = useMemo(() => missingRequired(form, table), [form, table]);
  const gone = form.fields.flatMap((item) => (!isSection(item) && !table.columns.some((c) => c.name === item.column && isFormWritable(c)) ? [item.column] : []));
  const firstVisible = fields.find(({ field }) => !field.hidden)?.column.name;

  const setValue = (column: string, value: unknown) => {
    setState((s) => ({ ...s, values: { ...s.values, [column]: value } }));
    setErrors((e) => {
      if (!e[column]) return e;
      const { [column]: _removed, ...rest } = e;
      void _removed;
      return rest;
    });
  };
  const setSign = (column: string, sign: "negative" | "positive") => setState((s) => ({ ...s, signs: { ...s.signs, [column]: sign } }));

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    if (readOnly || preview || saving) return;
    const built = buildRow(form, table, state.values, state.signs);
    setErrors(built.errors);
    if (Object.keys(built.errors).length > 0) {
      setError(null);
      return;
    }
    setSaving(true);
    setError(null);
    try {
      const created = await createRow(table.name, built.row);
      if (!publicForm) {
        writeLastValues(formKey, built.row);
        setRecent((current) => rememberRecent(formKey, current, built.row, fields.filter(({ column }) => fieldKind(column) === "text").map(({ column }) => column.name)));
      }
      setSaved({ row: created, summary: summarize(built.row) });
      onSaved?.(created);
      if (afterSave === "reset") {
        setState(initialState(form, table, publicForm ? {} : readLastValues(formKey)));
        setRound((r) => r + 1);
      }
    } catch (cause) {
      if (cause instanceof ApiClientError) {
        const field = (cause.details as { field?: unknown } | undefined)?.field;
        if (typeof field === "string" && fields.some(({ column }) => column.name === field)) setErrors({ [field]: cause.message });
        else setError(cause.message);
      } else {
        setError("The row could not be saved. Check the connection and try again.");
      }
    } finally {
      setSaving(false);
    }
  };

  /** "−12,50 · Bakery": the first typed values (links would show as ids), so the next entry starts knowing the last one landed. */
  const summarize = (row: Record<string, unknown>) =>
    fields
      .filter(({ field, column }) => !field.hidden && fieldKind(column) !== "reference")
      .map(({ column }) => row[column.name])
      .filter((v) => v !== undefined && v !== null && v !== "" && typeof v !== "boolean")
      .slice(0, 3)
      .map((v) => (typeof v === "number" ? v.toLocaleString(undefined, { minimumFractionDigits: Number.isInteger(v) ? 0 : 2, maximumFractionDigits: 2 }) : String(v)))
      .join(" · ");

  const key = table.columns.find((c) => c.primaryKey)?.name ?? "id";

  return (
    <form className="form-fill" onSubmit={submit} noValidate aria-label={`Add a row to ${table.displayName ?? table.name}`}>
      {missing.length > 0 && !preview ? (
        <Alert tone="warning" title="This form cannot add rows" message={`${table.displayName ?? table.name} needs ${missing.map((c) => columnLabel(c)).join(", ")}, which the form does not ask for. Edit the form to add ${missing.length === 1 ? "it" : "them"}.`} />
      ) : null}
      {gone.length > 0 ? <Alert tone="info" message={`Left out, the table no longer has: ${gone.join(", ")}.`} /> : null}
      {saved && afterSave === "reset" && !publicForm ? (
        <div className="form-fill__saved" role="status">
          <span>
            Saved{saved.summary ? `: ${saved.summary}` : ""}.
          </span>
          {saved.row[key] !== undefined ? <Link to={`/data/${encodeURIComponent(table.name)}`}>Open {table.displayName ?? table.name}</Link> : null}
        </div>
      ) : null}

      <div className="form-fill__fields" key={round}>
        {form.fields.map((item, index) => {
          if (isSection(item)) {
            return (
              <h3 key={`section-${index}`} className="form-fill__section section-label">
                {item.section}
              </h3>
            );
          }
          const entry = fields.find(({ field }) => field === item);
          if (!entry || entry.field.hidden) return null;
          const { field, column } = entry;
          return (
            <FormFieldInput
              key={column.name}
              id={`form-${formKey}-${column.name}`.replace(/[^A-Za-z0-9_-]/g, "_")}
              field={field}
              column={column}
              value={state.values[column.name]}
              sign={state.signs[column.name]}
              error={errors[column.name]}
              recent={recent[column.name]}
              autoFocus={column.name === firstVisible && !readOnly && !preview}
              onChange={(value) => setValue(column.name, value)}
              onSign={(sign) => setSign(column.name, sign)}
            />
          );
        })}
      </div>

      {error ? <Alert tone="error" message={error} /> : null}
      {readOnly && !preview ? <p className="form-field__help">You can see this form; adding rows needs the editor role.</p> : null}

      <div className="form-fill__actions">
        {onCancel ? (
          <Button type="button" variant="ghost" onClick={onCancel}>
            Cancel
          </Button>
        ) : null}
        <Button type="submit" variant="primary" size="lg" className="form-fill__submit" disabled={readOnly || preview || saving || missing.length > 0}>
          {saving ? "Saving…" : "Save"}
        </Button>
      </div>
    </form>
  );
}
