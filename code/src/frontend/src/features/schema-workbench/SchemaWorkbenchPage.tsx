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
  getCanonicalSchemaSnapshot,
  getSchemaConfirmationRequiredDetails,
  getSchemaSnapshot,
  getSchemaValidationFailedDetails,
  previewSchemaDraft,
} from "../../api/schema";
import { ApiClientError } from "../../api/client";
import { useAuthStore } from "../../stores/authStore";
import { useSchemaStore } from "../../stores/schemaStore";
import type {
  SchemaIssue,
  SchemaModelDocument,
  SchemaPreviewResponse,
} from "../../types/api";
import { JsonCodeEditor, type JsonEditorDiagnostic } from "./JsonCodeEditor";
import { parseJsonDraft, type JsonDraftLocation } from "./draftJson";
import {
  asSchemaModelDocument,
  formatSchemaDraftValueForEditor,
} from "./schemaDraftPresentation";
import { validateVisualSchemaDraft } from "./visualSchemaDraft";
import { buildMiniErdChangeMap, SchemaMiniErd } from "./MiniErd";
import {
  buildPreviewGroups,
  PreviewDiagnostics,
  PreviewSummary,
} from "./previewPresentation";
import { countLabel } from "./schemaWorkbenchHelpers";
import type { VisualCanvasSelection, WorkbenchDiagnostic } from "./types";
import {
  buildBackendDiagnostic,
  resolvePreviewErrorLocation,
} from "./workbenchDiagnostics";
import { VisualSchemaInspector } from "./visual/VisualSchemaInspector";
import { VisualSchemaMode } from "./visual/VisualSchemaMode";
import "./SchemaWorkbenchPage.css";

type WorkbenchStatus = "loading" | "ready" | "error";
type WorkbenchMode = "json" | "visual";

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
  errorCode: string | null;
  /** JSON path the server reported for a malformed draft, e.g. "$.tables[0]". */
  errorPath?: string | null;
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
  errorCode: null,
};

const initialApplyDialogState: ApplyDialogState = {
  open: false,
  canSubmit: false,
  confirmationRequired: false,
  error: null,
};

const autoPreviewDebounceMs = 800;

function isEmptySchema(document: SchemaModelDocument): boolean {
  return document.tables.length === 0 && document.relations.length === 0;
}

function formatSchemaText(document: SchemaModelDocument): string {
  return formatSchemaDraftValueForEditor(document);
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
  const [refreshConfirmOpen, setRefreshConfirmOpen] = useState(false);
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
  const [activeMode, setActiveMode] = useState<WorkbenchMode>("json");
  const [visualSelection, setVisualSelection] =
    useState<VisualCanvasSelection | null>(null);
  const importInputRef = useRef<HTMLInputElement | null>(null);
  const applyReviewRef = useRef<HTMLDivElement | null>(null);
  const previewRequestIdRef = useRef(0);
  const currentDraftTextRef = useRef(state.draftText);
  const previewedDraftTextRef = useRef<string | null>(null);
  currentDraftTextRef.current = state.draftText;

  function resetApplyDialog() {
    setApplyDialog(initialApplyDialogState);
    setApplyInFlight(false);
  }

  function resetPreviewState() {
    previewRequestIdRef.current += 1;
    previewedDraftTextRef.current = null;
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
        const snapshot = await getCanonicalSchemaSnapshot();

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
            : "Your schema could not be loaded. Try again in a moment.";

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
        maxScrollTop <= 8 || reviewContainer!.scrollTop >= maxScrollTop - 8;

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
  const baselineDocument = useMemo(() => {
    if (state.status !== "ready") {
      return null;
    }

    try {
      return asSchemaModelDocument(JSON.parse(state.loadedText));
    } catch {
      return null;
    }
  }, [state.loadedText, state.status]);
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
  const previewRequestErrorDiagnostics = useMemo<WorkbenchDiagnostic[]>(() => {
    if (previewState.status !== "error" || !previewState.error) {
      return [];
    }

    const code = previewState.errorCode ?? "preview_request_failed";
    const location = resolvePreviewErrorLocation(
      previewState.error,
      state.draftText,
      previewState.errorPath ?? null,
    );

    return [
      {
        code,
        id: `preview:error:${code}:${location?.path ?? "request"}`,
        location: location?.location ?? null,
        locationExact: location?.locationExact ?? false,
        message: previewState.error,
        path: location?.path ?? null,
        severity: "error",
        source: "preview",
      },
    ];
  }, [
    previewState.error,
    previewState.errorCode,
    previewState.errorPath,
    previewState.status,
    state.draftText,
  ]);
  const previewErrorDiagnostics = useMemo(
    () => [
      ...syntaxDiagnostics,
      ...backendErrorDiagnostics,
      ...previewRequestErrorDiagnostics,
    ],
    [
      syntaxDiagnostics,
      backendErrorDiagnostics,
      previewRequestErrorDiagnostics,
    ],
  );
  const editorDiagnostics = useMemo<JsonEditorDiagnostic[]>(
    () =>
      [...previewErrorDiagnostics, ...backendWarningDiagnostics]
        .filter((diagnostic) => diagnostic.location)
        .map((diagnostic) => ({
          line: diagnostic.location?.line ?? 1,
          severity: diagnostic.severity,
        })),
    [previewErrorDiagnostics, backendWarningDiagnostics],
  );
  const activeLine = useMemo(() => {
    const activeDiagnostic = [
      ...previewErrorDiagnostics,
      ...backendWarningDiagnostics,
    ].find((diagnostic) => diagnostic.id === activeDiagnosticId);

    if (activeDiagnostic?.location) {
      return activeDiagnostic.location.line;
    }

    return previewErrorDiagnostics[0]?.location?.line ?? null;
  }, [activeDiagnosticId, previewErrorDiagnostics, backendWarningDiagnostics]);
  const previewGroups = useMemo(
    () => buildPreviewGroups(previewState.response?.changes ?? []),
    [previewState.response],
  );
  const miniErdDocument = useMemo(() => {
    if (draftParse?.status !== "valid") {
      return null;
    }

    const previewDocument =
      previewState.status === "ready"
        ? previewState.response?.normalizedModel
        : null;

    return asSchemaModelDocument(previewDocument ?? draftParse.value);
  }, [draftParse, previewState]);
  const miniErdChangeMap = useMemo(
    () =>
      buildMiniErdChangeMap(
        previewState.status === "ready"
          ? (previewState.response?.changes ?? [])
          : [],
      ),
    [previewState.response, previewState.status],
  );
  const isLayoutOnlyDraft =
    draftParse?.status === "valid" &&
    previewState.status === "ready" &&
    previewState.response !== null &&
    previewState.response.isValid &&
    !previewState.response.hasChanges &&
    previewState.response.metadataOnly === true;
  const canOpenApplyDialog =
    draftParse?.status === "valid" &&
    previewState.status === "ready" &&
    previewState.response !== null &&
    previewState.response.isValid &&
    (previewState.response.hasChanges || isLayoutOnlyDraft) &&
    backendErrorDiagnostics.length === 0 &&
    !applyInFlight;
  const previewContentHiddenByErrors =
    draftParse?.status !== "valid" || previewState.status === "error";
  const visualDraftDocument = useMemo(() => {
    if (draftParse?.status !== "valid") {
      return null;
    }

    return asSchemaModelDocument(draftParse.value);
  }, [draftParse]);
  const visualDraftIssues = useMemo(
    () =>
      visualDraftDocument ? validateVisualSchemaDraft(visualDraftDocument) : [],
    [visualDraftDocument],
  );

  function applyImportedDraft(text: string) {
    replaceDraftText(text);
  }

  function requestSchemaRefresh() {
    if (dirty) {
      setRefreshConfirmOpen(true);
      return;
    }

    setReloadKey((current) => current + 1);
  }

  function confirmSchemaRefresh() {
    setRefreshConfirmOpen(false);
    setReloadKey((current) => current + 1);
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

    const formattedText = formatSchemaDraftValueForEditor(importedDraft.value);
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

    const blob = new Blob([formatSchemaDraftValueForEditor(draftParse.value)], {
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

  async function runPreview(model: SchemaModelDocument, draftText: string) {
    const requestId = previewRequestIdRef.current + 1;
    previewRequestIdRef.current = requestId;
    previewedDraftTextRef.current = draftText;
    setPreviewState({
      status: "loading",
      response: null,
      error: null,
      errorCode: null,
    });
    setActiveDiagnosticId(null);

    try {
      const response = await previewSchemaDraft(model);
      if (previewRequestIdRef.current !== requestId) {
        return;
      }

      setPreviewState({
        status: "ready",
        response,
        error: null,
        errorCode: null,
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
            : "The preview could not be run. Try again in a moment.",
        errorCode:
          error instanceof ApiClientError
            ? (error.code ?? "preview_request_failed")
            : "preview_request_failed",
        errorPath: error instanceof ApiClientError ? readErrorPath(error.details) : null,
      });
    }
  }

  async function handlePreview() {
    if (draftParse?.status !== "valid") {
      return;
    }

    await runPreview(draftParse.value as SchemaModelDocument, state.draftText);
  }

  useEffect(() => {
    if (
      !isAdmin ||
      state.status !== "ready" ||
      state.draftText === state.loadedText
    ) {
      return;
    }

    const draftText = state.draftText;
    const autoPreviewDraft = parseJsonDraft(draftText);
    if (autoPreviewDraft.status !== "valid") {
      return;
    }

    const timeoutId = window.setTimeout(() => {
      if (
        currentDraftTextRef.current !== draftText ||
        previewedDraftTextRef.current === draftText
      ) {
        return;
      }

      void runPreview(autoPreviewDraft.value as SchemaModelDocument, draftText);
    }, autoPreviewDebounceMs);

    return () => {
      window.clearTimeout(timeoutId);
    };
  }, [isAdmin, state.draftText, state.loadedText, state.status]);

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
    const snapshot = await getCanonicalSchemaSnapshot();
    const enrichedSnapshot = await getSchemaSnapshot();
    const loadedAt = Date.now();

    useSchemaStore.setState({
      status: "ready",
      snapshot: enrichedSnapshot,
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
      const applyDraft = draftParse.value as SchemaModelDocument;
      const applyResult = await applySchemaDraft(
        applyDraft,
        applyDialog.confirmationRequired
          ? { confirmDestructive: true }
          : undefined,
      );

      if (!applyResult.applied) {
        try {
          await refreshAppliedSchemaState();
        } catch {
          // The apply was a no-op; keep the local draft and show the no-op state.
        }

        resetPreviewState();
        setApplyDialog(initialApplyDialogState);
        setApplyFeedback({
          tone: "warning",
          title: "No schema changes applied",
          message: "Your draft already matches the current schema. Nothing to apply.",
        });
        return;
      }

      try {
        await refreshAppliedSchemaState();
      } catch (error) {
        const message =
          error instanceof ApiClientError
            ? error.message
            : "Changes applied, but the latest schema could not be reloaded. Refresh the page to see it.";

        setApplyFeedback({
          tone: "warning",
          title: "Schema changes applied",
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
        title: applyResult.metadataOnly
          ? "Layout saved"
          : "Schema changes applied",
        message: applyResult.metadataOnly
          ? "Table colors and positions saved."
          : `${countLabel(applyResult.changes.length, "change")} applied.`,
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
            errorCode: null,
          };
        });
        setApplyDialog(initialApplyDialogState);
        setApplyFeedback({
          tone: "error",
          title: "Apply blocked by validation errors",
          message:
            "Your changes were not applied. Fix the problems listed under Preview, then preview again.",
        });
        return;
      }

      const confirmationDetails = getSchemaConfirmationRequiredDetails(error);
      if (confirmationDetails) {
        setApplyDialog((current) => ({
          ...current,
          confirmationRequired: true,
          error:
            "Some changes will delete data. Review them, then click Apply destructive changes.",
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
            description="Only administrators can change the schema."
            icon="schema"
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
            message={
              state.error ??
              "Your schema could not be loaded. Try again in a moment."
            }
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
        <div
          className="schema-workbench__mode-switch"
          role="group"
          aria-label="Schema workbench mode"
        >
          <Button
            size="sm"
            className={
              activeMode === "json"
                ? "schema-workbench__mode-button schema-workbench__mode-button--active"
                : "schema-workbench__mode-button"
            }
            aria-pressed={activeMode === "json"}
            onClick={() => setActiveMode("json")}
          >
            JSON
          </Button>
          <Button
            size="sm"
            className={
              activeMode === "visual"
                ? "schema-workbench__mode-button schema-workbench__mode-button--active"
                : "schema-workbench__mode-button"
            }
            aria-pressed={activeMode === "visual"}
            onClick={() => setActiveMode("visual")}
          >
            Visual
          </Button>
        </div>
        <Button size="sm" onClick={requestSchemaRefresh}>
          Refresh
        </Button>
        <Button
          size="sm"
          disabled={draftParse?.status !== "valid"}
          onClick={() => {
            if (draftParse?.status !== "valid") {
              return;
            }

            replaceDraftText(formatSchemaDraftValueForEditor(draftParse.value));
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
          variant="accent"
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
          message="Your workspace has no tables yet. Add one in Visual mode or import a schema file."
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
          className={
            activeMode === "json"
              ? "schema-workbench__editor-panel"
              : "schema-workbench__editor-panel schema-workbench__visual-panel"
          }
          bodyClassName="schema-workbench__editor-body"
        >
          {activeMode === "json" ? (
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
          ) : (
            <VisualSchemaMode
              activeDiagnosticId={activeDiagnosticId}
              backendDiagnostics={[
                ...backendErrorDiagnostics,
                ...backendWarningDiagnostics,
              ]}
              document={visualDraftDocument}
              issues={visualDraftIssues}
              selection={visualSelection}
              syntaxInvalid={draftParse?.status !== "valid"}
              onBackendDiagnosticSelect={(diagnostic, nextSelection) => {
                focusDiagnostic(diagnostic);
                setVisualSelection(nextSelection);
              }}
              onDocumentChange={(document) => {
                replaceDraftText(formatSchemaDraftValueForEditor(document));
              }}
              onSelectionChange={setVisualSelection}
              onReturnToJson={() => setActiveMode("json")}
            />
          )}
        </Panel>

        <div className="schema-workbench__side">
          <Panel
            title={activeMode === "visual" ? "Edit Selection" : "Schema Map"}
            className="schema-workbench__side-panel"
          >
            {activeMode === "visual" ? (
              draftParse?.status !== "valid" || !visualDraftDocument ? (
                <EmptyState
                  title="Visual inspector unavailable"
                  description="Fix the JSON errors to edit tables and columns."
                  icon="pencil"
                />
              ) : (
                <VisualSchemaInspector
                  baselineDocument={baselineDocument}
                  document={visualDraftDocument}
                  selection={visualSelection}
                  onDocumentChange={(document) => {
                    replaceDraftText(formatSchemaDraftValueForEditor(document));
                  }}
                  onSelectionChange={setVisualSelection}
                />
              )
            ) : (
              <SchemaMiniErd
                baselineDocument={baselineDocument}
                changeMap={miniErdChangeMap}
                document={miniErdDocument}
                syntaxInvalid={draftParse?.status !== "valid"}
              />
            )}
          </Panel>

          <Panel title="Preview" className="schema-workbench__side-panel">
            <div className="schema-workbench__preview-panel">
              <PreviewDiagnostics
                activeDiagnosticId={activeDiagnosticId}
                errorDiagnostics={previewErrorDiagnostics}
                onSelect={focusDiagnostic}
                warningDiagnostics={backendWarningDiagnostics}
              />

              {previewContentHiddenByErrors ? null : previewState.status ===
                "idle" ? (
                <EmptyState
                  title="Run Preview to validate this draft"
                  description="Preview checks your changes and lists what will happen before you apply them."
                  icon="table"
                  bullets={["Previewing never changes your data."]}
                />
              ) : previewState.status === "loading" ? (
                <div
                  className="schema-workbench__preview-loading"
                  role="status"
                  aria-live="polite"
                >
                  <Spinner label="Checking your changes…" />
                </div>
              ) : previewState.response ? (
                <PreviewSummary
                  errorCount={previewErrorDiagnostics.length}
                  warningCount={backendWarningDiagnostics.length}
                  isLayoutOnlyDraft={isLayoutOnlyDraft}
                  response={previewState.response}
                  groups={previewGroups}
                />
              ) : null}
            </div>
          </Panel>
        </div>
      </div>

      <Dialog
        open={applyDialog.open}
        title="Review and apply schema changes"
        description="Review every change below, then apply."
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
              tone={applyDialog.confirmationRequired ? "warning" : "error"}
              title={
                applyDialog.confirmationRequired
                  ? "Confirm data loss"
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
                errorCount={previewErrorDiagnostics.length}
                warningCount={backendWarningDiagnostics.length}
                isLayoutOnlyDraft={isLayoutOnlyDraft}
                response={previewState.response}
                groups={previewGroups}
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
            ? `Importing “${pendingImport.fileName}” will replace your unsaved changes.`
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

      <ConfirmDialog
        open={refreshConfirmOpen}
        title="Discard local draft edits?"
        description="Refreshing will replace your unsaved changes with the saved schema."
        confirmLabel="Discard edits and refresh"
        cancelLabel="Keep editing"
        confirmVariant="danger"
        onConfirm={confirmSchemaRefresh}
        onCancel={() => setRefreshConfirmOpen(false)}
      />
    </div>
  );
}

function readErrorPath(details: unknown): string | null {
  if (details && typeof details === "object" && "path" in details) {
    const path = (details as { path: unknown }).path;
    return typeof path === "string" && path.length > 0 ? path : null;
  }
  return null;
}
