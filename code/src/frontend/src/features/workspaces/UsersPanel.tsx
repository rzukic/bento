import { useCallback, useEffect, useState, type FormEvent } from "react";
import { ApiClientError } from "../../api/client";
import { createUser, deleteUser, listUsers, updateUser, type BentoUser } from "../../api/users";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { ConfirmDialog } from "../../components/ui/Dialog";
import { Panel } from "../../components/ui/Panel";
import { SelectField } from "../../components/ui/SelectField";
import { Spinner } from "../../components/ui/Spinner";
import { TextField } from "../../components/ui/TextField";
import { useAuthStore } from "../../stores/authStore";
import { useWorkspacesStore } from "../../stores/workspacesStore";
import { ROLE_NAMES, ROLE_OPTIONS } from "./roles";
import "./Workspaces.css";

function errorText(cause: unknown, fallback: string): string {
  return cause instanceof ApiClientError ? cause.message : fallback;
}

const INSTALLATION_ROLES = [
  { value: "admin", label: "Installation admin — every workspace, users, vault, AI" },
  { value: "editor", label: "Member — editor in the workspaces below" },
  { value: "viewer", label: "Member — viewer in the workspaces below" },
];

/**
 * Step 5.5 part B (Settings, installation admins): the people who may sign
 * in, their installation role and the workspaces they are in. A reset
 * password signs them out everywhere.
 */
export function UsersPanel() {
  const me = useAuthStore((state) => state.user);
  const workspaces = useWorkspacesStore((state) => state.workspaces);
  const loadWorkspaces = useWorkspacesStore((state) => state.load);
  const [users, setUsers] = useState<BentoUser[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [role, setRole] = useState("editor");
  const [chosen, setChosen] = useState<Record<number, string>>({});
  const [busy, setBusy] = useState(false);
  const [resetting, setResetting] = useState<{ id: number; password: string } | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [deleting, setDeleting] = useState<BentoUser | null>(null);

  const remove = async (user: BentoUser) => {
    setDeleting(null);
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      await deleteUser(user.id);
      setNotice(`${user.username} was deleted; their dashboards, forms, queries and views are yours now.`);
      load();
    } catch (cause) {
      setError(errorText(cause, "The user could not be deleted."));
    } finally {
      setBusy(false);
    }
  };

  const load = useCallback(() => {
    listUsers()
      .then(setUsers)
      .catch((cause) => {
        setUsers([]);
        setError(errorText(cause, "The users could not be loaded."));
      });
  }, []);
  useEffect(load, [load]);
  useEffect(() => {
    loadWorkspaces().catch(() => undefined);
  }, [loadWorkspaces]);

  const create = async (event: FormEvent) => {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await createUser({
        username: username.trim(),
        password,
        role,
        workspaces: Object.entries(chosen).map(([id, workspaceRole]) => ({ id: Number(id), role: workspaceRole })),
      });
      setAdding(false);
      setUsername("");
      setPassword("");
      setChosen({});
      load();
    } catch (cause) {
      setError(errorText(cause, "The user could not be created."));
    } finally {
      setBusy(false);
    }
  };

  const change = async (user: BentoUser, changes: { role?: string; password?: string }, done: string) => {
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      await updateUser(user.id, changes);
      setNotice(done);
      setResetting(null);
      load();
    } catch (cause) {
      setError(errorText(cause, "The user could not be changed."));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Panel
      id="users"
      title="Users"
      description="Who may sign in. Installation admins run everything; members open the workspaces they are in, with a role in each (Settings → Members of a workspace)."
      actions={
        adding ? null : (
          <Button type="button" variant="ghost" size="sm" leadingIcon={<Icon name="plus" size={13} />} onClick={() => setAdding(true)}>
            Add user
          </Button>
        )
      }
    >
      {error ? <Alert tone="error" message={error} /> : null}
      {notice ? <Alert tone="success" message={notice} /> : null}
      {adding ? (
        <form className="ws-form" onSubmit={(event) => void create(event)}>
          <TextField label="User name" value={username} onChange={(event) => setUsername(event.target.value)} autoComplete="off" required autoFocus />
          <TextField
            label="Password"
            type="password"
            hint="At least 8 characters. Tell it to them; they can sign in at once."
            value={password}
            onChange={(event) => setPassword(event.target.value)}
            autoComplete="new-password"
            required
          />
          <SelectField label="Role" value={role} options={INSTALLATION_ROLES} onChange={(event) => setRole(event.target.value)} />
          {role !== "admin" && workspaces ? (
            <fieldset className="ws-form__workspaces">
              <legend className="field__label">Workspaces</legend>
              {workspaces.map((workspace) => (
                <div key={workspace.id} className="ws-form__workspace">
                  <label className="ws-form__check">
                    <input
                      type="checkbox"
                      checked={chosen[workspace.id] !== undefined}
                      onChange={(event) =>
                        setChosen(({ [workspace.id]: _, ...rest }) => (event.target.checked ? { ...rest, [workspace.id]: role } : rest))
                      }
                    />
                    {workspace.name}
                  </label>
                  {chosen[workspace.id] !== undefined ? (
                    <SelectField
                      label={`Role in ${workspace.name}`}
                      hideLabel
                      compact
                      value={chosen[workspace.id]}
                      options={ROLE_OPTIONS.map((o) => ({ value: o.value, label: ROLE_NAMES[o.value] }))}
                      onChange={(event) => setChosen((current) => ({ ...current, [workspace.id]: event.target.value }))}
                    />
                  ) : null}
                </div>
              ))}
              <p className="ws-form__hint">None ticked: the workspace you are in now.</p>
            </fieldset>
          ) : null}
          <div className="ws-form__actions">
            <Button type="submit" variant="primary" size="sm" disabled={busy || !username.trim() || password.length < 8}>
              {busy ? "Creating…" : "Create"}
            </Button>
            <Button type="button" variant="ghost" size="sm" onClick={() => setAdding(false)}>
              Cancel
            </Button>
          </div>
        </form>
      ) : null}
      {users === null ? (
        <Spinner label="Loading users" />
      ) : (
        <ul className="ws-list">
          {users.map((user) => (
            <li key={user.id} className="ws-list__item">
              <div className="ws-list__main">
                <div className="ws-list__title">
                  <strong>{user.username}</strong>
                  {user.role === "admin" ? <span className="ws-list__tag">Installation admin</span> : null}
                  {user.id === me?.id ? <span className="ws-list__tag">You</span> : null}
                </div>
                <span className="ws-list__address">
                  {user.role === "admin"
                    ? "Every workspace"
                    : user.workspaces.length === 0
                      ? "No workspace yet"
                      : user.workspaces.map((w) => `${w.name} (${ROLE_NAMES[w.role] ?? w.role})`).join(" · ")}
                </span>
              </div>
              {resetting?.id === user.id ? (
                <form
                  className="ws-list__rename"
                  onSubmit={(event) => {
                    event.preventDefault();
                    void change(user, { password: resetting.password }, `${user.username} has a new password and was signed out.`);
                  }}
                >
                  <TextField
                    label="New password"
                    hideLabel
                    compact
                    type="password"
                    placeholder="New password"
                    autoComplete="new-password"
                    value={resetting.password}
                    onChange={(event) => setResetting({ id: user.id, password: event.target.value })}
                    autoFocus
                  />
                  <Button type="submit" variant="primary" size="sm" disabled={busy || resetting.password.length < 8}>
                    Set
                  </Button>
                  <Button type="button" variant="ghost" size="sm" onClick={() => setResetting(null)}>
                    Cancel
                  </Button>
                </form>
              ) : (
                <div className="ws-list__actions">
                  <SelectField
                    label={`Installation role of ${user.username}`}
                    hideLabel
                    compact
                    value={user.role === "admin" ? "admin" : "member"}
                    options={[
                      { value: "admin", label: "Installation admin" },
                      { value: "member", label: "Member" },
                    ]}
                    disabled={busy}
                    onChange={(event) =>
                      void change(
                        user,
                        { role: event.target.value === "admin" ? "admin" : "editor" },
                        event.target.value === "admin" ? `${user.username} is an installation admin now.` : `${user.username} is a member now; add them to workspaces.`,
                      )
                    }
                  />
                  <Button type="button" variant="ghost" size="sm" onClick={() => setResetting({ id: user.id, password: "" })}>
                    Reset password
                  </Button>
                  {user.id === me?.id ? null : (
                    <Button type="button" variant="ghost" size="sm" aria-label={`Delete ${user.username}`} disabled={busy} onClick={() => setDeleting(user)}>
                      <Icon name="trash" size={14} />
                    </Button>
                  )}
                </div>
              )}
            </li>
          ))}
        </ul>
      )}
      <ConfirmDialog
        open={deleting !== null}
        title={`Delete ${deleting?.username ?? ""}?`}
        description="They are signed out and cannot sign in again; their API keys stop and their connected apps are disconnected. Their dashboards, forms, saved queries and views become yours."
        confirmLabel="Delete user"
        confirmVariant="danger"
        onConfirm={() => deleting && void remove(deleting)}
        onCancel={() => setDeleting(null)}
      />
    </Panel>
  );
}
