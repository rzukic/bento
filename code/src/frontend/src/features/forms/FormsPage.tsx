import { useEffect, useMemo, useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { getForm, type SavedForm } from "../../api/forms";
import { ApiClientError } from "../../api/client";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { Spinner } from "../../components/ui/Spinner";
import { useAuthStore } from "../../stores/authStore";
import { useFormsStore } from "../../stores/formsStore";
import { useSchemaStore } from "../../stores/schemaStore";
import { ShareDialog } from "../dashboards/ShareDialog";
import { FormFill } from "./FormFill";
import { isFormWritable } from "./formModel";
import "./Forms.css";

/** Editors and admins add rows; viewers see forms only. */
export function useCanWrite(): boolean {
  return useAuthStore((state) => state.user?.role === "admin" || state.user?.role === "editor");
}

/** Tables a form can add rows to: user tables with at least one column to fill. */
function formTables(tables: { name: string; displayName?: string | null; columns: { type: string }[] }[]) {
  return tables.filter((t) => !t.name.startsWith("_") && t.columns.some((c) => isFormWritable(c as never)));
}

/**
 * Forms (Step 4.4): quick entry into a table — "Add transaction" on the phone.
 * The list, and a start from any table.
 */
export function FormsPage() {
  const navigate = useNavigate();
  const canWrite = useCanWrite();
  const forms = useFormsStore((s) => s.forms);
  const loadForms = useFormsStore((s) => s.load);
  const formsError = useFormsStore((s) => s.error);
  const snapshot = useSchemaStore((s) => s.snapshot);
  const loadSchema = useSchemaStore((s) => s.load);
  const [table, setTable] = useState("");

  useEffect(() => {
    void loadSchema();
    void loadForms({ force: true });
  }, [loadSchema, loadForms]);

  const tables = useMemo(() => formTables(snapshot?.tables ?? []), [snapshot]);
  const label = (name: string) => snapshot?.tables.find((t) => t.name === name)?.displayName ?? name;

  return (
    <div className="forms-page">
      <header>
        <h1 className="forms-page__title">Forms</h1>
        <p className="forms-page__lead">Quick entry into a table — a transaction on the phone in a few taps. Each form adds a row, checked like any other.</p>
      </header>

      {formsError ? <Alert tone="error" message={formsError} /> : null}

      {forms === null ? (
        <Spinner label="Loading forms" />
      ) : forms.length === 0 ? (
        <p className="forms-page__empty">No forms yet.{canWrite ? " Start one from a table below." : ""}</p>
      ) : (
        <ul className="forms-list" role="list">
          {forms.map((form) => (
            <li key={form.id} className="forms-list__row">
              <Link className="forms-list__item" to={`/forms/${form.id}`}>
                <Icon name="forms" size={16} />
                <span className="forms-list__name">{form.name}</span>
                <span className="forms-list__meta">
                  {label(form.table)}
                  {form.form.pinned ? " · quick add" : ""}
                  {form.isShared ? " · shared" : ""}
                  {!form.isOwner ? " · by someone else" : ""}
                </span>
                <Icon name="chevron-right" size={14} />
              </Link>
              {form.isOwner && canWrite ? (
                <Link className="forms-list__edit" to={`/forms/${form.id}/edit`} aria-label={`Edit ${form.name}`}>
                  <Icon name="pencil" size={13} />
                </Link>
              ) : null}
            </li>
          ))}
        </ul>
      )}

      {canWrite ? (
        <section className="forms-new" aria-labelledby="forms-new-heading">
          <h2 id="forms-new-heading" className="forms-new__title">
            New form
          </h2>
          <form
            className="forms-new__form"
            onSubmit={(event) => {
              event.preventDefault();
              if (table) navigate(`/forms/new?table=${encodeURIComponent(table)}`);
            }}
          >
            <select className="field__control" aria-label="Table for the new form" value={table} onChange={(event) => setTable(event.target.value)}>
              <option value="">Choose a table…</option>
              {tables.map((t) => (
                <option key={t.name} value={t.name}>
                  {t.displayName ?? t.name}
                </option>
              ))}
            </select>
            <Button type="submit" variant="primary" disabled={!table} leadingIcon={<Icon name="plus" size={13} />}>
              Start
            </Button>
          </form>
          <p className="forms-page__hint">The form starts with every column you can fill; reorder, hide and give defaults before saving.</p>
        </section>
      ) : null}
    </div>
  );
}

/** One form, filled on its own page — what "Quick add" opens. */
export function FormPage() {
  const { formId } = useParams();
  const id = Number(formId);
  const canWrite = useCanWrite();
  const snapshot = useSchemaStore((s) => s.snapshot);
  const loadSchema = useSchemaStore((s) => s.load);
  const [form, setForm] = useState<SavedForm | null>(null);
  const [error, setError] = useState<string | null>(null);
  // Step 5.6: a public link to the form, for its owner and the workspace's admins.
  const isAdmin = useAuthStore((state) => state.user?.role === "admin");
  const [shareOpen, setShareOpen] = useState(false);

  useEffect(() => {
    void loadSchema();
    setForm(null);
    setError(null);
    getForm(id)
      .then(setForm)
      .catch((cause) => setError(cause instanceof ApiClientError && cause.status === 404 ? "This form does not exist, or it is not shared with you." : "The form could not be loaded."));
  }, [id, loadSchema]);

  const table = form ? snapshot?.tables.find((t) => t.name === form.table) : undefined;

  return (
    <div className="forms-page forms-page--fill">
      {error ? (
        <Alert tone="error" message={error} />
      ) : !form || !snapshot ? (
        <Spinner label="Loading the form" />
      ) : (
        <>
          <header className="form-page__header">
            <div>
              <h1 className="forms-page__title">{form.name}</h1>
              <p className="forms-page__lead">Adds a row to {table?.displayName ?? form.table}.</p>
            </div>
            <span className="form-page__actions">
              {form.isOwner || isAdmin ? (
                <button type="button" className="btn btn--ghost btn--sm" onClick={() => setShareOpen(true)}>
                  <Icon name="link" size={13} /> Share
                </button>
              ) : null}
              {form.isOwner && canWrite ? (
                <Link className="btn btn--ghost btn--sm" to={`/forms/${form.id}/edit`}>
                  <Icon name="pencil" size={13} /> Edit
                </Link>
              ) : null}
            </span>
          </header>
          {shareOpen ? <ShareDialog kind="form" targetId={form.id} title={form.name} onClose={() => setShareOpen(false)} /> : null}
          {table ? (
            <FormFill formKey={String(form.id)} form={form.form} table={table} readOnly={!canWrite} />
          ) : (
            <Alert tone="error" message={`The table ${form.table} no longer exists.`} />
          )}
        </>
      )}
    </div>
  );
}
