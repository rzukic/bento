import { Outlet } from "react-router-dom";
import { WorkspaceGate } from "../../features/workspaces/WorkspaceGate";
import { AppFrame } from "./AppFrame";

export function ShellLayout() {
  return (
    <AppFrame>
      <WorkspaceGate>
        <Outlet />
      </WorkspaceGate>
    </AppFrame>
  );
}
