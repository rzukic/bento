import { useCallback, useEffect, useState } from "react";
import { ApiClientError } from "../../api/client";
import type { DashboardFilter } from "../../api/dashboards";
import { createShare, listShares, revokeShare, type CreatedShare, type ShareKind, type ShareLink } from "../../api/shares";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { Dialog } from "../../components/ui/Dialog";
import { Spinner } from "../../components/ui/Spinner";
import { getCurrentLocale } from "../data-explorer/components/editors/temporalEditing";
import "./Share.css";

const EXPIRY = [
  { value: "7", label: "7 days" },
  { value: "30", label: "30 days" },
  { value: "90", label: "90 days" },
  { value: "365", label: "A year" },
  { value: "", label: "No end" },
];

function formatWhen(value: string | null): string {
  if (!value) return "—";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : new Intl.DateTimeFormat(getCurrentLocale(), { dateStyle: "medium", timeStyle: "short" }).format(date);
}

function errorText(cause: unknown, fallback: string): string {
  return cause instanceof ApiClientError ? cause.message : fallback;
}

/**
 * Step 5.6: links for people without an account — a dashboard read only,
 * with its fixed filters; a form to send rows, a few an hour. Until they
 * expire or are revoked; the address is shown once.
 */
export function ShareDialog({
  kind,
  targetId,
  title,
  filters = [],
  onClose,
}: {
  kind: ShareKind;
  targetId: number;
  title: string;
  filters?: DashboardFilter[];
  onClose: () => void;
}) {
  const [links, setLinks] = useState<ShareLink[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [name, setName] = useState("");
  const [expiry, setExpiry] = useState("30");
  const [embed, setEmbed] = useState(false);
  const [values, setValues] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  const [created, setCreated] = useState<CreatedShare | null>(null);
  const [copied, setCopied] = useState<"url" | "embed" | null>(null);
  const [perHour, setPerHour] = useState("10");
  const [thanks, setThanks] = useState("");
  const isForm = kind === "form";

  const load = useCallback(() => {
    listShares(kind, targetId)
      .then(setLinks)
      .catch((cause) => {
        setLinks([]);
        setError(errorText(cause, "The links could not be loaded."));
      });
  }, [kind, targetId]);
  useEffect(load, [load]);

  const create = async () => {
    setBusy(true);
    setError(null);
    try {
      const fixed = Object.fromEntries(
        Object.entries(values)
          .map(([column, text]) => [column, text.split(",").map((v) => v.trim()).filter(Boolean)] as const)
          .filter(([, list]) => list.length > 0),
      );
      const result = await createShare(kind, targetId, {
        name: name.trim() || "Link",
        expiresInDays: expiry ? Number(expiry) : undefined,
        ...(isForm
          ? { perHour: Number(perHour) || 10, ...(thanks.trim() ? { thanks: thanks.trim() } : {}) }
          : { filters: Object.keys(fixed).length > 0 ? fixed : undefined }),
        embed,
      });
      setCreated(result);
      setName("");
      setValues({});
      load();
    } catch (cause) {
      setError(errorText(cause, "The link could not be made."));
    } finally {
      setBusy(false);
    }
  };

  const revoke = async (link: ShareLink) => {
    if (!window.confirm(`Revoke “${link.name}”? Whoever has it can no longer open the ${isForm ? "form" : "dashboard"}.`)) return;
    try {
      await revokeShare(link.id);
      load();
    } catch (cause) {
      setError(errorText(cause, "The link could not be revoked."));
    }
  };

  const copy = (text: string, which: "url" | "embed") => {
    void navigator.clipboard?.writeText(text).then(() => setCopied(which), () => setCopied(null));
  };

  return (
    <Dialog
      open
      title={`Share “${title}”`}
      description={
        isForm
          ? "A link lets anyone who has it fill in this form without an account — it adds rows to the form's table and shows nothing of it."
          : "A link shows this dashboard to anyone who has it, without an account — read only, nothing else of the workspace."
      }
      onClose={onClose}
      panelClassName="share-dialog"
      footer={
        <Button type="button" variant="ghost" onClick={onClose}>
          Close
        </Button>
      }
    >
      {error ? <Alert tone="error" message={error} /> : null}

      {created ? (
        <div className="share-dialog__created" role="status">
          <p>
            <Icon name="check" size={14} /> <strong>The link is ready.</strong> Copy it now — it is shown only once.
          </p>
          <div className="share-dialog__secret">
            <code aria-label="Share link">{created.url}</code>
            <Button type="button" size="sm" variant="primary" leadingIcon={<Icon name={copied === "url" ? "check" : "copy"} size={13} />} onClick={() => copy(created.url, "url")}>
              {copied === "url" ? "Copied" : "Copy"}
            </Button>
          </div>
          {created.embed ? (
            <div className="share-dialog__secret">
              <code aria-label="Embed code">{created.embed}</code>
              <Button type="button" size="sm" leadingIcon={<Icon name={copied === "embed" ? "check" : "copy"} size={13} />} onClick={() => copy(created.embed!, "embed")}>
                {copied === "embed" ? "Copied" : "Copy"}
              </Button>
            </div>
          ) : null}
          <Button type="button" size="sm" variant="ghost" onClick={() => setCreated(null)}>
            Done
          </Button>
        </div>
      ) : (
        <form
          className="share-dialog__form"
          onSubmit={(event) => {
            event.preventDefault();
            void create();
          }}
        >
          <label className="share-dialog__field share-dialog__field--grow">
            <span>Name</span>
            <input className="field__control field__control--compact" value={name} placeholder="e.g. For the tax advisor" onChange={(event) => setName(event.target.value)} />
          </label>
          <label className="share-dialog__field">
            <span>Expires</span>
            <select className="field__control field__control--compact" value={expiry} onChange={(event) => setExpiry(event.target.value)}>
              {EXPIRY.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </select>
          </label>
          {isForm ? (
            <>
              <label className="share-dialog__field">
                <span>Entries per visitor an hour</span>
                <input className="field__control field__control--compact" type="number" min={1} max={1000} value={perHour} onChange={(event) => setPerHour(event.target.value)} />
              </label>
              <label className="share-dialog__field share-dialog__field--grow">
                <span>Thank-you text</span>
                <input className="field__control field__control--compact" value={thanks} placeholder="Thanks — it arrived." onChange={(event) => setThanks(event.target.value)} />
              </label>
            </>
          ) : null}
          {(isForm ? [] : filters).map((filter) => (
            <label key={filter.column} className="share-dialog__field">
              <span>Only {filter.label || filter.column}</span>
              <input
                className="field__control field__control--compact"
                value={values[filter.column] ?? ""}
                placeholder="all"
                onChange={(event) => setValues((current) => ({ ...current, [filter.column]: event.target.value }))}
              />
            </label>
          ))}
          <label className="share-dialog__check">
            <input type="checkbox" checked={embed} onChange={(event) => setEmbed(event.target.checked)} />
            May be embedded in other sites
          </label>
          <Button type="submit" variant="primary" disabled={busy} leadingIcon={<Icon name="link" size={13} />}>
            {busy ? "Making…" : "Make link"}
          </Button>
          {!isForm && filters.length > 0 ? <p className="share-dialog__hint">Fixed filters: the visitor always sees only these values (several separated by commas).</p> : null}
        </form>
      )}

      {links === null ? (
        <Spinner label="Loading links" />
      ) : links.length === 0 ? (
        <p className="share-dialog__hint">No links yet.</p>
      ) : (
        <ul className="share-dialog__links">
          {links.map((link) => (
            <li key={link.id} className={link.status === "active" ? "share-dialog__link" : "share-dialog__link share-dialog__link--off"}>
              <div>
                <strong>{link.name}</strong>
                <span className="share-dialog__meta">
                  {link.status === "active" ? `until ${link.expiresAt ? formatWhen(link.expiresAt) : "revoked"}` : link.status}
                  {" · "}
                  {link.views === 1 ? "opened once" : `opened ${link.views} times`}
                  {isForm && link.perHour ? ` · ${link.perHour} entries an hour each` : ""}
                  {link.lastUsedAt ? `, last ${formatWhen(link.lastUsedAt)}` : ""}
                  {link.filters ? ` · only ${Object.entries(link.filters).map(([c, v]) => `${c} ${v.join(", ")}`).join("; ")}` : ""}
                  {link.embed ? " · embeddable" : ""}
                </span>
              </div>
              {link.status === "active" ? (
                <Button type="button" variant="ghost" size="sm" onClick={() => void revoke(link)}>
                  Revoke
                </Button>
              ) : null}
            </li>
          ))}
        </ul>
      )}
    </Dialog>
  );
}
