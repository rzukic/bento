import { useEffect, useState } from "react";
import { useParams, useSearchParams } from "react-router-dom";
import { setFormTrap, setShareToken } from "../../api/client";
import { getForm, type SavedForm } from "../../api/forms";
import { getShareInfo, type ShareInfo } from "../../api/shares";
import { Spinner } from "../../components/ui/Spinner";
import { useDocumentTitle } from "../../hooks/useDocumentTitle";
import { setWorkspaceCalendar } from "../../lib/calendar";
import { setWorkspaceFormat } from "../../lib/preferences";
import { useSchemaStore } from "../../stores/schemaStore";
import { DashboardPage } from "../dashboards/DashboardPage";
import { FormFill } from "../forms/FormFill";
import "../dashboards/Share.css";

/** Inside an iframe? (A browser that refuses to tell counts as yes.) */
function framed(): boolean {
  try {
    return window.self !== window.top;
  } catch {
    return true;
  }
}

/**
 * Step 5.6: a dashboard through a share link, for anyone with it. Every
 * request goes as the link's visitor; the server answers only what the
 * dashboard's widgets ask for.
 */
export function SharedPage() {
  const { token = "" } = useParams();
  const [params] = useSearchParams();
  const embed = params.get("embed") === "1" || framed();
  const [info, setInfo] = useState<ShareInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  useDocumentTitle(info ? `${info.name} · Bento` : "Bento");

  useEffect(() => {
    setShareToken(token);
    let cancelled = false;
    getShareInfo()
      .then((next) => {
        if (cancelled) return;
        // The link's workspace decides how numbers and dates read.
        setWorkspaceFormat(next.settings.locale, next.settings.currency);
        setWorkspaceCalendar({ timeZone: next.settings.timeZone, weekStart: next.settings.weekStart as never, yearStart: next.settings.yearStart });
        setInfo(next);
      })
      .catch(() => !cancelled && setError("This link does not exist, has expired or was revoked."));
    return () => {
      cancelled = true;
      setShareToken(null);
    };
  }, [token]);

  if (error) return <div className="shared-page__message">{error}</div>;
  if (!info) return <Spinner label="Opening the dashboard" />;
  if (framed() && !info.embed) return <div className="shared-page__message">This page may not be shown inside another site. Open the link directly.</div>;
  if (info.kind === "form" && info.formId !== null) return <SharedForm formId={info.formId} thanks={info.thanks} embed={embed} />;
  if (info.dashboardId === null) return <div className="shared-page__message">This link does not exist, has expired or was revoked.</div>;

  const filters = Object.fromEntries(Object.entries(info.filters ?? {}).map(([column, values]) => [column, values.map(String)]));
  return (
    <main className={embed ? "shared-page shared-page--embed" : "shared-page"}>
      <DashboardPage shared={{ dashboardId: info.dashboardId, filters, embed }} />
      {embed ? null : <p className="shared-page__footer">Shared from Bento{info.expiresAt ? ` · this link works until ${new Date(info.expiresAt).toLocaleDateString()}` : ""}</p>}
    </main>
  );
}

/**
 * A public form (Step 5.6 part B): anyone with the link adds a row; after
 * sending, the link's thank-you text. The trap field is out of sight: people
 * leave it empty, robots fill it.
 */
function SharedForm({ formId, thanks, embed }: { formId: number; thanks: string | null; embed: boolean }) {
  const snapshot = useSchemaStore((state) => state.snapshot);
  const loadSchema = useSchemaStore((state) => state.load);
  const [form, setForm] = useState<SavedForm | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [sent, setSent] = useState(0);
  useDocumentTitle(form ? `${form.name} · Bento` : "Bento");

  useEffect(() => {
    void loadSchema();
    getForm(formId)
      .then(setForm)
      .catch(() => setError("This form could not be opened."));
  }, [formId, loadSchema]);

  if (error) return <div className="shared-page__message">{error}</div>;
  const table = form ? snapshot?.tables.find((t) => t.name === form.table) : undefined;
  if (!form || !table) return <Spinner label="Opening the form" />;
  return (
    <main className={embed ? "shared-page shared-page--embed shared-form" : "shared-page shared-form"}>
      <div className="shared-form__card">
        {embed ? null : <h1 className="shared-form__title">{form.name}</h1>}
        {sent > 0 ? (
          <p className="shared-form__thanks" role="status">
            {thanks ?? "Thanks — it arrived."} {sent > 1 ? `(${sent} sent)` : ""}
          </p>
        ) : null}
        <label className="shared-form__trap" aria-hidden="true">
          Leave this empty
          <input type="text" name="website" tabIndex={-1} autoComplete="off" onChange={(event) => setFormTrap(event.target.value)} />
        </label>
        <FormFill formKey={`share-${formId}`} form={form.form} table={table} publicForm onSaved={() => setSent((n) => n + 1)} />
      </div>
      {embed ? null : <p className="shared-page__footer">Sent to Bento</p>}
    </main>
  );
}
