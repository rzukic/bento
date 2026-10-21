import { useEffect, useState, type FormEvent } from "react";
import { useLocation } from "react-router-dom";
import { ApiClientError } from "../../api/client";
import { createWorkspace, deleteWorkspace, renameWorkspace, type Workspace } from "../../api/workspaces";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { Dialog } from "../../components/ui/Dialog";
import { Panel } from "../../components/ui/Panel";
import { Spinner } from "../../components/ui/Spinner";
import { TextField } from "../../components/ui/TextField";
import { currentWorkspaceSlug, workspaceHref } from "../../lib/workspace";
import { useWorkspacesStore } from "../../stores/workspacesStore";
import "./Workspaces.css";

function errorText(cause: unknown, fallback: string): string {
  return cause instanceof ApiClientError ? cause.message : fallback;
}

/**
 * Step 5.5 (Settings, admins): the workspaces of the installation — separate
 * areas, each with its own tables, dashboards, forms, functions, imports,
 * rules, apps and settings. Users, notification channels and AI stay shared.
 */
export function WorkspacesPanel() {
  const workspaces = useWorkspacesStore((state) => state.workspaces);
  const load = useWorkspacesStore((state) => state.load);
  const [error, setError] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);
  const [name, setName] = useState("");
  const [slug, setSlug] = useState("");
  const [busy, setBusy] = useState(false);
  const [renaming, setRenaming] = useState<{ id: number; name: string } | null>(null);
  const [deleting, setDeleting] = useState<Workspace | null>(null);
  const [confirm, setConfirm] = useState("");

  useEffect(() => {
    load({ force: true }).catch((cause) => setError(errorText(cause, "The workspaces could not be loaded.")));
  }, [load]);

  // "Manage workspaces" in the sidebar opens Settings at this panel.
  const { hash } = useLocation();
  useEffect(() => {
    if (hash === "#workspaces") document.getElementById("workspaces")?.scrollIntoView?.({ block: "start" });
  }, [hash]);

  const reload = () => load({ force: true }).catch(() => undefined);

  const create = async (event: FormEvent) => {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await createWorkspace(name.trim(), slug.trim() || undefined);
      setAdding(false);
      setName("");
      setSlug("");
      await reload();
    } catch (cause) {
      setError(errorText(cause, "The workspace could not be created."));
    } finally {
      setBusy(false);
    }
  };

  const rename = async (event: FormEvent) => {
    event.preventDefault();
    if (!renaming) return;
    setBusy(true);
    setError(null);
    try {
      await renameWorkspace(renaming.id, renaming.name.trim());
      setRenaming(null);
      await reload();
    } catch (cause) {
      setError(errorText(cause, "The workspace could not be renamed."));
    } finally {
      setBusy(false);
    }
  };

  const remove = async () => {
    if (!deleting) return;
    setBusy(true);
    setError(null);
    try {
      await deleteWorkspace(deleting.id, confirm.trim());
      const wasCurrent = deleting.slug === currentWorkspaceSlug();
      setDeleting(null);
      setConfirm("");
      if (wasCurrent) {
        window.location.assign("/");
        return;
      }
      await reload();
    } catch (cause) {
      setError(errorText(cause, "The workspace could not be deleted."));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Panel
      id="workspaces"
      title="Workspaces"
      description="Separate areas of this installation, each with its own tables, dashboards, forms, functions, imports, rules, apps and settings. Users, notification channels and AI are shared."
      actions={
        adding ? null : (
          <Button type="button" variant="ghost" size="sm" leadingIcon={<Icon name="plus" size={13} />} onClick={() => setAdding(true)}>
            New workspace
          </Button>
        )
      }
    >
      {error ? <Alert tone="error" message={error} /> : null}
      {adding ? (
        <form className="ws-form" onSubmit={(event) => void create(event)}>
          <TextField label="Name" value={name} onChange={(event) => setName(event.target.value)} placeholder="Finances" required autoFocus />
          <TextField
            label="Short name"
            hint="In the addresses: /w/<short name> and /mcp/<short name>. Made from the name when left empty; it cannot be changed later."
            value={slug}
            onChange={(event) => setSlug(event.target.value.toLowerCase())}
            placeholder="finances"
            pattern="[a-z][a-z0-9-]{1,39}"
          />
          <div className="ws-form__actions">
            <Button type="submit" variant="primary" size="sm" disabled={busy || !name.trim()}>
              {busy ? "Creating…" : "Create"}
            </Button>
            <Button type="button" variant="ghost" size="sm" onClick={() => setAdding(false)}>
              Cancel
            </Button>
          </div>
        </form>
      ) : null}
      {workspaces === null ? (
        <Spinner label="Loading workspaces" />
      ) : (
        <ul className="ws-list">
          {workspaces.map((workspace) => (
            <li key={workspace.id} className="ws-list__item">
              {renaming?.id === workspace.id ? (
                <form className="ws-list__rename" onSubmit={(event) => void rename(event)}>
                  <TextField
                    label="Name"
                    hideLabel
                    compact
                    value={renaming.name}
                    onChange={(event) => setRenaming({ id: workspace.id, name: event.target.value })}
                    autoFocus
                  />
                  <Button type="submit" variant="primary" size="sm" disabled={busy || !renaming.name.trim()}>
                    Save
                  </Button>
                  <Button type="button" variant="ghost" size="sm" onClick={() => setRenaming(null)}>
                    Cancel
                  </Button>
                </form>
              ) : (
                <div className="ws-list__main">
                  <div className="ws-list__title">
                    <a href={workspaceHref(workspace)}>
                      <strong>{workspace.name}</strong>
                    </a>
                    {workspace.isDefault ? <span className="ws-list__tag">Default</span> : null}
                    {workspace.slug === (currentWorkspaceSlug() ?? "default") ? <span className="ws-list__tag">Open</span> : null}
                  </div>
                  <code className="ws-list__address">
                    {workspace.isDefault ? "/  ·  /api/v1  ·  /mcp" : `/w/${workspace.slug}  ·  /api/v1/w/${workspace.slug}  ·  /mcp/${workspace.slug}`}
                  </code>
                </div>
              )}
              {renaming?.id === workspace.id ? null : (
                <div className="ws-list__actions">
                  <Button type="button" variant="ghost" size="sm" onClick={() => setRenaming({ id: workspace.id, name: workspace.name })}>
                    Rename
                  </Button>
                  {workspace.isDefault ? null : (
                    <Button type="button" variant="ghost" size="sm" aria-label={`Delete ${workspace.name}`} onClick={() => setDeleting(workspace)}>
                      <Icon name="trash" size={14} />
                    </Button>
                  )}
                </div>
              )}
            </li>
          ))}
        </ul>
      )}
      <Dialog
        open={deleting !== null}
        title={`Delete “${deleting?.name ?? ""}”?`}
        description="Its tables with all their rows, its dashboards, forms, functions, imports, rules, apps and settings are deleted. This cannot be undone."
        onClose={() => {
          setDeleting(null);
          setConfirm("");
        }}
        footer={
          <>
            <Button type="button" variant="ghost" onClick={() => setDeleting(null)}>
              Cancel
            </Button>
            <Button type="button" variant="danger" disabled={busy || confirm.trim() !== deleting?.slug} onClick={() => void remove()}>
              {busy ? "Deleting…" : "Delete workspace"}
            </Button>
          </>
        }
      >
        <TextField
          label={`Type “${deleting?.slug ?? ""}” to confirm`}
          value={confirm}
          onChange={(event) => setConfirm(event.target.value)}
          autoComplete="off"
        />
      </Dialog>
    </Panel>
  );
}
