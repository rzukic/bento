import { useEffect, useMemo, useState } from "react";
import { getOpenApiDocument, sendRaw, type OpenApiDocument, type TryItResponse } from "../../api/connections";
import { ApiClientError } from "../../api/client";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { Spinner } from "../../components/ui/Spinner";
import { bodyFields, buildUrl, curlFor, exampleFor, groupOperations, requestBodySchema, type OperationEntry } from "./openApi";

const SCOPE_LABELS: Record<string, string> = {
  read: "Read keys",
  write: "Read-write keys",
  none: "No key needed",
};

/** `code` in descriptions as code; the rest as text (no HTML). */
function Inline({ text }: { text: string }) {
  return (
    <>
      {text.split(/(`[^`]+`)/g).map((part, index) =>
        part.startsWith("`") && part.endsWith("`") && part.length > 2 ? <code key={index}>{part.slice(1, -1)}</code> : <span key={index}>{part}</span>,
      )}
    </>
  );
}

function prettyJson(text: string): string {
  try {
    return JSON.stringify(JSON.parse(text), null, 2);
  } catch {
    return text;
  }
}

async function copy(text: string) {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    /* clipboard unavailable */
  }
}

export function ApiExplorer() {
  const [doc, setDoc] = useState<OpenApiDocument | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  const [open, setOpen] = useState<string | null>(null);
  const [mode, setMode] = useState<"session" | "key">("session");
  const [apiKey, setApiKey] = useState("");

  useEffect(() => {
    getOpenApiDocument()
      .then(setDoc)
      .catch((cause) => setError(cause instanceof ApiClientError ? cause.message : "The API description could not be loaded."));
  }, []);

  const groups = useMemo(() => {
    if (!doc) return [];
    const needle = search.trim().toLowerCase();
    return groupOperations(doc)
      .map((group) => ({
        ...group,
        operations: needle
          ? group.operations.filter((o) => `${o.method} ${o.path} ${o.operation.summary ?? ""} ${group.name}`.toLowerCase().includes(needle))
          : group.operations,
      }))
      .filter((group) => group.operations.length > 0);
  }, [doc, search]);

  if (error) return <Alert tone="error" message={error} />;
  if (!doc) return <Spinner label="Loading the API description" />;

  const base = `${window.location.origin}${doc.servers?.[0]?.url ?? "/api/v1"}`;

  return (
    <div className="api-explorer">
      <section className="api-explorer__intro">
        <p>
          Every table has its own endpoints; this page is generated from your current schema. Scripts authenticate with an{" "}
          <strong>API key</strong> in the <code>X-API-Key</code> header.
        </p>
        <dl className="api-explorer__facts">
          <div>
            <dt>Base URL</dt>
            <dd>
              <code>{base}</code>
            </dd>
          </div>
          <div>
            <dt>OpenAPI</dt>
            <dd>
              <button
                type="button"
                className="api-explorer__link"
                onClick={() => {
                  const blob = new Blob([JSON.stringify(doc, null, 2)], { type: "application/json" });
                  const link = document.createElement("a");
                  link.href = URL.createObjectURL(blob);
                  link.download = "bento-openapi.json";
                  link.click();
                  URL.revokeObjectURL(link.href);
                }}
              >
                Download openapi.json
              </button>{" "}
              <span className="api-explorer__muted">(version {doc.info.version})</span>
            </dd>
          </div>
        </dl>
      </section>

      <div className="api-explorer__bar">
        <div className="api-explorer__auth" role="radiogroup" aria-label="Send requests as">
          <span className="api-explorer__muted">Try it as</span>
          <label>
            <input type="radio" name="auth" checked={mode === "session"} onChange={() => setMode("session")} />
            Me (signed in)
          </label>
          <label>
            <input type="radio" name="auth" checked={mode === "key"} onChange={() => setMode("key")} />
            API key
          </label>
          {mode === "key" ? (
            <input
              className="field__control field__control--compact api-explorer__key"
              type="password"
              autoComplete="off"
              aria-label="API key to send"
              placeholder="bento_…"
              value={apiKey}
              onChange={(event) => setApiKey(event.target.value)}
            />
          ) : null}
        </div>
        <input
          className="field__control field__control--compact api-explorer__search"
          type="search"
          aria-label="Search endpoints"
          placeholder="Search endpoints"
          value={search}
          onChange={(event) => setSearch(event.target.value)}
        />
      </div>

      {groups.map((group) => (
        <section key={group.name} className="api-group" aria-label={group.name}>
          <header className="api-group__header">
            <h3>{group.name}</h3>
            {group.description ? (
              <p>
                <Inline text={group.description} />
              </p>
            ) : null}
          </header>
          <ul className="api-group__list" role="list">
            {group.operations.map((entry) => (
              <li key={entry.id}>
                <OperationRow
                  doc={doc}
                  entry={entry}
                  base={base}
                  expanded={open === entry.id}
                  onToggle={() => setOpen((current) => (current === entry.id ? null : entry.id))}
                  apiKey={mode === "key" ? apiKey.trim() : null}
                />
              </li>
            ))}
          </ul>
        </section>
      ))}
      {groups.length === 0 ? <p className="api-explorer__muted">No endpoint matches “{search}”.</p> : null}
    </div>
  );
}

function OperationRow({
  doc,
  entry,
  base,
  expanded,
  onToggle,
  apiKey,
}: {
  doc: OpenApiDocument;
  entry: OperationEntry;
  base: string;
  expanded: boolean;
  onToggle: () => void;
  apiKey: string | null;
}) {
  const { method, path, operation } = entry;
  const scope = operation["x-bento-scope"] ?? "read";
  return (
    <article className={expanded ? "api-op api-op--open" : "api-op"}>
      <button type="button" className="api-op__summary" aria-expanded={expanded} onClick={onToggle}>
        <span className={`api-method api-method--${method}`}>{method.toUpperCase()}</span>
        <code className="api-op__path">{path}</code>
        <span className="api-op__title">{operation.summary}</span>
        <span className={`api-scope api-scope--${scope}`} title="Which API keys may call this">
          {SCOPE_LABELS[scope]}
        </span>
        <Icon name={expanded ? "chevron-up" : "chevron-down"} size={14} />
      </button>
      {expanded ? <OperationDetail doc={doc} entry={entry} base={base} apiKey={apiKey} /> : null}
    </article>
  );
}

function OperationDetail({ doc, entry, base, apiKey }: { doc: OpenApiDocument; entry: OperationEntry; base: string; apiKey: string | null }) {
  const { method, path, operation } = entry;
  const parameters = operation.parameters ?? [];
  const bodySchema = requestBodySchema(operation);
  const fields = bodyFields(doc, operation);
  const [values, setValues] = useState<Record<string, string>>(() =>
    Object.fromEntries(
      parameters.map((p) => [p.name, p.in === "path" ? String(p.schema?.example ?? (p.schema?.enum?.[0] as string | undefined) ?? "") : ""]),
    ),
  );
  const [body, setBody] = useState(() => (bodySchema ? JSON.stringify(exampleFor(doc, bodySchema), null, 2) : ""));
  const [response, setResponse] = useState<TryItResponse | null>(null);
  const [sending, setSending] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);

  const url = buildUrl(base, path, parameters, values);
  const missing = parameters.filter((p) => p.in === "path" && !values[p.name]?.trim());

  const send = async () => {
    setProblem(null);
    let payload: string | null = null;
    if (bodySchema) {
      try {
        payload = JSON.stringify(JSON.parse(body || "{}"));
      } catch {
        setProblem("The body is not valid JSON.");
        return;
      }
    }
    if (apiKey === "") {
      setProblem("Enter an API key, or try it as yourself.");
      return;
    }
    setSending(true);
    try {
      setResponse(await sendRaw(method.toUpperCase(), url, payload, apiKey));
    } catch {
      setProblem("The request did not reach Bento.");
    } finally {
      setSending(false);
    }
  };

  const curl = curlFor(method, url, bodySchema ? (() => {
    try {
      return JSON.stringify(JSON.parse(body || "{}"));
    } catch {
      return body;
    }
  })() : null);

  return (
    <div className="api-op__detail">
      {operation.description ? (
        <p className="api-op__description">
          <Inline text={operation.description} />
        </p>
      ) : null}

      {parameters.length > 0 ? (
        <div className="api-op__block">
          <h4>Parameters</h4>
          <div className="api-params">
            {parameters.map((parameter) => (
              <label key={parameter.name} className="api-param">
                <span className="api-param__name">
                  {parameter.name}
                  {parameter.in === "path" || parameter.required ? <span className="api-param__required">required</span> : null}
                  <span className="api-param__in">{parameter.in}</span>
                </span>
                {parameter.schema?.enum ? (
                  <select
                    className="field__control field__control--compact"
                    value={values[parameter.name] ?? ""}
                    onChange={(event) => setValues({ ...values, [parameter.name]: event.target.value })}
                  >
                    {parameter.in !== "path" ? <option value="">—</option> : null}
                    {parameter.schema.enum.map((v) => (
                      <option key={String(v)} value={String(v)}>
                        {String(v)}
                      </option>
                    ))}
                  </select>
                ) : (
                  <input
                    className="field__control field__control--compact"
                    value={values[parameter.name] ?? ""}
                    placeholder={parameter.style === "deepObject" ? "column=value&column[lt]=0" : String(parameter.schema?.example ?? "")}
                    onChange={(event) => setValues({ ...values, [parameter.name]: event.target.value })}
                  />
                )}
                {parameter.description ? (
                  <span className="api-param__hint">
                    <Inline text={parameter.description} />
                  </span>
                ) : null}
              </label>
            ))}
          </div>
        </div>
      ) : null}

      {bodySchema ? (
        <div className="api-op__block">
          <h4>Body</h4>
          {fields.length > 0 ? (
            <table className="api-fields">
              <tbody>
                {fields.map((field) => (
                  <tr key={field.name}>
                    <td>
                      <code>{field.name}</code>
                      {field.required ? <span className="api-param__required">required</span> : null}
                    </td>
                    <td className="api-fields__type">{field.type}</td>
                    <td className="api-fields__description">{field.description}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : null}
          <textarea
            className="field__control api-op__body"
            aria-label="Request body"
            spellCheck={false}
            value={body}
            onChange={(event) => setBody(event.target.value)}
            rows={Math.min(14, Math.max(4, body.split("\n").length))}
          />
        </div>
      ) : null}

      <div className="api-op__actions">
        <Button type="button" variant="primary" onClick={() => void send()} disabled={sending || missing.length > 0}>
          {sending ? "Sending…" : "Send"}
        </Button>
        <code className="api-op__url">
          {method.toUpperCase()} {url}
        </code>
      </div>
      {problem ? <p className="api-op__problem">{problem}</p> : null}

      {response ? (
        <div className="api-op__block" aria-live="polite">
          <h4>
            Response{" "}
            <span className={`api-status api-status--${Math.floor(response.status / 100)}xx`}>
              {response.status} {response.statusText}
            </span>
            <span className="api-explorer__muted"> · {response.durationMs} ms</span>
          </h4>
          <pre className="api-op__response">{response.body ? prettyJson(response.body) : "(no content)"}</pre>
        </div>
      ) : null}

      <div className="api-op__block">
        <h4>
          curl
          <Button type="button" variant="ghost" size="sm" onClick={() => void copy(curl)} aria-label="Copy curl command">
            <Icon name="copy" size={13} />
          </Button>
        </h4>
        <pre className="api-op__curl">{curl}</pre>
      </div>
    </div>
  );
}
