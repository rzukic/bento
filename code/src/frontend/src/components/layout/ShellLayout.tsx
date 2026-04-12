import { Outlet } from "react-router-dom";
import { AppFrame } from "./AppFrame";

export function ShellLayout() {
  return (
    <AppFrame>
      <Outlet />
    </AppFrame>
  );
}
