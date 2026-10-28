import { useEffect, useState } from "react";
import { ApiClientError } from "../../api/client";
import { createCredential, deleteCredential, listCredentials, updateCredential, type Credential } from "../../api/credentials";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { ConfirmDialog } from "../../components/ui/Dialog";
import { Spinner } from "../../components/ui/Spinner";

function message(cause: unknown, fallback: string): string {
  return cause instanceof ApiClientError ? cause.message : fallback;
}

function when(value: string | null): string {
  if (!value) return "never";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
}

/**
 * Credentials (Step 4.2): API keys for functions that call APIs. A value goes
 * in once; the server keeps it encrypted and only hands it to the functions
 * that name it.
 */
export function CredentialsPanel() {
  const [credentials, setCredentials] = useState<Credential[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [name, setName] = useState("");
  const [value, setValue] = useState("");
  const [description, setDescription] = useState("");
  const [saving, setSaving] = useState(false);
  const [replacing, setReplacing] = useState<string | null>(null);
  const [replacement, setReplacement] = useState("");
  const [deleting, setDeleting] = useState<Credential | null>(null);

  const reload = () =>
    listCredentials()
      .then(setCredentials)
      .catch((cause) => {
        setCredentials([]);
        setError(message(cause, "Credentials could not be loaded."));
      });

  useEffect(() => {
    void reload();
  }, []);

  const add = async () => {
    setSaving(true);
    setError(null);
    try {
      await createCredential(name.trim(), value, description.trim() || undefined);
      setName("");
      setValue("");
      setDescription("");
      await reload();
    } catch (cause) {
      setError(message(cause, "The credential could not be saved."));
    } finally {
      setSaving(false);
    }
  };

  const replace = async (credential: Credential) => {
    setError(null);
    try {
      await updateCredential(credential.name, { value: replacement });
      setReplacing(null);
      setReplacement("");
      await reload();
    } catch (cause) {
      setError(message(cause, "The value could not be replaced."));
    }
  };

  const remove = async (credential: Credential) => {
    setDeleting(null);
    setError(null);
    try {
      await deleteCredential(credential.name);
      await reload();
    } catch (cause) {
      setError(message(cause, "The credential could not be deleted."));
    }
  };

  return (
    <div className="credentials">
      <p className="credentials__intro">
        API keys for functions that call APIs, e.g. a weather or price service. Values are stored encrypted and never shown again; a function
        reads the ones it lists as <code>credentials.name</code>. Free sources such as Yahoo Finance need none.
      </p>

      {error ? <Alert tone="error" message={error} /> : null}

      {credentials === null ? (
        <Spinner label="Loading credentials" />
      ) : credentials.length === 0 ? (
        <p className="credentials__empty">No credentials stored.</p>
      ) : (
        <ul className="credentials__list">
          {credentials.map((credential) => (
            <li key={credential.name} className="credentials__item">
              <div className="credentials__main">
                <span className="mono credentials__name">{credential.name}</span>
                {credential.description ? <span className="credentials__description">{credential.description}</span> : null}
                <span className="credentials__meta">
                  {credential.usedBy.length > 0 ? `Used by ${credential.usedBy.join(", ")}` : "Not used by a function"} · last used{" "}
                  {when(credential.lastUsedAt)}
                </span>
                {!credential.readable ? (
                  <span className="credentials__unreadable">Cannot be read: the server's secret changed. Enter the value again.</span>
                ) : null}
              </div>
              {replacing === credential.name ? (
                <div className="credentials__replace">
                  <input
                    className="field__control field__control--compact"
                    type="password"
                    autoComplete="off"
                    value={replacement}
                    placeholder="New value"
                    aria-label={`New value for ${credential.name}`}
                    onChange={(event) => setReplacement(event.target.value)}
                  />
                  <Button type="button" size="sm" variant="primary" disabled={!replacement} onClick={() => replace(credential)}>
                    Save
                  </Button>
                  <Button type="button" size="sm" variant="ghost" onClick={() => setReplacing(null)}>
                    Cancel
                  </Button>
                </div>
              ) : (
                <div className="credentials__actions">
                  <Button type="button" size="sm" variant="ghost" onClick={() => { setReplacing(credential.name); setReplacement(""); }}>
                    Replace value
                  </Button>
                  <Button type="button" size="icon" variant="ghost" aria-label={`Delete ${credential.name}`} onClick={() => setDeleting(credential)}>
                    <Icon name="trash" size={13} />
                  </Button>
                </div>
              )}
            </li>
          ))}
        </ul>
      )}

      <form
        className="credentials__form"
        onSubmit={(event) => {
          event.preventDefault();
          void add();
        }}
      >
        <span className="section-label">Add a credential</span>
        <div className="credentials__fields">
          <label className="function-editor__field">
            <span className="function-editor__label">Name</span>
            <input
              className="field__control field__control--compact mono"
              value={name}
              placeholder="alphavantage_key"
              aria-label="Credential name"
              onChange={(event) => setName(event.target.value.toLowerCase().replace(/[^a-z0-9_]/g, "_"))}
            />
          </label>
          <label className="function-editor__field">
            <span className="function-editor__label">Value</span>
            <input
              className="field__control field__control--compact"
              type="password"
              autoComplete="off"
              value={value}
              aria-label="Credential value"
              onChange={(event) => setValue(event.target.value)}
            />
          </label>
          <label className="function-editor__field credentials__wide">
            <span className="function-editor__label">Description</span>
            <input
              className="field__control field__control--compact"
              value={description}
              placeholder="What it is for"
              aria-label="Credential description"
              onChange={(event) => setDescription(event.target.value)}
            />
          </label>
        </div>
        <div>
          <Button type="submit" variant="primary" disabled={saving || !name.trim() || !value}>
            {saving ? "Saving…" : "Add credential"}
          </Button>
        </div>
      </form>

      <ConfirmDialog
        open={deleting !== null}
        title={`Delete ${deleting?.name ?? "credential"}?`}
        description="The value is gone for good. Functions that still list it must be changed first."
        confirmLabel="Delete"
        confirmVariant="danger"
        onConfirm={() => deleting && remove(deleting)}
        onCancel={() => setDeleting(null)}
      />
    </div>
  );
}
