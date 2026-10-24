import type { Workspace } from "../../api/workspaces";
import "./OAuthConsent.css";

const ROLE_NAMES: Record<string, string> = { admin: "admin", editor: "editor", viewer: "viewer", api: "API" };

/**
 * The workspaces a connected app or an API key may use (Step 5.5): each one
 * keeps its own tables. Shown only when there is more than one. With
 * <c>onAllChange</c>, "all of them" comes first (Step 5.13): also those made
 * later — one connection for everything.
 */
export function WorkspaceChecklist({
  workspaces,
  chosen,
  onChange,
  all = false,
  onAllChange,
  disabled = false,
}: {
  workspaces: Workspace[];
  chosen: number[];
  onChange: (next: number[]) => void;
  all?: boolean;
  onAllChange?: (all: boolean) => void;
  disabled?: boolean;
}) {
  if (workspaces.length < 2) return null;
  return (
    <fieldset className="oauth-consent__choices" disabled={disabled}>
      <legend>In which workspaces</legend>
      {onAllChange ? (
        <label className="oauth-consent__choice">
          <input type="checkbox" checked={all} onChange={() => onAllChange(!all)} />
          <span>
            <strong>All workspaces</strong>
            <small>Also those made later. Every tool can then work in any of them.</small>
          </span>
        </label>
      ) : null}
      {workspaces.map((workspace) => {
        const checked = all || chosen.includes(workspace.id);
        return (
          <label key={workspace.id} className="oauth-consent__choice">
            <input
              type="checkbox"
              checked={checked}
              // At least one stays ticked; with "all" each one is in.
              disabled={all || (checked && chosen.length === 1)}
              onChange={() => onChange(checked ? chosen.filter((id) => id !== workspace.id) : [...chosen, workspace.id])}
            />
            <span>
              <strong>{workspace.name}</strong>
              <small>{workspace.role ? `You are ${ROLE_NAMES[workspace.role] ?? workspace.role} there.` : `/w/${workspace.slug}`}</small>
            </span>
          </label>
        );
      })}
    </fieldset>
  );
}
