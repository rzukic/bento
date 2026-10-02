import { useEffect, useMemo, useState, type FormEvent } from "react";
import { Link, Navigate, useNavigate } from "react-router-dom";
import { postRegister } from "../../api/auth";
import { ApiClientError } from "../../api/client";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { Panel } from "../../components/ui/Panel";
import { Spinner } from "../../components/ui/Spinner";
import { TextField } from "../../components/ui/TextField";
import { useDocumentTitle } from "../../hooks/useDocumentTitle";
import { useAuthStore } from "../../stores/authStore";
import type { SampleDataResponse, SetupStatus } from "../../types/setup";
import { postSampleData } from "./api";
import { useSetupStore } from "./setupStore";
import "./SetupPage.css";

type StepId = "welcome" | "environment" | "admin" | "sample" | "done";

const STEP_LABELS: Record<StepId, string> = {
  welcome: "Welcome",
  environment: "Environment",
  admin: "Admin account",
  sample: "Sample data",
  done: "Done"
};

/**
 * Which steps the wizard shows depends on the deployment: open auth mode has
 * no accounts to create, and once a schema exists the demo workspace can no
 * longer be loaded.
 */
export function buildSetupSteps(info: SetupStatus): StepId[] {
  const steps: StepId[] = ["welcome", "environment"];
  if (info.auth_required) steps.push("admin");
  if (info.sample_data_available) steps.push("sample");
  steps.push("done");
  return steps;
}

function describeError(cause: unknown, fallback: string): string {
  return cause instanceof ApiClientError ? cause.message : fallback;
}

export function SetupPage() {
  useDocumentTitle("Setup · Repere");

  const status = useSetupStore((state) => state.status);
  const info = useSetupStore((state) => state.info);
  const statusError = useSetupStore((state) => state.error);
  const wizardActive = useSetupStore((state) => state.wizardActive);
  const load = useSetupStore((state) => state.load);
  const beginWizard = useSetupStore((state) => state.beginWizard);

  useEffect(() => {
    if (status === "idle") {
      void load();
    }
  }, [status, load]);

  // Entering the wizard on an uninitialised installation pins it open until
  // the done step, even though the admin step initialises the backend.
  const shouldBeginWizard = status === "ready" && info !== null && !info.initialized && !wizardActive;
  useEffect(() => {
    if (shouldBeginWizard) beginWizard();
  }, [shouldBeginWizard, beginWizard]);

  // Only the very first fetch shows the splash. A forced re-read during the
  // wizard (e.g. after a rejected registration) must not unmount the steps.
  if ((status === "idle" || status === "loading") && !info) {
    return (
      <main className="setup setup--splash" role="status" aria-live="polite">
        <Spinner label="Checking installation" />
      </main>
    );
  }

  if (status === "error" && !info) {
    return (
      <SetupFrame>
        <h1 id="setup-title" className="setup__title">
          Setup unavailable
        </h1>
        <p className="setup__lede">
          The backend could not report the installation state. Make sure the API is running and try
          again.
        </p>
        <Alert tone="error" message={statusError ?? "Unknown error."} />
        <div className="setup__actions">
          <Button variant="primary" onClick={() => void load({ force: true })}>
            Retry
          </Button>
          <Link className="btn" to="/login">
            Go to sign in
          </Link>
        </div>
      </SetupFrame>
    );
  }

  if (!info) {
    // Unreachable in practice: `status` is "ready" or "error with cached info" here.
    return <Navigate to="/" replace />;
  }

  if (info.initialized && !wizardActive) {
    return <Navigate to="/" replace />;
  }

  return <SetupWizard info={info} />;
}

function SetupFrame({ children, steps, current }: { children: React.ReactNode; steps?: StepId[]; current?: StepId }) {
  return (
    <main className="setup" aria-labelledby="setup-title">
      <Panel className="setup__panel" bodyClassName="setup__panel-body">
        <header className="setup__brand">
          <span className="setup__brand-mark" aria-hidden="true">
            <Icon name="layers" size={16} />
          </span>
          <span className="setup__brand-name">Repere</span>
          <span className="setup__brand-env mono">first-run setup</span>
        </header>
        {steps && current ? <StepIndicator steps={steps} current={current} /> : null}
        <div className="setup__body">{children}</div>
        <footer className="setup__footer mono">
          <span>Stable public route: /setup</span>
          <span>© Repere</span>
        </footer>
      </Panel>
    </main>
  );
}

function StepIndicator({ steps, current }: { steps: StepId[]; current: StepId }) {
  const currentIndex = steps.indexOf(current);
  return (
    <ol className="setup__steps" aria-label="Setup progress">
      {steps.map((step, index) => {
        const state = index < currentIndex ? "done" : index === currentIndex ? "current" : "upcoming";
        return (
          <li
            key={step}
            className={`setup__step setup__step--${state}`}
            aria-current={state === "current" ? "step" : undefined}
          >
            <span className="setup__step-index mono" aria-hidden="true">
              {state === "done" ? <Icon name="check" size={12} /> : index + 1}
            </span>
            <span className="setup__step-label">{STEP_LABELS[step]}</span>
          </li>
        );
      })}
    </ol>
  );
}

function SetupWizard({ info }: { info: SetupStatus }) {
  // Steps are fixed when the wizard opens so completing the admin step (which
  // flips `initialized`) or loading sample data does not remove steps while
  // the user is standing on them.
  const steps = useMemo(() => buildSetupSteps(info), [info]);
  const [stepIndex, setStepIndex] = useState(0);
  const current = steps[Math.min(stepIndex, steps.length - 1)];

  const goNext = () => setStepIndex((index) => Math.min(index + 1, steps.length - 1));
  const goBack = () => setStepIndex((index) => Math.max(index - 1, 0));

  return (
    <SetupFrame steps={steps} current={current}>
      {current === "welcome" ? <WelcomeStep onNext={goNext} /> : null}
      {current === "environment" ? <EnvironmentStep info={info} onNext={goNext} onBack={goBack} /> : null}
      {current === "admin" ? <AdminStep onNext={goNext} onBack={goBack} /> : null}
      {current === "sample" ? <SampleDataStep onNext={goNext} /> : null}
      {current === "done" ? <DoneStep info={info} /> : null}
    </SetupFrame>
  );
}

function WelcomeStep({ onNext }: { onNext: () => void }) {
  return (
    <>
      <h1 id="setup-title" className="setup__title">
        Welcome to Repere
      </h1>
      <p className="setup__lede">
        This installation has not been set up yet. The next steps review the deployment, create the
        first administrator and optionally load a small demo workspace so you can explore the schema
        designer and the Data Explorer right away.
      </p>
      <div className="setup__actions setup__actions--end">
        <Button variant="primary" onClick={onNext} trailingIcon={<Icon name="expand" size={14} />}>
          Get started
        </Button>
      </div>
    </>
  );
}

function EnvironmentStep({ info, onNext, onBack }: { info: SetupStatus; onNext: () => void; onBack: () => void }) {
  const rows: Array<[string, string]> = [
    ["Database provider", info.database_provider],
    ["Authentication mode", info.auth_mode],
    ["Backend version", info.version],
    ["Schema version", String(info.schema_version)]
  ];
  return (
    <>
      <h1 id="setup-title" className="setup__title">
        Environment
      </h1>
      <p className="setup__lede">
        These values come from the deployment configuration (environment variables or Docker Compose)
        and cannot be changed from the browser.
      </p>
      <dl className="setup__facts">
        {rows.map(([label, value]) => (
          <div key={label} className="setup__fact">
            <dt>{label}</dt>
            <dd className="mono">{value}</dd>
          </div>
        ))}
      </dl>
      <div className="setup__actions">
        <Button variant="ghost" onClick={onBack}>
          Back
        </Button>
        <Button variant="primary" onClick={onNext}>
          Continue
        </Button>
      </div>
    </>
  );
}

function AdminStep({ onNext, onBack }: { onNext: () => void; onBack: () => void }) {
  const login = useAuthStore((state) => state.login);
  const markInitialized = useSetupStore((state) => state.markInitialized);
  const reloadStatus = useSetupStore((state) => state.load);

  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [bootstrapClosed, setBootstrapClosed] = useState(false);

  const mismatch = confirm.length > 0 && confirm !== password;
  const canSubmit = !submitting && username.trim().length > 0 && password.length > 0 && confirm === password;

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!canSubmit) return;
    setSubmitting(true);
    setError(null);
    const trimmed = username.trim();
    try {
      await postRegister({ username: trimmed, password });
    } catch (cause) {
      const closed = cause instanceof ApiClientError && (cause.status === 403 || cause.status === 409);
      setBootstrapClosed(closed);
      setError(
        closed
          ? "An administrator account already exists for this installation. Sign in with it instead."
          : describeError(cause, "Unable to reach the backend to create the account.")
      );
      if (closed) void reloadStatus({ force: true });
      setSubmitting(false);
      return;
    }

    // The account exists from here on; even if the automatic sign-in fails
    // the installation is initialised and /login must be reachable.
    markInitialized();
    try {
      await login(trimmed, password);
    } catch (cause) {
      setError(describeError(cause, "Account created, but signing in failed. Use the sign-in page."));
      setBootstrapClosed(true);
      setSubmitting(false);
      return;
    }
    setSubmitting(false);
    onNext();
  }

  return (
    <>
      <h1 id="setup-title" className="setup__title">
        Create the administrator
      </h1>
      <p className="setup__lede">
        The first account becomes the administrator. Further users can be added later through the
        API.
      </p>

      {error ? <Alert tone="error" message={error} /> : null}

      <form className="setup__form" onSubmit={handleSubmit} noValidate>
        <TextField
          label="Username"
          name="username"
          autoComplete="username"
          placeholder="admin"
          value={username}
          onChange={(event) => setUsername(event.target.value)}
          disabled={submitting}
          required
        />
        <TextField
          label="Password"
          type="password"
          name="password"
          autoComplete="new-password"
          placeholder="••••••••"
          value={password}
          onChange={(event) => setPassword(event.target.value)}
          disabled={submitting}
          required
        />
        <TextField
          label="Confirm password"
          type="password"
          name="confirm"
          autoComplete="new-password"
          placeholder="••••••••"
          value={confirm}
          onChange={(event) => setConfirm(event.target.value)}
          error={mismatch ? "Passwords do not match." : null}
          disabled={submitting}
          required
        />
        <div className="setup__actions">
          <Button variant="ghost" type="button" onClick={onBack} disabled={submitting}>
            Back
          </Button>
          {bootstrapClosed ? (
            <Link className="btn btn--primary" to="/login">
              Go to sign in
            </Link>
          ) : (
            <Button variant="primary" type="submit" disabled={!canSubmit}>
              {submitting ? "Creating account…" : "Create account and continue"}
            </Button>
          )}
        </div>
      </form>
    </>
  );
}

function SampleDataStep({ onNext }: { onNext: () => void }) {
  const [loading, setLoading] = useState(false);
  const [result, setResult] = useState<SampleDataResponse | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function handleLoad() {
    setLoading(true);
    setError(null);
    try {
      setResult(await postSampleData());
    } catch (cause) {
      setError(describeError(cause, "Unable to reach the backend to load the sample data."));
    } finally {
      setLoading(false);
    }
  }

  const totalRows = result ? Object.values(result.rows_inserted).reduce((sum, count) => sum + count, 0) : 0;

  return (
    <>
      <h1 id="setup-title" className="setup__title">
        Sample data
      </h1>
      <p className="setup__lede">
        Optionally load a small demo workspace — customers, orders and products with relations and
        a status column suited for a kanban view. This is only possible while the installation is
        empty; you can skip it and start from a blank schema.
      </p>

      {error ? <Alert tone="error" message={error} /> : null}

      {result ? (
        <Alert
          tone="success"
          title="Demo workspace loaded"
          message={
            <span>
              {result.tables_created.length} tables ({result.tables_created.join(", ")}), {totalRows} rows,
              schema version {result.schema_version}.
            </span>
          }
        />
      ) : null}

      <div className={result ? "setup__actions setup__actions--end" : "setup__actions"}>
        {result ? (
          <Button variant="primary" onClick={onNext}>
            Continue
          </Button>
        ) : (
          <>
            <Button variant="ghost" onClick={onNext} disabled={loading}>
              Skip for now
            </Button>
            <Button
              variant="primary"
              onClick={() => void handleLoad()}
              disabled={loading}
              leadingIcon={<Icon name="database" size={14} />}
            >
              {loading ? "Loading sample data…" : "Load sample data"}
            </Button>
          </>
        )}
      </div>
    </>
  );
}

function DoneStep({ info }: { info: SetupStatus }) {
  const navigate = useNavigate();
  const completeWizard = useSetupStore((state) => state.completeWizard);
  const authStatus = useAuthStore((state) => state.status);
  const signedIn = authStatus === "authenticated" || !info.auth_required;

  function finish() {
    completeWizard();
    navigate("/", { replace: true });
  }

  return (
    <>
      <h1 id="setup-title" className="setup__title">
        Setup complete
      </h1>
      <p className="setup__lede">
        {signedIn
          ? "Repere is ready. Open the workspace to design your schema and start working with data."
          : "Repere is ready. Sign in with the administrator account you just created to open the workspace."}
      </p>
      <div className="setup__actions setup__actions--end">
        <Button variant="primary" onClick={finish} trailingIcon={<Icon name="expand" size={14} />}>
          {signedIn ? "Open workspace" : "Go to sign in"}
        </Button>
      </div>
    </>
  );
}
