import { useEffect, useId, useMemo, useState, type CSSProperties, type FormEvent, type InputHTMLAttributes, type ReactNode } from "react";
import { Link, Navigate, useNavigate } from "react-router-dom";
import { postRegister } from "../../api/auth";
import { ApiClientError } from "../../api/client";
import { Glyph, Sky, type GlyphTone } from "../../components/common/Atmosphere";
import { Icon } from "../../components/common/Icon";
import { ThemeSwitch } from "../../components/common/ThemeSwitch";
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

/** The sky shifts hue as the wizard advances; one value per step position. */
const STEP_HUES = [0, 18, 40, 70, 110];

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

function pad(index: number): string {
  return String(index + 1).padStart(2, "0");
}

export function SetupPage() {
  useDocumentTitle("Setup · Bento");

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
        <Sky />
        <span className="setup__hint">Checking installation…</span>
      </main>
    );
  }

  if (status === "error" && !info) {
    return (
      <SetupFrame hue={0} numeral="!" tone="warn">
        <section className="setup__content">
          <span className="setup__eyebrow rise">Setup</span>
          <h1 id="setup-title" className="setup__title setup__title--md">
            <Line>Setup unavailable.</Line>
          </h1>
          <p className="setup__lede rise d2">
            We couldn't check this installation. Make sure Bento is running, then try again.
          </p>
          <p className="setup__error rise d3" role="alert">
            {statusError ?? "Unknown error."}
          </p>
        </section>
        <aside className="setup__actions rise d3">
          <div className="setup__row">
            <Link className="setup-btn setup-btn--ghost" to="/login">
              Go to sign in
            </Link>
            <button type="button" className="setup-btn setup-btn--accent" onClick={() => void load({ force: true })}>
              Retry
            </button>
          </div>
        </aside>
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

// ── Frame: header, footer ───────────────────────────────────────────────────

type NumeralTone = GlyphTone;

type FrameProps = {
  children: ReactNode;
  hue: number;
  numeral: string;
  tone: NumeralTone;
  steps?: StepId[];
  current?: StepId;
};

function SetupFrame({ children, hue, numeral, tone, steps, current }: FrameProps) {
  const currentIndex = steps && current ? steps.indexOf(current) : -1;
  const progress = steps && currentIndex >= 0 ? Math.round(((currentIndex + 1) / steps.length) * 100) : 0;
  return (
    <main className="setup" aria-labelledby="setup-title">
      <Sky hue={hue} />
      <Glyph value={numeral} tone={tone} />
      <div className="setup__frame">
        <header className="setup__header">
          <div className="setup__brand">
            <span className="setup__brand-mark" aria-hidden="true">
              <Icon name="layers" size={15} />
            </span>
            <span className="setup__brand-name">Bento</span>
          </div>
          <div className="setup__header-right">
            <ThemeSwitch />
          </div>
        </header>

        <div className="setup__body">{children}</div>

        {steps && current ? (
          <footer className="setup__footer">
            <div className="setup__track" aria-hidden="true">
              <i style={{ width: `${progress}%` }} />
              <b />
            </div>
            <div className="setup__footer-row">
              <ol className="setup__steps" aria-label="Setup progress">
                {steps.map((step, index) => {
                  const state = index < currentIndex ? "done" : index === currentIndex ? "current" : "upcoming";
                  return (
                    <li
                      key={step}
                      className={`setup__step setup__step--${state}`}
                      aria-current={state === "current" ? "step" : undefined}
                    >
                      <span className="setup__step-num">{pad(index)}</span>
                      <span>{STEP_LABELS[step]}</span>
                    </li>
                  );
                })}
              </ol>
              <span className="setup__hint">
                step {pad(currentIndex)} of {pad(steps.length - 1)}
              </span>
            </div>
          </footer>
        ) : (
          <footer className="setup__footer">
            <div className="setup__track" aria-hidden="true" />
            <div className="setup__footer-row">
              <span className="setup__hint">© Bento</span>
            </div>
          </footer>
        )}
      </div>
    </main>
  );
}

/** One headline line revealed from behind a clipping edge. */
function Line({ children, delay = 0 }: { children: ReactNode; delay?: number }) {
  return (
    <span className="setup__line">
      <span style={delay ? { animationDelay: `${delay}s` } : undefined}>{children}</span>
    </span>
  );
}

// ── Wizard ───────────────────────────────────────────────────────────────────

function SetupWizard({ info }: { info: SetupStatus }) {
  // Steps are fixed when the wizard opens so completing the admin step (which
  // flips `initialized`) or loading sample data does not remove steps while
  // the user is standing on them.
  const steps = useMemo(() => buildSetupSteps(info), [info]);
  const [stepIndex, setStepIndex] = useState(0);
  const index = Math.min(stepIndex, steps.length - 1);
  const current = steps[index];

  const goNext = () => setStepIndex((i) => Math.min(i + 1, steps.length - 1));
  const goBack = () => setStepIndex((i) => Math.max(i - 1, 0));

  return (
    <SetupFrame
      hue={STEP_HUES[Math.min(index, STEP_HUES.length - 1)]}
      numeral={pad(index)}
      tone={current === "done" ? "ok" : "default"}
      steps={steps}
      current={current}
    >
      {current === "welcome" ? <WelcomeStep onNext={goNext} /> : null}
      {current === "environment" ? <EnvironmentStep info={info} onNext={goNext} onBack={goBack} /> : null}
      {current === "admin" ? <AdminStep onNext={goNext} onBack={goBack} /> : null}
      {current === "sample" ? <SampleDataStep onNext={goNext} /> : null}
      {current === "done" ? <DoneStep info={info} /> : null}
    </SetupFrame>
  );
}

function ArrowIcon() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M5 12h14" />
      <path d="m13 6 6 6-6 6" />
    </svg>
  );
}

function WelcomeStep({ onNext }: { onNext: () => void }) {
  return (
    <>
      <section className="setup__content">
        <span className="setup__eyebrow rise">Welcome</span>
        <h1 id="setup-title" className="setup__title">
          <Line>Your data,</Line>
          <Line delay={0.1}>one place</Line>
          <Line delay={0.2}>to shape it.</Line>
        </h1>
        <p className="setup__lede rise d2">
          This installation has not been set up yet. A few quick steps: check your setup, create an
          administrator account and, if you like, load a demo workspace.
        </p>
      </section>
      <aside className="setup__actions rise d3">
        <div className="setup__row setup__row--end">
          <button type="button" className="setup-btn setup-btn--primary" onClick={onNext} autoFocus>
            Get started <ArrowIcon />
          </button>
        </div>
      </aside>
    </>
  );
}

function formatDatabase(provider: string): string {
  const key = provider.toLowerCase();
  if (key === "sqlite") return "SQLite";
  if (key === "postgres" || key === "postgresql") return "PostgreSQL";
  return provider;
}

function formatSignIn(authMode: string): string {
  if (authMode === "standard") return "Accounts required";
  if (authMode === "open") return "Open access";
  return authMode;
}

function EnvironmentStep({ info, onNext, onBack }: { info: SetupStatus; onNext: () => void; onBack: () => void }) {
  const facts: Array<[string, string]> = [
    ["Database", formatDatabase(info.database_provider)],
    ["Sign-in", formatSignIn(info.auth_mode)],
    ["Version", info.version]
  ];
  return (
    <>
      <section className="setup__content">
        <span className="setup__eyebrow rise">Environment</span>
        <h1 id="setup-title" className="setup__title setup__title--md">
          <Line>Your</Line>
          <Line delay={0.1}>installation.</Line>
        </h1>
        <dl className="setup__readout rise d2">
          {facts.map(([label, value], i) => (
            <div key={label} className="setup__fact">
              <dt>{label}</dt>
              <dd style={{ "--len": value.length, animationDelay: `${0.3 + i * 0.5}s, 0s` } as CSSProperties}>{value}</dd>
            </div>
          ))}
        </dl>
        <p className="setup__note rise d3">Set in the environment or Docker Compose; not editable here.</p>
      </section>
      <aside className="setup__actions rise d3">
        <div className="setup__row setup__row--end">
          <button type="button" className="setup-btn setup-btn--ghost" onClick={onBack}>
            Back
          </button>
          <button type="button" className="setup-btn setup-btn--primary" onClick={onNext} autoFocus>
            Continue
          </button>
        </div>
      </aside>
    </>
  );
}

function Field({
  id,
  label,
  error,
  ...input
}: { id: string; label: string; error?: string | null } & InputHTMLAttributes<HTMLInputElement>) {
  return (
    <div className="setup-field">
      <label htmlFor={id}>{label}</label>
      <input id={id} aria-invalid={error ? true : undefined} aria-describedby={error ? `${id}-error` : undefined} {...input} />
      {error ? (
        <p id={`${id}-error`} className="setup__error" role="alert">
          {error}
        </p>
      ) : null}
    </div>
  );
}

function AdminStep({ onNext, onBack }: { onNext: () => void; onBack: () => void }) {
  const login = useAuthStore((state) => state.login);
  const markInitialized = useSetupStore((state) => state.markInitialized);
  const reloadStatus = useSetupStore((state) => state.load);
  const ids = useId();

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
          : describeError(cause, "Couldn't create the account. Check your connection and try again.")
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
      <section className="setup__content">
        <span className="setup__eyebrow rise">Admin account</span>
        <h1 id="setup-title" className="setup__title setup__title--md">
          <Line>The first account</Line>
          <Line delay={0.1}>runs the place.</Line>
        </h1>
        <p className="setup__lede rise d2">
          It becomes the administrator and signs you in right away.
        </p>
      </section>
      <aside className="setup__actions rise d2">
        <form className="setup__form" onSubmit={handleSubmit} noValidate>
          {error ? (
            <p className="setup__error" role="alert">
              {error}
            </p>
          ) : null}
          <Field
            id={`${ids}-username`}
            label="Username"
            name="username"
            autoComplete="username"
            placeholder="admin"
            value={username}
            onChange={(event) => setUsername(event.target.value)}
            disabled={submitting}
            required
            autoFocus
          />
          <Field
            id={`${ids}-password`}
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
          <Field
            id={`${ids}-confirm`}
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
          <div className="setup__row setup__row--end">
            <button type="button" className="setup-btn setup-btn--ghost" onClick={onBack} disabled={submitting}>
              Back
            </button>
            {bootstrapClosed ? (
              <Link className="setup-btn setup-btn--primary" to="/login">
                Go to sign in
              </Link>
            ) : (
              <button type="submit" className="setup-btn setup-btn--accent" disabled={!canSubmit}>
                {submitting ? "Creating account…" : "Create account and continue"}
              </button>
            )}
          </div>
        </form>
      </aside>
    </>
  );
}

const SAMPLE_TABLES = ["customers", "orders", "products"];

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
      setError(describeError(cause, "Couldn't load the demo. Check your connection and try again."));
    } finally {
      setLoading(false);
    }
  }

  const totalRows = result ? Object.values(result.rows_inserted).reduce((sum, count) => sum + count, 0) : 0;
  const tables = result ? result.tables_created : SAMPLE_TABLES;

  return (
    <>
      <section className="setup__content">
        <span className="setup__eyebrow rise">Sample data</span>
        <h1 id="setup-title" className="setup__title setup__title--md">
          <Line>Start with</Line>
          <Line delay={0.1}>something to see.</Line>
        </h1>
        <div className="setup__constellation rise d2" aria-hidden="true">
          {result ? (
            <svg className="setup__wires" viewBox="0 0 560 200" xmlns="http://www.w3.org/2000/svg">
              <path className="setup__wire" d="M 150 40 C 220 40, 200 130, 240 130" />
              <path className="setup__wire setup__wire--2" d="M 380 130 C 430 130, 400 30, 430 30" />
            </svg>
          ) : null}
          {tables.slice(0, 3).map((table, i) => (
            <div key={table} className={`setup__node setup__node--${i + 1}`}>
              <span>{table}</span>
              {result ? <span className="setup__node-count">{result.rows_inserted[table] ?? 0} rows</span> : null}
            </div>
          ))}
        </div>
      </section>
      <aside className="setup__actions rise d3">
        {error ? (
          <p className="setup__error" role="alert">
            {error}
          </p>
        ) : null}
        {result ? (
          <div className="setup__stack setup__stack--end">
            <div className="setup__success">
              <span className="setup__tick" aria-hidden="true">
                <Icon name="check" size={12} />
              </span>
              <span>
                <strong>Demo workspace loaded.</strong>{" "}
                {result.tables_created.length} tables ({result.tables_created.join(", ")}), {totalRows} rows.
              </span>
            </div>
            <button type="button" className="setup-btn setup-btn--primary" onClick={onNext} autoFocus>
              Continue
            </button>
          </div>
        ) : (
          <div className="setup__stack setup__stack--end">
            <p className="setup__note setup__note--right">
              A small demo workspace: customers, orders and products with relations and an order
              status made for a kanban board. Only possible while the installation is empty.
            </p>
            <div className="setup__row setup__row--end">
              <button type="button" className="setup-btn setup-btn--ghost" onClick={onNext} disabled={loading}>
                Skip for now
              </button>
              <button type="button" className="setup-btn setup-btn--accent" onClick={() => void handleLoad()} disabled={loading}>
                {loading ? (
                  <>
                    <span className="setup__spin" aria-hidden="true" />
                    Loading the demo…
                  </>
                ) : (
                  <>
                    <Icon name="database" size={14} />
                    Load sample data
                  </>
                )}
              </button>
            </div>
          </div>
        )}
      </aside>
    </>
  );
}

function DoneStep({ info }: { info: SetupStatus }) {
  const navigate = useNavigate();
  const completeWizard = useSetupStore((state) => state.completeWizard);
  const authStatus = useAuthStore((state) => state.status);
  const user = useAuthStore((state) => state.user);
  const signedIn = authStatus === "authenticated" || !info.auth_required;

  function finish() {
    completeWizard();
    navigate("/", { replace: true });
  }

  return (
    <>
      <section className="setup__content">
        <span className="setup__eyebrow setup__eyebrow--ok rise">Done</span>
        <h1 id="setup-title" className="setup__title setup__title--xl">
          <Line>Ready.</Line>
        </h1>
        <p className="setup__lede rise d2">
          {signedIn
            ? "Open the workspace to design your schema and start working with data."
            : "Sign in with the administrator account you just created to open the workspace."}
        </p>
      </section>
      <aside className="setup__actions rise d3">
        <div className="setup__row setup__row--end">
          {user ? <span className="setup__hint">signed in as {user.username}</span> : null}
          <button type="button" className="setup-btn setup-btn--primary" onClick={finish} autoFocus>
            {signedIn ? "Open workspace" : "Go to sign in"} <ArrowIcon />
          </button>
        </div>
      </aside>
    </>
  );
}
