import { useEffect, useRef, useState } from "react";
import { NavLink } from "react-router-dom";
import { Icon } from "../../components/common/Icon";
import { workspaceHref } from "../../lib/workspace";
import { useAuthStore } from "../../stores/authStore";
import { isInstallationAdmin } from "../../types/auth";
import { currentWorkspace, useWorkspacesStore } from "../../stores/workspacesStore";
import "./Workspaces.css";

/**
 * The sidebar's "Workspace" heading (Step 5.5): with more than one workspace
 * it names the current one and opens a list to switch. Switching loads the
 * page anew in the other workspace.
 */
export function WorkspaceSwitcher() {
  const workspaces = useWorkspacesStore((state) => state.workspaces);
  const load = useWorkspacesStore((state) => state.load);
  const admin = useAuthStore((state) => isInstallationAdmin(state.user));
  const [open, setOpen] = useState(false);
  const root = useRef<HTMLDivElement>(null);

  useEffect(() => {
    load().catch(() => undefined);
  }, [load]);

  useEffect(() => {
    if (!open) return;
    const close = (event: MouseEvent | KeyboardEvent) => {
      if (event instanceof KeyboardEvent ? event.key === "Escape" : !root.current?.contains(event.target as Node)) setOpen(false);
    };
    document.addEventListener("mousedown", close);
    document.addEventListener("keydown", close);
    return () => {
      document.removeEventListener("mousedown", close);
      document.removeEventListener("keydown", close);
    };
  }, [open]);

  const current = currentWorkspace(workspaces);
  if (!workspaces || workspaces.length < 2) return <>Workspace</>;

  return (
    <div className="ws-switcher" ref={root}>
      <button
        type="button"
        className="ws-switcher__button"
        aria-haspopup="menu"
        aria-expanded={open}
        aria-label={`Workspace: ${current?.name ?? "Default"}. Switch workspace`}
        onClick={() => setOpen((value) => !value)}
      >
        <span className="ws-switcher__name">{current?.name ?? "Workspace"}</span>
        <Icon name="chevron-down" size={12} />
      </button>
      {open ? (
        <div className="ws-switcher__menu" role="menu">
          {workspaces.map((workspace) => (
            <a
              key={workspace.id}
              role="menuitemradio"
              aria-checked={workspace.id === current?.id}
              className={workspace.id === current?.id ? "ws-switcher__item ws-switcher__item--current" : "ws-switcher__item"}
              href={workspaceHref(workspace)}
            >
              <span>{workspace.name}</span>
              {workspace.id === current?.id ? <Icon name="check" size={13} /> : null}
            </a>
          ))}
          {admin ? (
            <NavLink role="menuitem" className="ws-switcher__item ws-switcher__item--manage" to="/settings#workspaces" onClick={() => setOpen(false)}>
              Manage workspaces
            </NavLink>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}
