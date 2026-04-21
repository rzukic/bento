import { useEffect, useMemo, useRef, useState } from "react";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { EmptyState } from "../../components/ui/EmptyState";
import { Panel } from "../../components/ui/Panel";
import { Spinner } from "../../components/ui/Spinner";
import { ConfirmDialog, Dialog } from "../../components/ui/Dialog";
import {
  applySchemaDraft,
  getSchemaConfirmationRequiredDetails,
  getSchemaSnapshot,
  getSchemaValidationFailedDetails,
  previewSchemaDraft,
} from "../../api/schema";
import { ApiClientError } from "../../api/client";
import { useAuthStore } from "../../stores/authStore";
import { useSchemaStore } from "../../stores/schemaStore";
import type {
  SchemaCastExample,
  SchemaChange,
  SchemaIssue,
  SchemaModelDocument,
  SchemaPreviewResponse,
} from "../../types/api";
import { JsonCodeEditor, type JsonEditorDiagnostic } from "./JsonCodeEditor";
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

type PreviewChangeGroup = {
  id: "tables" | "columns" | "relations" | "indexes";
  title: string;
  changes: SchemaChange[];
};

type ApplyFeedback = {
  tone: "success" | "warning" | "error";
  title: string;
  message: string;
};

type ApplyDialogState = {
  open: boolean;
  canSubmit: boolean;
  confirmationRequired: boolean;
  error: string | null;
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

const initialApplyDialogState: ApplyDialogState = {
  open: false,
  canSubmit: false,
  confirmationRequired: false,
  error: null,
};

function isEmptySchema(document: SchemaModelDocument): boolean {
  return document.tables.length === 0 && document.relations.length === 0;
}

function formatSchemaText(document: SchemaModelDocument): string {
  return JSON.stringify(document, null, 2);
}

function buildLoadedState(document: SchemaModelDocument): LoadState {
  const text = formatSchemaText(document);

  return {
    status: "ready",
    error: null,
    version: document.version,
    loadedText: text,
    draftText: text,
    loadedAt: Date.now(),
    isEmpty: isEmptySchema(document),
  };
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

function countLabel(
  count: number,
  singular: string,
  plural = `${singular}s`,
): string {
  return `${count} ${count === 1 ? singular : plural}`;
}

function buildPreviewGroups(changes: SchemaChange[]): PreviewChangeGroup[] {
  const groups: PreviewChangeGroup[] = [
    {
      id: "tables",
      title: "Tables",
      changes: changes.filter(
        (change) =>
          change.changeType === "AddTable" ||
          change.changeType === "DropTable" ||
          change.changeType === "RenameTable",
      ),
    },
    {
      id: "columns",
      title: "Columns",
      changes: changes.filter(
        (change) =>
          change.changeType === "AddColumn" ||
          change.changeType === "DropColumn" ||
          change.changeType === "RenameColumn" ||
          change.changeType === "AlterColumnType" ||
          change.changeType === "AlterColumnNullability" ||
          change.changeType === "AlterColumnDefault" ||
          change.changeType === "AlterColumnUnique",
      ),
    },
    {
      id: "relations",
      title: "Relations",
      changes: changes.filter(
        (change) =>
          change.changeType === "AddRelation" ||
          change.changeType === "DropRelation" ||
          change.changeType === "AlterRelation",
      ),
    },
    {
      id: "indexes",
      title: "Indexes",
      changes: changes.filter(
        (change) =>
          change.changeType === "AddIndex" ||
          change.changeType === "DropIndex" ||
          change.changeType === "RenameIndex" ||
          change.changeType === "AlterIndex",
      ),
    },
  ];

  return groups.filter((group) => group.changes.length > 0);
}

function getQualifiedColumnName(change: SchemaChange): string {
  return change.column ? `${change.table}.${change.column}` : change.table;
}

function formatPreviewValue(value: unknown): string {
  if (value === undefined) {
    return "undefined";
  }

  try {
    const serialized = JSON.stringify(value);
    return serialized ?? String(value);
  } catch {
    return String(value);
  }
}

function describePreviewChange(change: SchemaChange): {
  detail?: string;
  title: string;
} {
  switch (change.changeType) {
    case "AddTable":
      return { title: `Add table ${change.table}` };
    case "DropTable":
      return { title: `Drop table ${change.table}` };
    case "RenameTable":
      return {
        title: `Rename table ${change.previousTable ?? change.table} to ${change.table}`,
      };
    case "AddColumn":
      return { title: `Add column ${getQualifiedColumnName(change)}` };
    case "DropColumn":
      return { title: `Drop column ${getQualifiedColumnName(change)}` };
    case "RenameColumn":
      return {
        title: `Rename column ${change.table}.${change.previousColumn ?? change.column} to ${change.column ?? change.previousColumn}`,
      };
    case "AlterColumnType":
      return {
        title: `Change type of ${getQualifiedColumnName(change)} from ${change.oldValue} to ${change.newValue}`,
        detail:
          change.coercionKind === "Lossy"
            ? "Lossy conversion. Existing values may be truncated or rejected depending on provider behavior."
            : change.coercionKind
              ? `${change.coercionKind} conversion`
              : undefined,
      };
    case "AlterColumnNullability":
      return {
        title:
          change.newValue === false
            ? `Require values in ${getQualifiedColumnName(change)}`
            : `Allow null values in ${getQualifiedColumnName(change)}`,
      };
    case "AlterColumnDefault":
      return { title: `Change default for ${getQualifiedColumnName(change)}` };
    case "AlterColumnUnique":
      return {
        title:
          change.newValue === true
            ? `Make ${getQualifiedColumnName(change)} unique`
            : `Remove uniqueness from ${getQualifiedColumnName(change)}`,
      };
    case "AddRelation":
      return { title: `Add relation ${change.relation ?? change.table}` };
    case "DropRelation":
      return { title: `Drop relation ${change.relation ?? change.table}` };
    case "AlterRelation":
      return { title: `Change relation ${change.relation ?? change.table}` };
    case "AddIndex":
      return { title: `Add index ${change.index ?? change.table}` };
    case "DropIndex":
      return { title: `Drop index ${change.index ?? change.table}` };
    case "RenameIndex":
      return {
        title: `Rename index ${String(change.oldValue ?? change.index)} to ${String(change.newValue ?? change.index)}`,
      };
    case "AlterIndex":
      return { title: `Change index ${change.index ?? change.table}` };
    default:
      return { title: change.changeType };
  }
}

function mergePreviewValidationErrors(
  response: SchemaPreviewResponse,
  errors: SchemaIssue[],
): SchemaPreviewResponse {
  return {
    ...response,
    isValid: false,
    errors,
  };
}

export function SchemaWorkbenchPage() {
  const user = useAuthStore((state) => state.user);
  const isAdmin = user?.role === "admin";
  const [state, setState] = useState<LoadState>(initialState);
  const [reloadKey, setReloadKey] = useState(0);
  const [importFeedback, setImportFeedback] = useState<ImportFeedback | null>(
    null,
  );
  const [pendingImport, setPendingImport] = useState<PendingImport | null>(
    null,
  );
  const [previewState, setPreviewState] =
    useState<PreviewState>(initialPreviewState);
  const [activeDiagnosticId, setActiveDiagnosticId] = useState<string | null>(
    null,
  );
  const [editorFocusLocation, setEditorFocusLocation] =
    useState<JsonDraftLocation | null>(null);
  const [applyFeedback, setApplyFeedback] = useState<ApplyFeedback | null>(
    null,
  );
  const [applyDialog, setApplyDialog] = useState<ApplyDialogState>(
    initialApplyDialogState,
  );
  const [applyInFlight, setApplyInFlight] = useState(false);
  const importInputRef = useRef<HTMLInputElement | null>(null);
  const applyReviewRef = useRef<HTMLDivElement | null>(null);
  const previewRequestIdRef = useRef(0);

  function resetApplyDialog() {
    setApplyDialog(initialApplyDialogState);
    setApplyInFlight(false);
  }

  function resetPreviewState() {
    previewRequestIdRef.current += 1;
    setPreviewState(initialPreviewState);
    setActiveDiagnosticId(null);
    setEditorFocusLocation(null);
    resetApplyDialog();
  }

  function replaceDraftText(draftText: string) {
    setState((current) => ({
      ...current,
      draftText,
    }));
    setImportFeedback(null);
    setApplyFeedback(null);
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
      setApplyFeedback(null);
      setImportFeedback(null);
      setPendingImport(null);
      resetPreviewState();

      try {
        const snapshot = await getSchemaSnapshot();

        if (cancelled) {
          return;
        }

        setState(buildLoadedState(snapshot));
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

  useEffect(() => {
    if (!applyDialog.open) {
      return;
    }

    const reviewContainer = applyReviewRef.current;
    if (!reviewContainer) {
      return;
    }

    function updateCanSubmit() {
      const maxScrollTop =
        reviewContainer!.scrollHeight - reviewContainer!.clientHeight;
      const canSubmit =
        maxScrollTop <= 8 ||
        reviewContainer!.scrollTop >= maxScrollTop - 8;

      setApplyDialog((current) =>
        current.canSubmit === canSubmit
          ? current
          : {
              ...current,
              canSubmit,
            },
      );
    }

    const animationFrameId = window.requestAnimationFrame(updateCanSubmit);
    reviewContainer.addEventListener("scroll", updateCanSubmit, {
      passive: true,
    });
    window.addEventListener("resize", updateCanSubmit);

    return () => {
      window.cancelAnimationFrame(animationFrameId);
      reviewContainer.removeEventListener("scroll", updateCanSubmit);
      window.removeEventListener("resize", updateCanSubmit);
    };
  }, [applyDialog.open, previewState.response]);

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
    if (
      draftParse?.status !== "valid" ||
      previewState.status !== "ready" ||
      !previewState.response
    ) {
      return [];
    }

    return previewState.response.errors.map((issue, index) =>
      buildBackendDiagnostic(issue, "error", index, state.draftText),
    );
  }, [draftParse, previewState, state.draftText]);
  const backendWarningDiagnostics = useMemo<WorkbenchDiagnostic[]>(() => {
    if (
      draftParse?.status !== "valid" ||
      previewState.status !== "ready" ||
      !previewState.response
    ) {
      return [];
    }

    return previewState.response.warnings.map((issue, index) =>
      buildBackendDiagnostic(issue, "warning", index, state.draftText),
    );
  }, [draftParse, previewState, state.draftText]);
  const editorDiagnostics = useMemo<JsonEditorDiagnostic[]>(
    () =>
      [
        ...syntaxDiagnostics,
        ...backendErrorDiagnostics,
        ...backendWarningDiagnostics,
      ]
        .filter((diagnostic) => diagnostic.location)
        .map((diagnostic) => ({
          line: diagnostic.location?.line ?? 1,
          severity: diagnostic.severity,
        })),
    [syntaxDiagnostics, backendErrorDiagnostics, backendWarningDiagnostics],
  );
  const activeLine = useMemo(() => {
    const activeDiagnostic = [
      ...syntaxDiagnostics,
      ...backendErrorDiagnostics,
      ...backendWarningDiagnostics,
    ].find((diagnostic) => diagnostic.id === activeDiagnosticId);

    if (activeDiagnostic?.location) {
      return activeDiagnostic.location.line;
    }

    return syntaxDiagnostics[0]?.location?.line ?? null;
  }, [
    activeDiagnosticId,
    syntaxDiagnostics,
    backendErrorDiagnostics,
    backendWarningDiagnostics,
  ]);
  const previewGroups = useMemo(
    () => buildPreviewGroups(previewState.response?.changes ?? []),
    [previewState.response],
  );
  const destructiveChangeCount = useMemo(
    () =>
      previewState.response?.changes.filter((change) => change.isDestructive)
        .length ?? 0,
    [previewState.response],
  );
  const lossyChangeCount = useMemo(
    () =>
      previewState.response?.changes.filter(
        (change) => change.coercionKind === "Lossy",
      ).length ?? 0,
    [previewState.response],
  );
  const canOpenApplyDialog =
    draftParse?.status === "valid" &&
    previewState.status === "ready" &&
    previewState.response !== null &&
    previewState.response.isValid &&
    previewState.response.hasChanges &&
    backendErrorDiagnostics.length === 0 &&
    !applyInFlight;

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

    const blob = new Blob([draftParse.formattedText], {
      type: "application/json",
    });
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
      const response = await previewSchemaDraft(
        draftParse.value as SchemaModelDocument,
      );
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

  function openApplyDialog() {
    if (!canOpenApplyDialog) {
      return;
    }

    setApplyFeedback(null);
    setApplyDialog({
      open: true,
      canSubmit: false,
      confirmationRequired: false,
      error: null,
    });
  }

  async function refreshAppliedSchemaState(): Promise<SchemaModelDocument> {
    const snapshot = await getSchemaSnapshot();
    const loadedAt = Date.now();

    useSchemaStore.setState({
      status: "ready",
      snapshot,
      error: null,
      lastLoadedAt: loadedAt,
      inFlight: null,
    });

    setState({
      ...buildLoadedState(snapshot),
      loadedAt,
    });

    return snapshot;
  }

  async function handleApply() {
    if (draftParse?.status !== "valid" || !previewState.response) {
      return;
    }

    setApplyInFlight(true);
    setApplyFeedback(null);
    setApplyDialog((current) => ({
      ...current,
      error: null,
    }));

    try {
      const applyResult = await applySchemaDraft(
        draftParse.value as SchemaModelDocument,
        applyDialog.confirmationRequired
          ? { confirmDestructive: true }
          : undefined,
      );

      let appliedSnapshot: SchemaModelDocument;
      try {
        appliedSnapshot = await refreshAppliedSchemaState();
      } catch (error) {
        const message =
          error instanceof ApiClientError
            ? error.message
            : "The schema was applied, but the refreshed live snapshot could not be loaded.";

        setApplyFeedback({
          tone: "warning",
          title: `Schema applied at version ${applyResult.version}`,
          message,
        });
        resetPreviewState();
        setApplyDialog(initialApplyDialogState);
        return;
      }

      resetPreviewState();
      setApplyDialog(initialApplyDialogState);
      setApplyFeedback({
        tone: "success",
        title: `Applied schema version ${appliedSnapshot.version}`,
        message: `${countLabel(applyResult.changes.length, "change")} committed and the live schema snapshot was refreshed.`,
      });
    } catch (error) {
      const validationDetails = getSchemaValidationFailedDetails(error);
      if (validationDetails?.errors && validationDetails.errors.length > 0) {
        setPreviewState((current) => {
          if (current.status !== "ready" || !current.response) {
            return current;
          }

          return {
            status: "ready",
            response: mergePreviewValidationErrors(
              current.response,
              validationDetails.errors ?? [],
            ),
            error: null,
          };
        });
        setApplyDialog(initialApplyDialogState);
        setApplyFeedback({
          tone: "error",
          title: "Apply blocked by validation errors",
          message:
            "The draft was not applied. Review the diagnostics panel, fix the schema errors, and preview again.",
        });
        return;
      }

      const confirmationDetails = getSchemaConfirmationRequiredDetails(error);
      if (confirmationDetails) {
        setApplyDialog((current) => ({
          ...current,
          confirmationRequired: true,
          error:
            "This migration includes destructive changes. Review the preview and click Apply destructive changes to continue.",
        }));
        return;
      }

      setApplyDialog((current) => ({
        ...current,
        error:
          error instanceof ApiClientError
            ? error.message
            : "Unable to apply the schema draft right now.",
      }));
    } finally {
      setApplyInFlight(false);
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
          <div
            className="schema-workbench__loading"
            role="status"
            aria-live="polite"
          >
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
        <Button
          size="sm"
          onClick={() => setReloadKey((current) => current + 1)}
        >
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
          disabled={
            draftParse?.status !== "valid" || previewState.status === "loading"
          }
          onClick={() => {
            void handlePreview();
          }}
        >
          Preview
        </Button>
        <Button
          size="sm"
          variant="primary"
          disabled={!canOpenApplyDialog}
          onClick={openApplyDialog}
        >
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

      {applyFeedback ? (
        <Alert
          tone={applyFeedback.tone}
          title={applyFeedback.title}
          message={applyFeedback.message}
        />
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
              onChange={(draftText) => {
                replaceDraftText(draftText);
              }}
            />
          </div>
        </Panel>

        <div className="schema-workbench__side">
          <Panel title="Diagnostics" className="schema-workbench__side-panel">
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
                  <div
                    className="schema-workbench__preview-loading"
                    role="status"
                    aria-live="polite"
                  >
                    <Spinner label="Previewing schema draft" />
                    <p className="schema-workbench__preview-copy">
                      Validating the draft against the backend schema engine and
                      mapping issue paths back into the editor.
                    </p>
                  </div>
                ) : null}

                {previewState.status === "error" ? (
                  <Alert
                    tone="error"
                    title="Preview request failed"
                    message={
                      previewState.error ??
                      "Unable to preview the schema draft right now."
                    }
                  />
                ) : null}

                {previewState.status === "ready" ? (
                  <>
                    {backendErrorDiagnostics.length === 0 &&
                    backendWarningDiagnostics.length === 0 ? (
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

          <Panel title="Preview" className="schema-workbench__side-panel">
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
                description="Preview validates the draft against the backend schema engine and turns the diff into a grouped change summary before you open the apply review."
                icon="table"
                bullets={[
                  "Preview is non-mutating and safe to rerun",
                  "Destructive changes will be highlighted before apply",
                ]}
              />
            ) : previewState.status === "loading" ? (
              <div
                className="schema-workbench__preview-loading"
                role="status"
                aria-live="polite"
              >
                <Spinner label="Preparing preview result" />
                <p className="schema-workbench__preview-copy">
                  Building the grouped schema summary and destructive-change
                  details from the backend preview result.
                </p>
              </div>
            ) : previewState.status === "error" ? (
              <Alert
                tone="error"
                title="Preview unavailable"
                message={
                  previewState.error ??
                  "Unable to preview the schema draft right now."
                }
              />
            ) : previewState.response ? (
              <PreviewSummary
                errorCount={backendErrorDiagnostics.length}
                warningCount={backendWarningDiagnostics.length}
                response={previewState.response}
                groups={previewGroups}
                destructiveChangeCount={destructiveChangeCount}
                lossyChangeCount={lossyChangeCount}
              />
            ) : null}
          </Panel>
        </div>
      </div>

      <Dialog
        open={applyDialog.open}
        title="Review and apply schema changes"
        description="This dialog uses the latest preview result. Scroll through the summary to the end before applying the draft."
        onClose={() => {
          if (applyInFlight) {
            return;
          }

          resetApplyDialog();
        }}
        panelClassName="schema-workbench__apply-dialog"
        bodyClassName="schema-workbench__apply-dialog-body"
        footerClassName="schema-workbench__apply-dialog-footer"
        footer={
          <>
            <Button
              variant="ghost"
              disabled={applyInFlight}
              onClick={() => resetApplyDialog()}
            >
              Cancel
            </Button>
            <Button
              variant={applyDialog.confirmationRequired ? "danger" : "primary"}
              disabled={!applyDialog.canSubmit || applyInFlight}
              onClick={() => {
                void handleApply();
              }}
            >
              {applyInFlight
                ? applyDialog.confirmationRequired
                  ? "Applying destructive changes..."
                  : "Applying..."
                : applyDialog.confirmationRequired
                  ? "Apply destructive changes"
                  : "Apply"}
            </Button>
          </>
        }
      >
        <div className="schema-workbench__apply-dialog-shell">
          {applyDialog.error ? (
            <Alert
              tone={
                applyDialog.confirmationRequired ? "warning" : "error"
              }
              title={
                applyDialog.confirmationRequired
                  ? "Explicit destructive confirmation required"
                  : "Apply failed"
              }
              message={applyDialog.error}
            />
          ) : null}

          <div className="schema-workbench__apply-dialog-hint">
            <span className="schema-workbench__apply-dialog-hint-label">
              {applyDialog.canSubmit
                ? "Preview review complete"
                : "Scroll to the end to enable Apply"}
            </span>
            <span className="schema-workbench__apply-dialog-hint-copy">
              {previewState.response?.hasChanges
                ? `${countLabel(previewState.response.changes.length, "change")} from preview`
                : "No changes"}
            </span>
          </div>

          {previewState.response ? (
            <div
              ref={applyReviewRef}
              className="schema-workbench__apply-review"
              data-testid="schema-apply-review-scroll"
            >
              <PreviewSummary
                errorCount={backendErrorDiagnostics.length}
                warningCount={backendWarningDiagnostics.length}
                response={previewState.response}
                groups={previewGroups}
                destructiveChangeCount={destructiveChangeCount}
                lossyChangeCount={lossyChangeCount}
              />
            </div>
          ) : null}
        </div>
      </Dialog>

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

type PreviewChangeCardProps = {
  change: SchemaChange;
};

type PreviewSummaryProps = {
  errorCount: number;
  warningCount: number;
  response: SchemaPreviewResponse;
  groups: PreviewChangeGroup[];
  destructiveChangeCount: number;
  lossyChangeCount: number;
};

function PreviewSummary({
  errorCount,
  warningCount,
  response,
  groups,
  destructiveChangeCount,
  lossyChangeCount,
}: PreviewSummaryProps) {
  return (
    <div className="schema-workbench__preview-summary">
      <div className="schema-workbench__preview-stat-grid">
        <div className="schema-workbench__preview-stat">
          <span className="schema-workbench__preview-stat-label">Errors</span>
          <strong>{errorCount}</strong>
        </div>
        <div className="schema-workbench__preview-stat">
          <span className="schema-workbench__preview-stat-label">Warnings</span>
          <strong>{warningCount}</strong>
        </div>
        <div className="schema-workbench__preview-stat">
          <span className="schema-workbench__preview-stat-label">Changes</span>
          <strong>{response.changes.length}</strong>
        </div>
      </div>

      {response.isDestructive ? (
        <Alert
          tone="warning"
          title="Destructive changes require confirmation"
          message={
            lossyChangeCount > 0
              ? `${countLabel(destructiveChangeCount, "destructive change")} detected, including ${countLabel(lossyChangeCount, "lossy type conversion")}. Review the example casts before applying.`
              : `${countLabel(destructiveChangeCount, "destructive change")} detected. Applying this draft will require explicit confirmation.`
          }
        />
      ) : response.hasChanges ? (
        <Alert
          tone="success"
          title="No destructive changes detected"
          message="This preview only contains additive or reversible schema changes."
        />
      ) : null}

      {!response.hasChanges ? (
        <EmptyState
          title="No-op preview"
          description="The current draft already matches the live schema. Preview is safe to rerun, but there is no migration to apply."
          icon="table"
          bullets={[
            "No tables, columns, relations, or indexes would change",
            "The apply flow should stay disabled for this draft",
          ]}
        />
      ) : (
        <div className="schema-workbench__preview-groups">
          {groups.map((group) => (
            <section
              key={group.id}
              className="schema-workbench__preview-group"
              aria-label={group.title}
            >
              <div className="schema-workbench__preview-group-heading">
                <h3>{group.title}</h3>
                <span className="schema-workbench__preview-group-count">
                  {countLabel(group.changes.length, "change")}
                </span>
              </div>

              <ol className="schema-workbench__preview-change-list">
                {group.changes.map((change, index) => (
                  <PreviewChangeCard
                    key={`${group.id}:${change.changeType}:${change.table}:${change.column ?? index}`}
                    change={change}
                  />
                ))}
              </ol>
            </section>
          ))}
        </div>
      )}
    </div>
  );
}

function PreviewChangeCard({ change }: PreviewChangeCardProps) {
  const description = describePreviewChange(change);

  return (
    <li
      className={[
        "schema-workbench__preview-change",
        change.isDestructive
          ? "schema-workbench__preview-change--destructive"
          : "",
      ]
        .filter(Boolean)
        .join(" ")}
    >
      <div className="schema-workbench__preview-change-header">
        <div className="schema-workbench__preview-change-copy">
          <strong>{description.title}</strong>
          {description.detail ? (
            <p className="schema-workbench__preview-change-detail">
              {description.detail}
            </p>
          ) : null}
        </div>

        <div className="schema-workbench__preview-change-badges">
          {change.coercionKind ? (
            <span className="schema-workbench__preview-badge schema-workbench__preview-badge--muted">
              {change.coercionKind}
            </span>
          ) : null}
          {change.isDestructive ? (
            <span className="schema-workbench__preview-badge schema-workbench__preview-badge--destructive">
              Destructive
            </span>
          ) : null}
        </div>
      </div>

      {change.coercionKind === "Lossy" &&
      change.castExamples &&
      change.castExamples.length > 0 ? (
        <CastExamples examples={change.castExamples} />
      ) : null}
    </li>
  );
}

type CastExamplesProps = {
  examples: SchemaCastExample[];
};

function CastExamples({ examples }: CastExamplesProps) {
  return (
    <div className="schema-workbench__cast-examples">
      <h4>Example Casts (5 first rows)</h4>
      <ol className="schema-workbench__cast-example-list">
        {examples.map((example, index) => (
          <li
            key={`${formatPreviewValue(example.sourceValue)}:${formatPreviewValue(example.castValue)}:${index}`}
            className="schema-workbench__cast-example"
          >
            <code className="mono">
              {formatPreviewValue(example.sourceValue)}
              {" -> "}
              {example.isValid
                ? formatPreviewValue(example.castValue)
                : "invalid cast"}
            </code>
            {!example.isValid && example.error ? (
              <span className="schema-workbench__cast-example-error">
                {example.error}
              </span>
            ) : null}
          </li>
        ))}
      </ol>
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
    <section
      className="schema-workbench__diagnostic-section"
      aria-label={title}
    >
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
              ]
                .filter(Boolean)
                .join(" ")}
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
                  {diagnostic.source === "syntax"
                    ? "Syntax"
                    : diagnostic.severity === "error"
                      ? "Error"
                      : "Warning"}
                </span>
                <code className="schema-workbench__diagnostic-code">
                  {diagnostic.code}
                </code>
                {diagnostic.path ? (
                  <span className="schema-workbench__diagnostic-path mono">
                    {diagnostic.path}
                  </span>
                ) : null}
                {locationLabel ? (
                  <span className="schema-workbench__diagnostic-location">
                    {locationLabel}
                  </span>
                ) : null}
              </div>
              <p className="schema-workbench__diagnostic-message">
                {diagnostic.message}
              </p>
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
