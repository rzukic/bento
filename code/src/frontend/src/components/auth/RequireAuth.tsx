import { useEffect } from "react";
import { Navigate, Outlet, useLocation } from "react-router-dom";
import { useAuthStore } from "../../stores/authStore";
import { Spinner } from "../ui/Spinner";
import "./RequireAuth.css";

/**
 * Gates the authenticated shell routes. While the auth store is still figuring
 * out whether a session can be silently resumed, a small full-screen splash is
 * shown; once the status resolves, unauthenticated users are redirected to the
 * login page with the attempted location preserved so they can be returned
 * after sign-in.
 */
export function RequireAuth() {
  const status = useAuthStore((state) => state.status);
  const bootstrap = useAuthStore((state) => state.bootstrap);
  const location = useLocation();

  useEffect(() => {
    if (status === "idle") {
      void bootstrap();
    }
  }, [status, bootstrap]);

  if (status === "idle" || status === "bootstrapping") {
    return (
      <div className="auth-splash" role="status" aria-live="polite">
        <div className="auth-splash__mark" aria-hidden="true" />
        <Spinner label="Resuming session" />
      </div>
    );
  }

  if (status === "unauthenticated") {
    return <Navigate to="/login" replace state={{ from: location }} />;
  }

  return <Outlet />;
}
