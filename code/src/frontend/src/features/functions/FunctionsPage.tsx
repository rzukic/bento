import { isInstallationAdmin } from "../../types/auth";
import { useCallback, useEffect, useMemo, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { ApiClientError } from "../../api/client";
import { listCredentials, type Credential } from "../../api/credentials";
import {
  ARGUMENT_TYPES,
  RESULT_TYPES,
  createFunction,
  deleteFunction,
  getFunctionStatus,
  getFunctionVersions,
  listFunctions,
  listFunctionTemplates,
  testFunction,
  updateFunction,
  type ArgumentType,
  type CustomFunction,
  type FunctionDraft,
  type FunctionTemplate,
  type FunctionStatus,
  type FunctionTestResult,
  type FunctionType,
  type FunctionVersion,
} from "../../api/functions";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { ConfirmDialog } from "../../components/ui/Dialog";
import { Spinner } from "../../components/ui/Spinner";
import { useAuthStore } from "../../stores/authStore";
import { CodeEditor } from "./CodeEditor";
import { CredentialsPanel } from "./CredentialsPanel";
import { PriceSourcesPanel } from "./PriceSourcesPanel";
import "./Functions.css";

const NEW = "__new";

const TYPE_LABELS: Record<ArgumentType, string> = {
  float: "Number",
  integer: "Whole number",
  currency: "Amount",
  text: "Text",
  boolean: "Yes/no",
  date: "Date",
  datetime: "Date and time",
  any: "Any (JSON)",
};

const STARTER: FunctionDraft & { name: string } = {
  name: "",
  description: "",
  arguments: [{ name: "value", type: "float" }],
  resultType: "text",
  code: '// The arguments are the function\'s parameters; return the result.\nif (value === null) return null;\nreturn value > 0 ? "positive" : "zero or negative";',
  allowedHosts: [],
  credentials: [],
  mode: "row",
};

type Draft = FunctionDraft & { name: string };

function toDraft(fn: CustomFunction): Draft {
  return {
    name: fn.name,
    description: fn.description ?? "",
    arguments: fn.arguments.map((a) => ({ ...a })),
    resultType: fn.resultType,
    code: fn.code,
    allowedHosts: [...fn.allowedHosts],
    credentials: [...fn.credentials],
    mode: fn.mode ?? "row",
  };
}

function sameDraft(a: Draft, b: Draft): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}

function message(cause: unknown, fallback: string): string {
  if (cause instanceof ApiClientError) return cause.status === 403 ? "Only admins change functions." : cause.message;
  return fallback;
}

/** CUSTOM("name", arg, …) as a formula would call it; CUSTOM_ROWS(…) for functions over rows. */
function callSnippet(draft: Draft): string {
  const args = draft.arguments.map((a) => `, ${a.name}`).filter((a) => a !== ", ").join("");
  return draft.mode === "rows"
    ? `CUSTOM_ROWS("${draft.name || "name"}", PARTITION(isin), ORDER(date, id)${args})`
    : `CUSTOM("${draft.name || "name"}"${args})`;
}

/** Rows mode: a test input is a JSON list, a value per row (a single value counts as one row). */
function testList(raw: string, type: ArgumentType): unknown[] {
  try {
    const parsed: unknown = JSON.parse(raw.trim() || "[]");
    return Array.isArray(parsed) ? parsed : [parsed];
  } catch {
    // not JSON: one row below
  }
  return [testValue(raw, type)];
}

/** A test input as the value the API sends: numbers, yes/no, text, empty as null. */
function testValue(raw: string, type: ArgumentType): unknown {
  const text = raw.trim();
  if (text === "") return null;
  if (type === "float" || type === "integer" || type === "currency") {
    const n = Number(text.replace(",", "."));
    return Number.isFinite(n) ? n : text;
  }
  if (type === "boolean") return text === "true";
  if (type === "any") {
    try {
      return JSON.parse(text) as string | number | boolean | null;
    } catch {
      // not JSON: plain text below
    }
    const n = Number(text);
    if (text !== "" && Number.isFinite(n)) return n;
    if (text === "true" || text === "false") return text === "true";
  }
  return raw;
}

/**
 * Functions (Step 4.1): your own JavaScript, called from formulas with
 * CUSTOM("name", …). Write the body, test it with values, and see where it
 * is used and whether stored values failed.
 */
export function FunctionsPage() {
  const isAdmin = useAuthStore((state) => state.user?.role === "admin");
  // The vault is the installation's (Step 5.5): only its admins see and fill it.
  const vaultAdmin = useAuthStore((state) => isInstallationAdmin(state.user));
  const [searchParams, setSearchParams] = useSearchParams();
  const [functions, setFunctions] = useState<CustomFunction[] | null>(null);
  const [status, setStatus] = useState<FunctionStatus | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);

  const selected = searchParams.get("fn");
  const tab = searchParams.get("tab") === "prices" ? "prices" : searchParams.get("tab") === "credentials" ? "credentials" : "functions";
  const selectTab = (next: string) => {
    const params = new URLSearchParams();
    if (next !== "functions") params.set("tab", next);
    setSearchParams(params, { replace: true });
  };
  const select = useCallback(
    (name: string | null) => {
      const params = new URLSearchParams(searchParams);
      if (name) params.set("fn", name);
      else params.delete("fn");
      setSearchParams(params, { replace: true });
    },
    [searchParams, setSearchParams],
  );

  const reload = useCallback(async () => {
    try {
      const [list, current] = await Promise.all([listFunctions(), getFunctionStatus()]);
      setFunctions(list);
      setStatus(current);
      setLoadError(null);
      return list;
    } catch (cause) {
      setFunctions((previous) => previous ?? []);
      setLoadError(message(cause, "Functions could not be loaded."));
      return null;
    }
  }, []);

  useEffect(() => {
    void reload();
  }, [reload]);

  // While stored values are being recalculated, check again shortly.
  useEffect(() => {
    if (!status?.pending) return;
    const timer = window.setTimeout(() => {
      void getFunctionStatus().then(setStatus).catch(() => undefined);
    }, 1500);
    return () => window.clearTimeout(timer);
  }, [status]);

  const current = functions?.find((f) => f.name === selected) ?? null;
  const creating = selected === NEW;
  const showEditor = creating || current !== null;

  return (
    <div className="functions-page">
      <header className="functions-page__header">
        <h1 className="functions-page__title">Functions</h1>
        <p className="functions-page__lead">
          Your own JavaScript: called from formulas with <code>CUSTOM("name", …)</code>, or fetching prices from an API of your choice.
        </p>
      </header>

      <div className="functions-tabs" role="tablist" aria-label="Functions">
        {[
          ["functions", "Functions"],
          ["prices", "Price sources"],
          ...(vaultAdmin ? [["credentials", "Credentials"]] : []),
        ].map(([id, label]) => (
          <button key={id} type="button" role="tab" aria-selected={tab === id} className="functions-tabs__tab" onClick={() => selectTab(id)}>
            {label}
          </button>
        ))}
      </div>

      {tab === "prices" ? (
        <PriceSourcesPanel functions={functions ?? []} isAdmin={isAdmin} />
      ) : tab === "credentials" && vaultAdmin ? (
        <CredentialsPanel />
      ) : (
      <>
      {loadError ? <Alert tone="error" message={loadError} /> : null}

      <div className="functions-layout">
        <aside className="functions-list" aria-label="Functions">
          <div className="functions-list__head">
            <span className="section-label">Your functions</span>
            {isAdmin ? (
              <Button type="button" size="sm" variant="ghost" leadingIcon={<Icon name="plus" size={13} />} onClick={() => select(NEW)}>
                New
              </Button>
            ) : null}
          </div>
          {functions === null ? (
            <Spinner label="Loading functions" />
          ) : functions.length === 0 ? (
            <p className="functions-list__empty">No functions yet.{isAdmin ? " Create one to call it from formulas." : ""}</p>
          ) : (
            <ul className="functions-list__items">
              {functions.map((fn) => {
                const failures = status?.failures.filter((f) => f.function === fn.name) ?? [];
                return (
                  <li key={fn.name}>
                    <button
                      type="button"
                      className="functions-list__item"
                      aria-current={fn.name === selected ? "true" : undefined}
                      onClick={() => select(fn.name)}
                    >
                      <span className="functions-list__name mono">{fn.name}</span>
                      <span className="functions-list__signature mono">
                        ({fn.arguments.map((a) => a.name).join(", ")}) → {TYPE_LABELS[fn.resultType].toLowerCase()}
                      </span>
                      {fn.allowedHosts.length > 0 ? <span className="functions-list__net">Calls {fn.allowedHosts.join(", ")}</span> : null}
                      <span className="functions-list__meta">
                        {fn.usedBy.length === 0 ? "Not used yet" : `Used by ${fn.usedBy.length}`}
                        {failures.length > 0 ? <span className="functions-list__failed"> · failed on {failures.reduce((n, f) => n + f.rows, 0)} rows</span> : null}
                      </span>
                    </button>
                  </li>
                );
              })}
            </ul>
          )}
        </aside>

        <section className="functions-main">
          {showEditor ? (
            <FunctionEditor
              key={creating ? NEW : current!.name}
              fn={creating ? null : current}
              status={status}
              readOnly={!isAdmin}
              onSaved={async (name) => {
                await reload();
                select(name);
              }}
              onDeleted={async () => {
                await reload();
                select(null);
              }}
              onCancelNew={() => select(null)}
            />
          ) : (
            <FunctionsIntro canCreate={isAdmin} hasFunctions={(functions?.length ?? 0) > 0} onCreate={() => select(NEW)} />
          )}
        </section>
      </div>
      </>
      )}
    </div>
  );
}

function FunctionsIntro({ canCreate, hasFunctions, onCreate }: { canCreate: boolean; hasFunctions: boolean; onCreate: () => void }) {
  return (
    <div className="functions-intro">
      <h2>What functions are for</h2>
      <p>
        Formulas cover sums, conditions, dates and lookups. When you need more — a rule with many cases, a calculation in several
        steps, text parsing — write it once as a function and call it from any formula.
      </p>
      <pre className="functions-intro__example mono">
        {'// risk_class(volatility) → text\nif (volatility === null) return null;\nif (volatility > 0.3) return "high";\nreturn volatility > 0.15 ? "medium" : "low";'}
      </pre>
      <p>
        Use it as a computed column, <code>CUSTOM("risk_class", volatility)</code>: values are stored and recalculated in the
        background when rows or the function change. In dashboards, <code>CUSTOM("score", SUM(amount))</code> runs on each result.
      </p>
      <p className="functions-intro__note">
        Functions run sandboxed on the server: no files, no access to Bento, at most 1 s per call (5 s for functions over rows, 30 s with network). A function may
        call the hosts you list with <code>fetch</code> — then it runs as an <strong>import source</strong> (any API into a table, on a schedule) or a{" "}
        <strong>price source</strong> rather than in formulas.
      </p>
      {canCreate ? (
        <Button type="button" variant="primary" leadingIcon={<Icon name="plus" size={14} />} onClick={onCreate}>
          {hasFunctions ? "New function" : "Create your first function"}
        </Button>
      ) : null}
    </div>
  );
}

type FunctionEditorProps = {
  fn: CustomFunction | null;
  status: FunctionStatus | null;
  readOnly: boolean;
  onSaved: (name: string) => Promise<void>;
  onDeleted: () => Promise<void>;
  onCancelNew: () => void;
};

function FunctionEditor({ fn, status, readOnly, onSaved, onDeleted, onCancelNew }: FunctionEditorProps) {
  const initial = useMemo(() => (fn ? toDraft(fn) : { ...STARTER, arguments: STARTER.arguments.map((a) => ({ ...a })) }), [fn]);
  const [draft, setDraft] = useState<Draft>(initial);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [testInputs, setTestInputs] = useState<string[]>(() => initial.arguments.map(() => ""));
  const [testing, setTesting] = useState(false);
  const [result, setResult] = useState<FunctionTestResult | null>(null);
  const [versions, setVersions] = useState<FunctionVersion[] | null>(null);
  const [copied, setCopied] = useState(false);
  const [templates, setTemplates] = useState<FunctionTemplate[]>([]);
  const [stored, setStored] = useState<Credential[]>([]);
  const [hostInput, setHostInput] = useState("");

  useEffect(() => {
    if (readOnly) return;
    if (!fn) void listFunctionTemplates().then(setTemplates).catch(() => setTemplates([]));
    void listCredentials().then(setStored).catch(() => setStored([]));
  }, [fn, readOnly]);

  const networked = draft.allowedHosts.length > 0;
  const overRows = draft.mode === "rows";
  const inFormulas = !networked && draft.resultType !== "any";
  const dirty = !sameDraft(draft, initial);
  const failures = fn ? (status?.failures.filter((f) => f.function === fn.name) ?? []) : [];
  const snippet = callSnippet(draft);

  const update = (changes: Partial<Draft>) => {
    setDraft((d) => ({ ...d, ...changes }));
    setError(null);
  };
  const setArgument = (index: number, changes: Partial<Draft["arguments"][number]>) =>
    update({ arguments: draft.arguments.map((a, i) => (i === index ? { ...a, ...changes } : a)) });
  const addArgument = () => {
    const used = new Set(draft.arguments.map((a) => a.name));
    let n = draft.arguments.length + 1;
    while (used.has(`arg${n}`)) n++;
    update({ arguments: [...draft.arguments, { name: `arg${n}`, type: "float" }] });
    setTestInputs((inputs) => [...inputs, ""]);
  };
  const removeArgument = (index: number) => {
    update({ arguments: draft.arguments.filter((_, i) => i !== index) });
    setTestInputs((inputs) => inputs.filter((_, i) => i !== index));
  };

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      const body: FunctionDraft = {
        name: draft.name.trim(),
        description: draft.description?.trim() || null,
        arguments: draft.arguments.map((a) => ({ name: a.name.trim(), type: a.type })),
        resultType: draft.resultType,
        code: draft.code,
        allowedHosts: draft.allowedHosts,
        credentials: draft.credentials,
        mode: draft.mode ?? "row",
      };
      const saved = fn ? await updateFunction(fn.name, body) : await createFunction(body);
      setVersions(null);
      await onSaved(saved.name);
    } catch (cause) {
      setError(message(cause, "The function could not be saved."));
    } finally {
      setSaving(false);
    }
  };

  const remove = async () => {
    setConfirmDelete(false);
    if (!fn) return;
    try {
      await deleteFunction(fn.name);
      await onDeleted();
    } catch (cause) {
      setError(message(cause, "The function could not be deleted."));
    }
  };

  const runTest = async () => {
    setTesting(true);
    try {
      const rows = draft.mode === "rows";
      setResult(await testFunction(draft, draft.arguments.map((a, i) => (rows ? testList : testValue)(testInputs[i] ?? "", a.type))));
    } catch (cause) {
      setResult({ value: null, resultType: draft.resultType, error: message(cause, "The test could not run."), logs: [], durationMs: 0, requests: 0 });
    } finally {
      setTesting(false);
    }
  };

  const loadVersions = async () => {
    if (!fn) return;
    try {
      setVersions(await getFunctionVersions(fn.name));
    } catch (cause) {
      setError(message(cause, "The history could not be loaded."));
    }
  };

  const applyTemplate = (template: FunctionTemplate) => {
    setDraft({
      name: draft.name.trim() || template.name,
      description: template.description,
      arguments: template.arguments.map((a) => ({ ...a })),
      resultType: template.resultType,
      code: template.code,
      allowedHosts: [...template.allowedHosts],
      credentials: [...template.credentials],
      mode: template.mode ?? "row",
    });
    setTestInputs(template.arguments.map(() => ""));
    setResult(null);
  };

  const addHost = () => {
    const host = hostInput.trim().toLowerCase().replace(/^https?:\/\//, "").replace(/\/.*$/, "");
    if (host && !draft.allowedHosts.includes(host)) update({ allowedHosts: [...draft.allowedHosts, host] });
    setHostInput("");
  };

  const copySnippet = async () => {
    try {
      await navigator.clipboard.writeText(snippet);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1500);
    } catch {
      setCopied(false);
    }
  };

  return (
    <div className="function-editor">
      <header className="function-editor__header">
        {fn ? (
          <div className="function-editor__title-row">
            <h2 className="function-editor__name mono">{fn.name}</h2>
            <span className="badge" title={`Saved ${new Date(fn.updatedAt).toLocaleString()}`}>
              Version {fn.version}
            </span>
            {dirty ? <span className="function-editor__dirty">Unsaved changes</span> : null}
          </div>
        ) : (
          <label className="function-editor__field">
            <span className="function-editor__label">Name</span>
            <input
              className="field__control mono"
              value={draft.name}
              placeholder="risk_class"
              aria-label="Function name"
              autoFocus
              onChange={(event) => update({ name: event.target.value.toLowerCase().replace(/[^a-z0-9_]/g, "_") })}
            />
            <span className="function-editor__hint">Small letters, digits and _. Formulas call it by this name; it cannot change later.</span>
          </label>
        )}
        {!fn && templates.length > 0 ? (
          <div className="function-templates" role="group" aria-label="Start from a template">
            <span className="function-editor__label">Start from</span>
            {templates.map((template) => (
              <button key={template.id} type="button" className="function-templates__item" title={template.description} onClick={() => applyTemplate(template)}>
                <Icon name="braces" size={12} />
                {template.title}
              </button>
            ))}
          </div>
        ) : null}
        <label className="function-editor__field">
          <span className="function-editor__label">Description</span>
          <input
            className="field__control"
            value={draft.description ?? ""}
            placeholder="What it computes, for whoever reads the formula"
            aria-label="Description"
            disabled={readOnly}
            onChange={(event) => update({ description: event.target.value })}
          />
        </label>
      </header>

      <section className="function-editor__section" aria-label="Signature">
        <div className="function-editor__section-head">
          <span className="section-label">Arguments</span>
          {!readOnly ? (
            <Button type="button" size="sm" variant="ghost" leadingIcon={<Icon name="plus" size={12} />} onClick={addArgument} disabled={draft.arguments.length >= 10}>
              Argument
            </Button>
          ) : null}
        </div>
        {draft.arguments.length === 0 ? <p className="function-editor__hint">No arguments: the function always gives the same value.</p> : null}
        <ol className="function-args">
          {draft.arguments.map((argument, index) => (
            <li key={index} className="function-args__row">
              <input
                className="field__control field__control--compact mono"
                value={argument.name}
                aria-label={`Name of argument ${index + 1}`}
                disabled={readOnly}
                onChange={(event) => setArgument(index, { name: event.target.value.replace(/[^A-Za-z0-9_$]/g, "") })}
              />
              <select
                className="field__control field__control--compact"
                value={argument.type}
                aria-label={`Type of ${argument.name || `argument ${index + 1}`}`}
                disabled={readOnly}
                onChange={(event) => setArgument(index, { type: event.target.value as ArgumentType })}
              >
                {ARGUMENT_TYPES.map((type) => (
                  <option key={type} value={type}>
                    {TYPE_LABELS[type]}
                  </option>
                ))}
              </select>
              {!readOnly ? (
                <Button type="button" size="icon" variant="ghost" aria-label={`Remove ${argument.name || `argument ${index + 1}`}`} onClick={() => removeArgument(index)}>
                  <Icon name="x" size={13} />
                </Button>
              ) : null}
            </li>
          ))}
        </ol>
        <label className="function-editor__field function-editor__field--inline">
          <span className="function-editor__label">Gives</span>
          <select
            className="field__control field__control--compact"
            value={draft.resultType}
            aria-label="Result type"
            disabled={readOnly}
            onChange={(event) => update({ resultType: event.target.value as FunctionType })}
          >
            {RESULT_TYPES.filter((type) => !overRows || type !== "any").map((type) => (
              <option key={type} value={type}>
                {TYPE_LABELS[type]}
              </option>
            ))}
          </select>
        </label>
        <label className="function-editor__check">
          <input
            type="checkbox"
            checked={overRows}
            disabled={readOnly || networked || draft.resultType === "any"}
            onChange={(event) => update({ mode: event.target.checked ? "rows" : "row" })}
          />
          <span>
            Runs over a group of rows
            <span className="function-editor__hint">
              {networked || draft.resultType === "any"
                ? " — only for functions without internet that give a declared type."
                : " — once per group, in order: each argument is a list, and the function returns a list with a value per row. For running values such as the average cost of a position; called with CUSTOM_ROWS."}
            </span>
          </span>
        </label>
      </section>

      <section className="function-editor__section" aria-label="Code">
        <div className="function-editor__section-head">
          <span className="section-label">Code</span>
          <span className="function-editor__hint">
            {overRows ? "Each argument is a list with a value per row, in order; return a list as long. " : ""}
            Numbers arrive as numbers, dates as "yyyy-MM-dd" text, empty values as null. Return a Date or "yyyy-MM-dd" for dates.
          </span>
        </div>
        <div className="function-code">
          <div className="function-code__frame mono" aria-hidden="true">
            function {draft.name || "name"}({draft.arguments.map((a) => a.name).join(", ")}) {"{"}
          </div>
          <CodeEditor value={draft.code} onChange={(code) => update({ code })} readOnly={readOnly} />
          <div className="function-code__frame mono" aria-hidden="true">
            {"}"}
          </div>
        </div>
      </section>

      {!overRows ? (
      <section className="function-editor__section" aria-label="Internet">
        <div className="function-editor__section-head">
          <span className="section-label">Internet</span>
          <span className="function-editor__hint">
            {networked
              ? "fetch may call these hosts (https only, never the server's own network). Such functions run as price sources, not in formulas."
              : "No network: the function only computes. Add a host to call an API with fetch."}
          </span>
        </div>
        {draft.allowedHosts.length > 0 ? (
          <ul className="function-hosts" aria-label="Allowed hosts">
            {draft.allowedHosts.map((host) => (
              <li key={host} className="function-hosts__item mono">
                {host}
                {!readOnly ? (
                  <button type="button" aria-label={`Remove ${host}`} onClick={() => update({ allowedHosts: draft.allowedHosts.filter((h) => h !== host), credentials: draft.allowedHosts.length === 1 ? [] : draft.credentials })}>
                    <Icon name="x" size={11} />
                  </button>
                ) : null}
              </li>
            ))}
          </ul>
        ) : null}
        {!readOnly ? (
          <div className="function-hosts__add">
            <input
              className="field__control field__control--compact mono"
              value={hostInput}
              placeholder="api.example.com"
              aria-label="Add a host"
              onChange={(event) => setHostInput(event.target.value)}
              onKeyDown={(event) => {
                if (event.key === "Enter") {
                  event.preventDefault();
                  addHost();
                }
              }}
            />
            <Button type="button" size="sm" variant="ghost" onClick={addHost} disabled={!hostInput.trim() || draft.allowedHosts.length >= 10}>
              Allow host
            </Button>
          </div>
        ) : null}
        {networked ? (
          <div className="function-credentials" role="group" aria-label="Credentials">
            <span className="function-editor__label">Credentials</span>
            {stored.length === 0 && draft.credentials.length === 0 ? (
              <span className="function-editor__hint">None stored. API keys go under the Credentials tab; the code reads them as credentials.name.</span>
            ) : (
              [...new Set([...stored.map((c) => c.name), ...draft.credentials])].map((name) => (
                <label key={name} className="function-credentials__item">
                  <input
                    type="checkbox"
                    checked={draft.credentials.includes(name)}
                    disabled={readOnly}
                    onChange={(event) =>
                      update({ credentials: event.target.checked ? [...draft.credentials, name] : draft.credentials.filter((c) => c !== name) })
                    }
                  />
                  <span className="mono">credentials.{name}</span>
                </label>
              ))
            )}
          </div>
        ) : null}
      </section>
      ) : null}

      {!readOnly ? (
        <section className="function-editor__section function-test" aria-label="Test">
          <div className="function-editor__section-head">
            <span className="section-label">Try it</span>
            <span className="function-editor__hint">
              Runs the code above in the sandbox; nothing is saved.{overRows ? " Give each argument a list, e.g. [10, -5]." : ""}
            </span>
          </div>
          <div className="function-test__inputs">
            {draft.arguments.map((argument, index) => (
              <label key={index} className="function-editor__field">
                <span className="function-editor__label mono">{argument.name || `argument ${index + 1}`}</span>
                {argument.type === "boolean" && !overRows ? (
                  <select
                    className="field__control field__control--compact"
                    value={testInputs[index] ?? ""}
                    aria-label={`Test value for ${argument.name}`}
                    onChange={(event) => setTestInputs((inputs) => inputs.map((v, i) => (i === index ? event.target.value : v)))}
                  >
                    <option value="">empty</option>
                    <option value="true">true</option>
                    <option value="false">false</option>
                  </select>
                ) : (
                  <input
                    className="field__control field__control--compact"
                    type={argument.type === "date" && !overRows ? "date" : "text"}
                    value={testInputs[index] ?? ""}
                    placeholder={
                      overRows
                        ? argument.type === "text" || argument.type === "date"
                          ? '["a", "b"]'
                          : "[1, 2]"
                        : argument.type === "text"
                          ? "text"
                          : argument.type === "any"
                            ? '["VWCE.DE"]'
                            : "0"
                    }
                    aria-label={`Test value for ${argument.name}`}
                    onChange={(event) => setTestInputs((inputs) => inputs.map((v, i) => (i === index ? event.target.value : v)))}
                  />
                )}
              </label>
            ))}
            <Button type="button" onClick={runTest} disabled={testing} leadingIcon={<Icon name="arrow-right" size={13} />}>
              {testing ? "Running…" : "Run"}
            </Button>
          </div>
          {result ? (
            <div className={result.error ? "function-test__result function-test__result--error" : "function-test__result"} role="status">
              {result.error ? (
                <span>{result.error}</span>
              ) : (
                <span>
                  <span className="function-test__arrow">→</span>{" "}
                  {result.value !== null && typeof result.value === "object" ? (
                    <pre className="function-test__json mono">{JSON.stringify(result.value, null, 2)}</pre>
                  ) : (
                    <span className="mono">{result.value === null ? "empty" : typeof result.value === "string" ? `"${result.value}"` : String(result.value)}</span>
                  )}
                </span>
              )}
              <span className="function-test__duration">
                {result.requests > 0 ? `${result.requests} ${result.requests === 1 ? "request" : "requests"} · ` : ""}
                {result.durationMs} ms
              </span>
              {result.logs.length > 0 ? (
                <pre className="function-test__logs mono" aria-label="console.log output">
                  {result.logs.join("\n")}
                </pre>
              ) : null}
            </div>
          ) : null}
        </section>
      ) : null}

      <section className="function-editor__section" aria-label="Use it">
        <div className="function-editor__section-head">
          <span className="section-label">Use it</span>
        </div>
        {!inFormulas ? (
          <p className="function-editor__hint">
            {networked ? "This function calls the internet" : "This function gives any JSON value"}, so it runs as an import source — a table's Import →{" "}
            <em>From a function</em> (no argument or one: when the import last worked → a list of rows) — or a{" "}
            <Link to="/functions?tab=prices">price source</Link> (prices: arguments symbols, from, to → a list of {"{ symbol, date, price }"};
            symbols: keys → {"{ key: symbol }"}), not in formulas.
          </p>
        ) : (
        <>
        <div className="function-snippet">
          <code className="mono">{snippet}</code>
          <Button type="button" size="sm" variant="ghost" leadingIcon={<Icon name={copied ? "check" : "copy"} size={12} />} onClick={copySnippet}>
            {copied ? "Copied" : "Copy"}
          </Button>
        </div>
        {overRows ? (
          <p className="function-editor__hint">
            In a computed column, as the whole formula: rows with the same PARTITION values form a group, ORDER sorts them (then the key), and each row
            gets its value. The values are stored and recalculated in the background when a row of the group changes.
          </p>
        ) : (
          <p className="function-editor__hint">
            As a computed column, the formula is the whole call; build on its result in another column. In dashboards, pass measures:{" "}
            <code>CUSTOM("{draft.name || "name"}", SUM(amount))</code>.
          </p>
        )}
        </>
        )}
        {fn ? (
          fn.usedBy.length > 0 ? (
            <ul className="function-usages">
              {fn.usedBy.map((usage) => (
                <li key={`${usage.kind}:${usage.name}`}>
                  <Icon name={usage.kind === "column" ? "table" : usage.kind === "priceSource" ? "refresh" : usage.kind === "importSource" ? "download" : "dashboard"} size={13} />
                  {usage.kind === "importSource" && usage.table ? (
                    <Link to={`/data/${encodeURIComponent(usage.table)}/import`}>
                      {usage.name} → {usage.table}
                    </Link>
                  ) : usage.kind === "column" && usage.table ? (
                    <Link to={`/data/${encodeURIComponent(usage.table)}`}>{usage.name}</Link>
                  ) : usage.kind === "priceSource" ? (
                    <Link to="/functions?tab=prices">{usage.name}</Link>
                  ) : usage.dashboardId ? (
                    <Link to={`/dashboards/${usage.dashboardId}`}>{usage.name}</Link>
                  ) : (
                    usage.name
                  )}
                </li>
              ))}
            </ul>
          ) : (
            <p className="function-editor__hint">Not used in any computed column or dashboard yet.</p>
          )
        ) : null}
        {failures.map((failure) => (
          <Alert
            key={`${failure.table}.${failure.column}`}
            tone="warning"
            title={`${failure.table}.${failure.column}: failed on ${failure.rows} ${failure.rows === 1 ? "row" : "rows"}`}
            message={`${failure.error} The value stays empty until the function or the row changes.`}
          />
        ))}
        {status?.pending && fn && fn.usedBy.some((u) => u.kind === "column") ? (
          <p className="function-editor__hint" role="status">
            <Spinner label="Updating stored values" /> Updating stored values…
          </p>
        ) : null}
      </section>

      {fn ? (
        <section className="function-editor__section" aria-label="History">
          <div className="function-editor__section-head">
            <span className="section-label">History</span>
            {versions === null ? (
              <Button type="button" size="sm" variant="ghost" onClick={loadVersions}>
                Show versions
              </Button>
            ) : null}
          </div>
          {versions ? (
            <ol className="function-versions">
              {versions.map((version) => (
                <li key={version.version}>
                  <span className="mono">v{version.version}</span>
                  <span className="function-versions__when">{new Date(version.createdAt).toLocaleString()}</span>
                  {!readOnly && version.version !== fn.version ? (
                    <Button
                      type="button"
                      size="sm"
                      variant="ghost"
                      onClick={() => update({ code: version.code, arguments: version.arguments.map((a) => ({ ...a })), resultType: version.resultType })}
                    >
                      Load into editor
                    </Button>
                  ) : version.version === fn.version ? (
                    <span className="function-versions__current">current</span>
                  ) : null}
                </li>
              ))}
            </ol>
          ) : null}
        </section>
      ) : null}

      {error ? <Alert tone="error" message={error} /> : null}

      {!readOnly ? (
        <footer className="function-editor__footer">
          {fn ? (
            <Button type="button" variant="ghost" className="function-editor__delete" leadingIcon={<Icon name="trash" size={13} />} onClick={() => setConfirmDelete(true)}>
              Delete
            </Button>
          ) : null}
          <span className="function-editor__spacer" />
          {fn ? (
            <Button type="button" variant="ghost" disabled={!dirty || saving} onClick={() => setDraft(initial)}>
              Discard changes
            </Button>
          ) : (
            <Button type="button" variant="ghost" onClick={onCancelNew}>
              Cancel
            </Button>
          )}
          <Button type="button" variant="primary" disabled={saving || (!!fn && !dirty) || (!fn && !draft.name.trim())} onClick={save}>
            {saving ? "Saving…" : fn ? "Save" : "Create function"}
          </Button>
        </footer>
      ) : null}

      <ConfirmDialog
        open={confirmDelete}
        title={`Delete ${fn?.name ?? "function"}?`}
        description="Formulas that call it stop working. Computed columns that use it must be changed first."
        confirmLabel="Delete"
        confirmVariant="danger"
        onConfirm={remove}
        onCancel={() => setConfirmDelete(false)}
      />
    </div>
  );
}
