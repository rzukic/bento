import { useEffect, useState, type ReactNode } from "react";
import { EmptyState } from "../../components/ui/EmptyState";
import { Spinner } from "../../components/ui/Spinner";
import { workspaceHref } from "../../lib/workspace";
import { useAuthStore } from "../../stores/authStore";
import { useWorkspacesStore } from "../../stores/workspacesStore";

/**
 * Step 5.5 part B: someone who is not a member of the workspace in the address
 * goes to the first one they are in (a full load, so everything starts there);
 * someone in none is told so.
 */
export function WorkspaceGate({ children }: { children: ReactNode }) {
  // Only an answer that says so: older servers send no workspace_role.
  const outside = useAuthStore((state) => state.user !== null && state.user.workspace_role === null);
  const load = useWorkspacesStore((state) => state.load);
  const [none, setNone] = useState(false);

  useEffect(() => {
    if (!outside) return;
    load({ force: true })
      .then((workspaces) => {
        if (workspaces.length > 0) window.location.assign(workspaceHref(workspaces[0]));
        else setNone(true);
      })
      .catch(() => setNone(true));
  }, [outside, load]);

  if (!outside) return <>{children}</>;
  if (!none) return <Spinner label="Opening your workspace" />;
  return (
    <EmptyState
      title="No workspace yet"
      description="You are not a member of any workspace. Ask an admin of this Bento to add you to one."
    />
  );
}
