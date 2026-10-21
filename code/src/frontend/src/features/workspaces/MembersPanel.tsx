import { useCallback, useEffect, useState } from "react";
import { ApiClientError } from "../../api/client";
import { listMembers, removeMember, setMember, type MemberCandidate, type WorkspaceMember, type WorkspaceRole } from "../../api/workspaces";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { Panel } from "../../components/ui/Panel";
import { SelectField } from "../../components/ui/SelectField";
import { Spinner } from "../../components/ui/Spinner";
import { currentWorkspace, useWorkspacesStore } from "../../stores/workspacesStore";
import { ROLE_NAMES, ROLE_OPTIONS } from "./roles";
import "./Workspaces.css";

function errorText(cause: unknown, fallback: string): string {
  return cause instanceof ApiClientError ? cause.message : fallback;
}

/**
 * Step 5.5 part B (Settings, the workspace's admins): who may open this
 * workspace and what they may do in it. Installation admins are admins
 * everywhere; new people are made by them (Settings → Users).
 */
export function MembersPanel() {
  const workspaces = useWorkspacesStore((state) => state.workspaces);
  const load = useWorkspacesStore((state) => state.load);
  const workspace = currentWorkspace(workspaces);
  const [members, setMembers] = useState<WorkspaceMember[] | null>(null);
  const [candidates, setCandidates] = useState<MemberCandidate[]>([]);
  const [adding, setAdding] = useState("");
  const [role, setRole] = useState<WorkspaceRole>("editor");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    load().catch(() => undefined);
  }, [load]);

  const reload = useCallback(() => {
    if (!workspace) return;
    listMembers(workspace.id)
      .then(({ members: list, candidates: others }) => {
        setMembers(list);
        setCandidates(others);
      })
      .catch((cause) => {
        setMembers([]);
        setError(errorText(cause, "The members could not be loaded."));
      });
  }, [workspace]);
  useEffect(reload, [reload]);

  const run = async (action: () => Promise<void>, fallback: string) => {
    setBusy(true);
    setError(null);
    try {
      await action();
      reload();
    } catch (cause) {
      setError(errorText(cause, fallback));
    } finally {
      setBusy(false);
    }
  };

  if (!workspace) return null;

  return (
    <Panel
      title={`Members of ${workspace.name}`}
      description="Who may open this workspace, and what they may do in it. Installation admins are admins in every workspace."
    >
      {error ? <Alert tone="error" message={error} /> : null}
      {members === null ? (
        <Spinner label="Loading members" />
      ) : (
        <ul className="ws-list">
          {members.map((member) => (
            <li key={member.userId} className="ws-list__item">
              <div className="ws-list__main">
                <div className="ws-list__title">
                  <strong>{member.username}</strong>
                  {member.installationAdmin ? <span className="ws-list__tag">Installation admin</span> : null}
                </div>
              </div>
              {member.installationAdmin ? (
                <span className="ws-list__address">{ROLE_NAMES.admin}</span>
              ) : (
                <div className="ws-list__actions">
                  <SelectField
                    label={`Role of ${member.username}`}
                    hideLabel
                    compact
                    value={member.role}
                    options={ROLE_OPTIONS.map((o) => ({ value: o.value, label: ROLE_NAMES[o.value] }))}
                    disabled={busy}
                    onChange={(event) => void run(() => setMember(workspace.id, member.userId, event.target.value as WorkspaceRole), "The role could not be changed.")}
                  />
                  <Button
                    type="button"
                    variant="ghost"
                    size="sm"
                    aria-label={`Remove ${member.username}`}
                    disabled={busy}
                    onClick={() => {
                      if (window.confirm(`Remove ${member.username} from ${workspace.name}? They will no longer see it.`))
                        void run(() => removeMember(workspace.id, member.userId), "The member could not be removed.");
                    }}
                  >
                    <Icon name="trash" size={14} />
                  </Button>
                </div>
              )}
            </li>
          ))}
        </ul>
      )}
      {candidates.length > 0 ? (
        <form
          className="ws-members__add"
          onSubmit={(event) => {
            event.preventDefault();
            const userId = Number(adding);
            if (!userId) return;
            void run(async () => {
              await setMember(workspace.id, userId, role);
              setAdding("");
            }, "The member could not be added.");
          }}
        >
          <SelectField
            label="Add a person"
            compact
            value={adding}
            options={[{ value: "", label: "Choose…" }, ...candidates.map((c) => ({ value: String(c.userId), label: c.username }))]}
            onChange={(event) => setAdding(event.target.value)}
          />
          <SelectField label="as" compact value={role} options={ROLE_OPTIONS} onChange={(event) => setRole(event.target.value as WorkspaceRole)} />
          <Button type="submit" variant="primary" size="sm" disabled={busy || !adding}>
            Add
          </Button>
        </form>
      ) : null}
    </Panel>
  );
}
