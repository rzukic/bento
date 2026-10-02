import { useEffect, useState, type FormEvent } from "react";
import { Navigate, useLocation, useNavigate } from "react-router-dom";
import { Icon } from "../components/common/Icon";
import { Alert } from "../components/ui/Alert";
import { Button } from "../components/ui/Button";
import { Panel } from "../components/ui/Panel";
import { TextField } from "../components/ui/TextField";
import { useAuthStore } from "../stores/authStore";
import { useDocumentTitle } from "../hooks/useDocumentTitle";
import "./LoginPage.css";

type LocationState = { from?: { pathname?: string } } | null;

export function LoginPage() {
  // The shell sets titles for app routes; /login lives outside the shell, so
  // without this the tab keeps showing the title of the page the user left.
  useDocumentTitle("Sign in · Repere");

  const status = useAuthStore((state) => state.status);
  const loginInFlight = useAuthStore((state) => state.loginInFlight);
  const error = useAuthStore((state) => state.error);
  const login = useAuthStore((state) => state.login);
  const clearError = useAuthStore((state) => state.clearError);
  const bootstrap = useAuthStore((state) => state.bootstrap);

  const navigate = useNavigate();
  const location = useLocation();
  const redirectTarget =
    (location.state as LocationState)?.from?.pathname ?? "/";

  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");

  // If the user lands on /login directly with no prior bootstrap attempt,
  // still try to silently resume a session — otherwise they would be stuck
  // on the login form even though they already have a valid refresh cookie.
  useEffect(() => {
    if (status === "idle") {
      void bootstrap();
    }
  }, [status, bootstrap]);

  // Already signed in → bounce back to the workspace.
  if (status === "authenticated") {
    return <Navigate to={redirectTarget} replace />;
  }

  const busy = loginInFlight || status === "bootstrapping";

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (busy) return;
    try {
      await login(username.trim(), password);
      navigate(redirectTarget, { replace: true });
    } catch {
      // auth store already captured the error; UI renders it below
    }
  }

  return (
    <main className="login" aria-labelledby="login-title">
      <Panel className="login__panel" bodyClassName="login__panel-body">
        <header className="login__brand">
          <span className="login__brand-mark" aria-hidden="true">
            <Icon name="layers" size={16} />
          </span>
          <span className="login__brand-name">Repere</span>
          <span className="login__brand-env mono">local · v0.1.0</span>
        </header>

        <div className="login__body">
          <h1 id="login-title" className="login__title">
            Sign in
          </h1>
          <p className="login__lede">
            Authenticate against the Repere backend to continue to the workspace.
          </p>

          {error ? <Alert tone="error" message={error} /> : null}

          <form className="login__form" onSubmit={handleSubmit} noValidate>
            <TextField
              label="Username"
              type="text"
              name="username"
              autoComplete="username"
              placeholder="you@local"
              value={username}
              onChange={(event) => {
                setUsername(event.target.value);
                if (error) clearError();
              }}
              disabled={busy}
              required
            />

            <TextField
              label="Password"
              type="password"
              name="password"
              autoComplete="current-password"
              placeholder="••••••••"
              value={password}
              onChange={(event) => {
                setPassword(event.target.value);
                if (error) clearError();
              }}
              disabled={busy}
              required
            />

            <Button
              className="login__submit"
              variant="primary"
              type="submit"
              disabled={busy || username.length === 0 || password.length === 0}
            >
              {loginInFlight ? "Signing in…" : "Sign in"}
            </Button>

            <p className="login__notice mono">
              Session resumes automatically while the refresh cookie is valid.
            </p>
          </form>
        </div>

        <footer className="login__footer mono">
          <span>Stable public route: /login</span>
          <span>© Repere</span>
        </footer>
      </Panel>
    </main>
  );
}
