import { useEffect, useMemo, useRef, useState } from "react";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { EmptyState } from "../../components/ui/EmptyState";
import { Panel } from "../../components/ui/Panel";
import { Spinner } from "../../components/ui/Spinner";
import { ConfirmDialog } from "../../components/ui/Dialog";
import { getSchemaSnapshot, previewSchemaDraft } from "../../api/schema";
import { ApiClientError } from "../../api/client";
import { useAuthStore } from "../../stores/authStore";
import type { SchemaIssue, SchemaModelDocument, SchemaPreviewResponse } from "../../types/api";
import {
  JsonCodeEditor,
  type JsonEditorDiagnostic,
} from "./JsonCodeEditor";
import { parseJsonDraft, type JsonDraftLocation } from "./draftJson";
import { resolveJsonPathLocation } from "./jsonPathLocation";
import "./SchemaWorkbenchPage.css";

type WorkbenchStatus = "loading" | "ready" | "error";

type LoadState = {
  status: WorkbenchStatus;
  error: string | null;
  version: number | null;
  loadedText: string;
  draftText: string;
  loadedAt: number | null;
  isEmpty: boolean;
};

type ImportFeedback = {
  detail?: string;
  message: string;
  title: string;
};

type PendingImport = {
  fileName: string;
  text: string;
};

type PreviewStatus = "idle" | "loading" | "ready" | "error";

type PreviewState = {
  status: PreviewStatus;
  response: SchemaPreviewResponse | null;
  error: string | null;
};

type WorkbenchDiagnostic = {
  code: string;
  detail?: string;
  id: string;
  location: JsonDraftLocation | null;
  locationExact: boolean;
  message: string;
  path: string | null;
  severity: "error" | "warning";
  source: "backend" | "syntax";
};

function readBlobText(file: Blob): Promise<string> {
  if (typeof file.text === "function") {
    return file.text();
  }

  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => {
      reject(reader.error ?? new Error("Unable to read the selected file."));
    };
    reader.onload = () => {
      resolve(typeof reader.result === "string" ? reader.result : "");
    };
    reader.readAsText(file);
  });
}

const initialState: LoadState = {
  status: "loading",
  error: null,
  version: null,
  loadedText: "",
  draftText: "",
  loadedAt: null,
  isEmpty: false,
};

const initialPreviewState: PreviewState = {
  status: "idle",
  response: null,
  error: null,
};

function isEmptySchema(document: SchemaModelDocument): boolean {
  return document.tables.length === 0 && document.relations.length === 0;
}

function buildBackendDiagnostic(
  issue: SchemaIssue,
  severity: "error" | "warning",
  index: number,
  draftText: string,
): WorkbenchDiagnostic {
  const resolution = resolveJsonPathLocation(draftText, issue.path);

  return {
    code: issue.code,
    id: `backend:${severity}:${index}:${issue.path}`,
    location: resolution?.location ?? null,
    locationExact: resolution?.exact ?? false,
    message: issue.message,
    path: issue.path,
    severity,
    source: "backend",
  };
}

function countLabel(count: number, singular: string, plural = `${singular}s`): string {
  return `${count} ${count === 1 ? singular : plural}`;
}

export function SchemaWorkbenchPage() {
  const user = useAuthStore((state) => state.user);
  const isAdmin = user?.role === "admin";
  const [state, setState] = useState<LoadState>(initialState);
  const [reloadKey, setReloadKey] = useState(0);
  const [importFeedback, setImportFeedback] = useState<ImportFeedback | null>(null);
  const [pendingImport, setPendingImport] = useState<PendingImport | null>(null);
  const [previewState, setPreviewState] = useState<PreviewState>(initialPreviewState);
  const [activeDiagnosticId, setActiveDiagnosticId] = useState<string | null>(null);
  const [editorFocusLocation, setEditorFocusLocation] = useState<JsonDraftLocation | null>(null);
  const importInputRef = useRef<HTMLInputElement | null>(null);
  const previewRequestIdRef = useRef(0);

  function resetPreviewState() {
    previewRequestIdRef.current += 1;
    setPreviewState(initialPreviewState);
    setActiveDiagnosticId(null);
    setEditorFocusLocation(null);
  }

  function replaceDraftText(draftText: string) {
    setState((current) => ({
      ...current,
      draftText,
    }));
    setImportFeedback(null);
    resetPreviewState();
  }

  useEffect(() => {
    if (!isAdmin) {
      return;
    }

    let cancelled = false;

    async function loadCurrentSchema() {
      setState((current) => ({
        ...current,
        status: "loading",
        error: null,
      }));
      setImportFeedback(null);
      setPendingImport(null);
      resetPreviewState();

      try {
        const snapshot = await getSchemaSnapshot();
        const text = JSON.stringify(snapshot, null, 2);

        if (cancelled) {
          return;
        }

        setState({
          status: "ready",
          error: null,
          version: snapshot.version,
          loadedText: text,
          draftText: text,
          loadedAt: Date.now(),
          isEmpty: isEmptySchema(snapshot),
        });
      } catch (error) {
        if (cancelled) {
          return;
        }

        const message =
          error instanceof ApiClientError
            ? error.message
            : "Unable to load the current Model JSON.";

        setState((current) => ({
          ...current,
          status: "error",
          error: message,
        }));
      }
    }

    void loadCurrentSchema();

    return () => {
      cancelled = true;
    };
  }, [isAdmin, reloadKey]);

  const draftParse =
    state.status === "ready" ? parseJsonDraft(state.draftText) : null;
  const dirty = state.draftText !== state.loadedText;
  const syntaxDiagnostics = useMemo<WorkbenchDiagnostic[]>(() => {
    if (draftParse?.status !== "invalid") {
      return [];
    }

    return [
      {
        code: "json_parse_error",
        detail: draftParse.detail,
        id: "syntax:json_parse_error",
        location: draftParse.location,
        locationExact: true,
        message: draftParse.message,
        path: null,
        severity: "error",
        source: "syntax",
      },
    ];
  }, [draftParse]);
  const backendErrorDiagnostics = useMemo<WorkbenchDiagnostic[]>(() => {
    if (draftParse?.status !== "valid" || previewState.status !== "ready" || !previewState.response) {
      return [];
    }

    return previewState.response.errors.map((issue, index) =>
      buildBackendDiagnostic(issue, "error", index, state.draftText),
    );
  }, [draftParse, previewState, state.draftText]);
  const backendWarningDiagnostics = useMemo<WorkbenchDiagnostic[]>(() => {
    if (draftParse?.status !== "valid" || previewState.status !== "ready" || !previewState.response) {
      return [];
    }

    return previewState.response.warnings.map((issue, index) =>
      buildBackendDiagnostic(issue, "warning", index, state.draftText),
    );
  }, [draftParse, previewState, state.draftText]);
  const editorDiagnostics = useMemo<JsonEditorDiagnostic[]>(() => (
    [...syntaxDiagnostics, ...backendErrorDiagnostics, ...backendWarningDiagnostics]
      .filter((diagnostic) => diagnostic.location)
      .map((diagnostic) => ({
        line: diagnostic.location?.line ?? 1,
        severity: diagnostic.severity,
      }))
  ), [syntaxDiagnostics, backendErrorDiagnostics, backendWarningDiagnostics]);
  const activeLine = useMemo(() => {
    const activeDiagnostic = [...syntaxDiagnostics, ...backendErrorDiagnostics, ...backendWarningDiagnostics]
      .find((diagnostic) => diagnostic.id === activeDiagnosticId);

    if (activeDiagnostic?.location) {
      return activeDiagnostic.location.line;
    }

    return syntaxDiagnostics[0]?.location?.line ?? null;
  }, [activeDiagnosticId, syntaxDiagnostics, backendErrorDiagnostics, backendWarningDiagnostics]);

  function applyImportedDraft(text: string) {
    replaceDraftText(text);
  }

  async function handleImportFile(file: File) {
    const fileText = await readBlobText(file);
    const importedDraft = parseJsonDraft(fileText);

    if (importedDraft.status !== "valid") {
      setImportFeedback({
        title: "Import failed",
        message: `“${file.name}” is not valid JSON. The current draft was left unchanged.`,
        detail: importedDraft.detail,
      });
      return;
    }

    const formattedText = importedDraft.formattedText;
    if (formattedText === state.draftText) {
      setImportFeedback(null);
      return;
    }

    if (dirty) {
      setPendingImport({
        fileName: file.name,
        text: formattedText,
      });
      setImportFeedback(null);
      return;
    }

    applyImportedDraft(formattedText);
  }

  function handleExportDraft() {
    if (draftParse?.status !== "valid") {
      return;
    }

    const blob = new Blob([draftParse.formattedText], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = "model-draft.json";
    anchor.style.display = "none";
    document.body.append(anchor);
    anchor.click();
    anchor.remove();
    window.setTimeout(() => {
      URL.revokeObjectURL(url);
    }, 0);
  }

  async function handlePreview() {
    if (draftParse?.status !== "valid") {
      return;
    }

    const requestId = previewRequestIdRef.current + 1;
    previewRequestIdRef.current = requestId;
    setPreviewState({
      status: "loading",
      response: null,
      error: null,
    });
    setActiveDiagnosticId(null);

    try {
      const response = await previewSchemaDraft(draftParse.value as SchemaModelDocument);
      if (previewRequestIdRef.current !== requestId) {
        return;
      }

      setPreviewState({
        status: "ready",
        response,
        error: null,
      });
    } catch (error) {
      if (previewRequestIdRef.current !== requestId) {
        return;
      }

      setPreviewState({
        status: "error",
        response: null,
        error:
          error instanceof ApiClientError
            ? error.message
            : "Unable to preview the schema draft right now.",
      });
    }
  }

  function focusDiagnostic(diagnostic: WorkbenchDiagnostic) {
    setActiveDiagnosticId(diagnostic.id);

    if (!diagnostic.location) {
      return;
    }

    setEditorFocusLocation({ ...diagnostic.location });
  }

  if (!isAdmin) {
    return (
      <div className="schema-workbench schema-workbench--state">
        <Panel>
          <EmptyState
            title="Admin access required"
            description="The schema workbench loads the admin Model JSON surface. Sign in with an admin account to inspect or stage schema drafts."
            icon="schema"
            bullets={[
              "Loads the current server schema into a local draft",
              "Keeps the shared schema store on the last applied snapshot",
              "Preview and apply actions stay gated to administrators",
            ]}
          />
        </Panel>
      </div>
    );
  }

  if (state.status === "loading") {
    return (
      <div className="schema-workbench schema-workbench--state">
        <Panel className="schema-workbench__state-panel">
          <div className="schema-workbench__loading" role="status" aria-live="polite">
            <Spinner label="Loading current schema" />
            <p className="schema-workbench__state-copy">
              Fetching the current Model JSON from the admin schema API.
            </p>
          </div>
        </Panel>
      </div>
    );
  }

  if (state.status === "error") {
    return (
      <div className="schema-workbench schema-workbench--state">
        <Panel className="schema-workbench__state-panel">
          <Alert
            tone="error"
            title="Unable to load the schema workbench"
            message={state.error ?? "Unable to load the current Model JSON."}
          />
          <div className="schema-workbench__state-actions">
            <Button
              variant="primary"
              leadingIcon={<Icon name="refresh" size={14} />}
              onClick={() => {
                setReloadKey((current) => current + 1);
              }}
            >
              Retry
            </Button>
          </div>
        </Panel>
      </div>
    );
  }

  return (
    <div className="schema-workbench">
      <div
        className="schema-workbench__toolbar"
        role="toolbar"
        aria-label="Schema workbench actions"
      >
        <Button size="sm" onClick={() => setReloadKey((current) => current + 1)}>
          Refresh
        </Button>
        <Button
          size="sm"
          disabled={draftParse?.status !== "valid"}
          onClick={() => {
            if (draftParse?.status !== "valid") {
              return;
            }

            replaceDraftText(draftParse.formattedText);
          }}
        >
          Format JSON
        </Button>
        <Button
          size="sm"
          disabled={draftParse?.status !== "valid" || previewState.status === "loading"}
          onClick={() => {
            void handlePreview();
          }}
        >
          Preview
        </Button>
        <Button size="sm" disabled>
          Apply
        </Button>
        <Button
          size="sm"
          onClick={() => {
            importInputRef.current?.click();
          }}
        >
          Import
        </Button>
        <Button
          size="sm"
          disabled={draftParse?.status !== "valid"}
          onClick={handleExportDraft}
        >
          Export
        </Button>
      </div>

      <input
        ref={importInputRef}
        type="file"
        accept="application/json,.json"
        className="schema-workbench__import-input"
        data-testid="model-json-import-input"
        onChange={(event) => {
          const [file] = Array.from(event.target.files ?? []);
          event.target.value = "";

          if (!file) {
            return;
          }

          void handleImportFile(file);
        }}
      />

      {state.isEmpty ? (
        <Alert
          tone="info"
          title="New workspace schema"
          message="No tables or relations are currently applied. This shell still seeds a local draft so the next tasks can build editor, preview, and import flows on top of a stable empty-schema state."
        />
      ) : null}

      {importFeedback ? (
        <div className="schema-workbench__import-feedback">
          <Alert
            tone="error"
            title={importFeedback.title}
            message={importFeedback.message}
          />
          {importFeedback.detail ? (
            <div className="schema-workbench__diagnostic-detail mono">
              {importFeedback.detail}
            </div>
          ) : null}
        </div>
      ) : null}

      <div className="schema-workbench__workspace">
        <Panel
          className="schema-workbench__editor-panel"
          bodyClassName="schema-workbench__editor-body"
        >
          <div className="schema-workbench__editor-shell">
            <JsonCodeEditor
              activeLine={activeLine}
              diagnostics={editorDiagnostics}
              focusLocation={editorFocusLocation}
              value={state.draftText}
              onChange={(draftText) =>
                {
                  replaceDraftText(draftText);
                }
              }
            />
          </div>
        </Panel>

        <div className="schema-workbench__side">
          <Panel
            title="Diagnostics"
            className="schema-workbench__side-panel"
          >
            {draftParse?.status === "invalid" ? (
              <div className="schema-workbench__diagnostics">
                <Alert
                  tone="error"
                  title="Syntax error"
                  message="The draft is not valid JSON yet, so backend schema validation is paused until the syntax is fixed."
                />
                <DiagnosticsSection
                  title="Local JSON syntax"
                  diagnostics={syntaxDiagnostics}
                  activeDiagnosticId={activeDiagnosticId}
                  onSelect={focusDiagnostic}
                />
              </div>
            ) : (
              <div className="schema-workbench__diagnostics">
                <Alert
                  tone="success"
                  title="JSON syntax valid"
                  message="Run Preview to validate schema rules, warnings, and issue paths against the backend."
                />

                {previewState.status === "idle" ? (
                  <Alert
                    title="Backend validation idle"
                    message="No backend diagnostics yet. Preview the current draft to load schema errors and warnings with editor path mapping."
                  />
                ) : null}

                {previewState.status === "loading" ? (
                  <div className="schema-workbench__preview-loading" role="status" aria-live="polite">
                    <Spinner label="Previewing schema draft" />
                    <p className="schema-workbench__preview-copy">
                      Validating the draft against the backend schema engine and mapping issue paths back into the editor.
                    </p>
                  </div>
                ) : null}

                {previewState.status === "error" ? (
                  <Alert
                    tone="error"
                    title="Preview request failed"
                    message={previewState.error ?? "Unable to preview the schema draft right now."}
                  />
                ) : null}

                {previewState.status === "ready" ? (
                  <>
                    {backendErrorDiagnostics.length === 0 && backendWarningDiagnostics.length === 0 ? (
                      <Alert
                        tone="success"
                        title="No backend diagnostics"
                        message="The backend accepted the draft without validation errors or warnings."
                      />
                    ) : null}

                    <DiagnosticsSection
                      title="Schema errors"
                      diagnostics={backendErrorDiagnostics}
                      activeDiagnosticId={activeDiagnosticId}
                      onSelect={focusDiagnostic}
                    />

                    <DiagnosticsSection
                      title="Warnings"
                      diagnostics={backendWarningDiagnostics}
                      activeDiagnosticId={activeDiagnosticId}
                      onSelect={focusDiagnostic}
                    />
                  </>
                ) : null}
              </div>
            )}
          </Panel>

          <Panel
            title="Preview"
            className="schema-workbench__side-panel"
          >
            {draftParse?.status !== "valid" ? (
              <EmptyState
                title="Fix JSON syntax before previewing"
                description="The workbench keeps your raw text intact while the draft is invalid. Preview and apply stay unavailable until the JSON parses again."
                icon="table"
                bullets={[
                  "Local parse errors do not wipe your draft",
                  "Format, preview, and apply reactivate once the JSON parses again",
                ]}
              />
            ) : previewState.status === "idle" ? (
              <EmptyState
                title="Run Preview to validate this draft"
                description="The next task will expand this panel into the grouped change summary, but preview already validates the schema and records warning/error counts."
                icon="table"
                bullets={[
                  "Preview is non-mutating and safe to rerun",
                  "Diagnostics already map backend issue paths back into the editor",
                ]}
              />
            ) : previewState.status === "loading" ? (
              <div className="schema-workbench__preview-loading" role="status" aria-live="polite">
                <Spinner label="Preparing preview result" />
                <p className="schema-workbench__preview-copy">
                  Building the backend validation result before the richer change summary lands in the next task.
                </p>
              </div>
            ) : previewState.status === "error" ? (
              <Alert
                tone="error"
                title="Preview unavailable"
                message={previewState.error ?? "Unable to preview the schema draft right now."}
              />
            ) : previewState.response ? (
              <div className="schema-workbench__preview-summary">
                <Alert
                  tone={previewState.response.isValid ? "success" : "error"}
                  title={previewState.response.isValid ? "Preview validation complete" : "Preview found blocking issues"}
                  message={
                    previewState.response.isValid
                      ? "Backend validation completed successfully. TASK-118 will turn these results into the grouped migration summary."
                      : "Fix the schema errors in Diagnostics before the workbench can move on to richer preview and apply flows."
                  }
                />
                <div className="schema-workbench__preview-stat-grid">
                  <div className="schema-workbench__preview-stat">
                    <span className="schema-workbench__preview-stat-label">Errors</span>
                    <strong>{backendErrorDiagnostics.length}</strong>
                  </div>
                  <div className="schema-workbench__preview-stat">
                    <span className="schema-workbench__preview-stat-label">Warnings</span>
                    <strong>{backendWarningDiagnostics.length}</strong>
                  </div>
                  <div className="schema-workbench__preview-stat">
                    <span className="schema-workbench__preview-stat-label">Changes</span>
                    <strong>{previewState.response.changes.length}</strong>
                  </div>
                </div>
              </div>
            ) : null}
          </Panel>
        </div>
      </div>

      <ConfirmDialog
        open={pendingImport !== null}
        title="Replace the current draft?"
        description={
          pendingImport
            ? `Importing “${pendingImport.fileName}” will replace your unsaved local edits in the Model JSON draft.`
            : ""
        }
        confirmLabel="Replace draft"
        onConfirm={() => {
          if (!pendingImport) {
            return;
          }

          applyImportedDraft(pendingImport.text);
          setPendingImport(null);
        }}
        onCancel={() => setPendingImport(null)}
      />
    </div>
  );
}

type DiagnosticsSectionProps = {
  activeDiagnosticId: string | null;
  diagnostics: WorkbenchDiagnostic[];
  onSelect: (diagnostic: WorkbenchDiagnostic) => void;
  title: string;
};

function DiagnosticsSection({
  activeDiagnosticId,
  diagnostics,
  onSelect,
  title,
}: DiagnosticsSectionProps) {
  if (diagnostics.length === 0) {
    return null;
  }

  return (
    <section className="schema-workbench__diagnostic-section" aria-label={title}>
      <div className="schema-workbench__diagnostic-heading">
        <h3>{title}</h3>
        <span className="schema-workbench__diagnostic-count">
          {countLabel(diagnostics.length, "issue")}
        </span>
      </div>

      <div className="schema-workbench__diagnostic-list">
        {diagnostics.map((diagnostic) => {
          const locationLabel = diagnostic.location
            ? `${diagnostic.locationExact ? "Line" : "Near line"} ${diagnostic.location.line}`
            : null;

          return (
            <button
              key={diagnostic.id}
              type="button"
              className={[
                "schema-workbench__diagnostic-card",
                diagnostic.severity === "warning"
                  ? "schema-workbench__diagnostic-card--warning"
                  : "schema-workbench__diagnostic-card--error",
                activeDiagnosticId === diagnostic.id
                  ? "schema-workbench__diagnostic-card--active"
                  : "",
              ].filter(Boolean).join(" ")}
              onMouseDown={(event) => {
                event.preventDefault();
                onSelect(diagnostic);
              }}
              onClick={(event) => {
                if (event.detail === 0) {
                  onSelect(diagnostic);
                }
              }}
            >
              <div className="schema-workbench__diagnostic-meta">
                <span className="schema-workbench__diagnostic-severity">
                  {diagnostic.source === "syntax" ? "Syntax" : diagnostic.severity === "error" ? "Error" : "Warning"}
                </span>
                <code className="schema-workbench__diagnostic-code">{diagnostic.code}</code>
                {diagnostic.path ? (
                  <span className="schema-workbench__diagnostic-path mono">{diagnostic.path}</span>
                ) : null}
                {locationLabel ? (
                  <span className="schema-workbench__diagnostic-location">{locationLabel}</span>
                ) : null}
              </div>
              <p className="schema-workbench__diagnostic-message">{diagnostic.message}</p>
              {diagnostic.detail ? (
                <div className="schema-workbench__diagnostic-detail mono">
                  {diagnostic.detail}
                </div>
              ) : null}
            </button>
          );
        })}
      </div>
    </section>
  );
}
