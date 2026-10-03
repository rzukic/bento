import { useEffect } from "react";
import { Navigate, Outlet } from "react-router-dom";
import { AuthSplash } from "../../components/auth/AuthSplash";
import { useSetupStore } from "./setupStore";

/**
 * Wraps every route except `/setup`. On the first render it asks the backend
 * whether the installation is initialised; an uninitialised one is sent to the
 * wizard from any path (including `/login`). If the status call fails the app
 * continues as if initialised — a backend hiccup must not lock users out.
 */
export function SetupGate() {
  const status = useSetupStore((state) => state.status);
  const initialized = useSetupStore((state) => state.info?.initialized ?? true);
  const load = useSetupStore((state) => state.load);

  useEffect(() => {
    if (status === "idle") {
      void load();
    }
  }, [status, load]);

  if (status === "idle" || status === "loading") {
    return <AuthSplash label="Checking installation" />;
  }

  if (status === "ready" && !initialized) {
    return <Navigate to="/setup" replace />;
  }

  return <Outlet />;
}
