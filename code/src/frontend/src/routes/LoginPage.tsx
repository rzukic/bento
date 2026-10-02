import { useEffect, useState, type FormEvent } from "react";
import { Navigate, useLocation, useNavigate } from "react-router-dom";
import { Glyph, Sky } from "../components/common/Atmosphere";
import { Icon } from "../components/common/Icon";
import { ThemeSwitch } from "../components/common/ThemeSwitch";
import { TextField } from "../components/ui/TextField";
import { useSetupStore } from "../features/setup/setupStore";
import { useDocumentTitle } from "../hooks/useDocumentTitle";
import { useAuthStore } from "../stores/authStore";
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
  const setupInfo = useSetupStore((state) => state.info);

  const navigate = useNavigate();
  const location = useLocation();
  const redirectTarget = (location.state as LocationState)?.from?.pathname ?? "/";

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
      <Sky />
      <Glyph value="Re" />
      <div className="login__frame">
        <header className="login__header">
          <div className="login__brand">
            <span className="login__brand-mark" aria-hidden="true">
              <Icon name="layers" size={15} />
            </span>
            <span className="login__brand-name">Repere</span>
          </div>
          <div className="login__header-right">
            {setupInfo ? (
              <span className="login__hint login__hint--env">
                {setupInfo.database_provider} · {setupInfo.auth_mode} · v{setupInfo.version}
              </span>
            ) : null}
            <ThemeSwitch />
          </div>
        </header>

        <div className="login__body">
          <section className="login__content">
            <span className="login__eyebrow">Sign in</span>
            <h1 id="login-title" className="login__title display">
              <span className="login__line">
                <span>Welcome</span>
              </span>
              <span className="login__line">
                <span style={{ animationDelay: "0.1s" }}>back.</span>
              </span>
            </h1>
            <p className="login__lede">
              {status === "bootstrapping"
                ? "Resuming your session…"
                : "Your session resumes on its own while the refresh cookie is valid. Otherwise, sign in to continue to the workspace."}
            </p>
          </section>

          <form className="login__form" onSubmit={handleSubmit} noValidate>
            {error ? (
              <p className="login__error" role="alert">
                {error}
              </p>
            ) : null}
            <TextField
              underline
              label="Username"
              type="text"
              name="username"
              autoComplete="username"
              placeholder="admin"
              value={username}
              onChange={(event) => {
                setUsername(event.target.value);
                if (error) clearError();
              }}
              disabled={busy}
              required
              autoFocus
            />
            <TextField
              underline
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
            <div className="login__row">
              <span className="login__hint">Enter ↵</span>
              <button
                type="submit"
                className="btn btn--primary btn--lg"
                disabled={busy || username.length === 0 || password.length === 0}
              >
                {loginInFlight ? "Signing in…" : "Sign in"} <Icon name="expand" size={14} />
              </button>
            </div>
          </form>
        </div>

        <footer className="login__footer">
          <div className="login__rule" aria-hidden="true" />
          <div className="login__footer-row">
            <span className="login__hint">Stable public route: /login</span>
            <span className="login__hint">© Repere</span>
          </div>
        </footer>
      </div>
    </main>
  );
}
