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
  SchemaCastExample,
  SchemaChange,
  SchemaColumn,
  SchemaIssue,
  SchemaModelDocument,
  SchemaPreviewResponse,
  SchemaRelation,
  SchemaTable,
} from "../../types/api";
import { JsonCodeEditor, type JsonEditorDiagnostic } from "./JsonCodeEditor";
import { parseJsonDraft, type JsonDraftLocation } from "./draftJson";
import { resolveJsonPathLocation } from "./jsonPathLocation";
import { formatSchemaDraftValueForEditor } from "./schemaDraftPresentation";
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
  errorCode: string | null;
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
  source: "backend" | "preview" | "syntax";
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

type MiniErdChangeTone = "added" | "changed" | "removed";

type MiniErdChangeAnnotation = {
  destructive: boolean;
  label: string;
  tone: MiniErdChangeTone;
  typeTransition?: {
    from: string;
    to: string;
  };
};

type MiniErdChangeMap = {
  columns: Map<string, MiniErdChangeAnnotation>;
  hasChanges: boolean;
  relations: Map<string, MiniErdChangeAnnotation>;
  tables: Map<string, MiniErdChangeAnnotation>;
};

const numericColumnTypeLabels = [
  "text",
  "integer",
  "float",
  "boolean",
  "date",
  "datetime",
  "json",
  "reference",
  "computed",
  "currency",
  "enum",
  "file",
];

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
          change.changeType === "RenameTable" ||
          change.changeType === "AlterTableMetadata",
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
          change.changeType === "AlterColumnMetadata" ||
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
    case "AlterTableMetadata":
      return { title: `Update table details for ${change.table}` };
    case "AddColumn":
      return { title: `Add column ${getQualifiedColumnName(change)}` };
    case "DropColumn":
      return { title: `Drop column ${getQualifiedColumnName(change)}` };
    case "RenameColumn":
      return {
        title: `Rename column ${change.table}.${change.previousColumn ?? change.column} to ${change.column ?? change.previousColumn}`,
      };
    case "AlterColumnMetadata":
      return { title: `Update column details for ${getQualifiedColumnName(change)}` };
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

function resolvePreviewErrorLocation(
  message: string,
  draftText: string,
): Pick<WorkbenchDiagnostic, "location" | "locationExact" | "path"> | null {
  const path = extractServerJsonPath(message);
  if (path) {
    const resolution = resolveJsonPathLocation(draftText, path);
    if (resolution) {
      return {
        location: resolution.location,
        locationExact: resolution.exact,
        path,
      };
    }
  }

  const lineByteLocation = extractServerLineByteLocation(message, draftText);
  if (lineByteLocation) {
    return {
      location: lineByteLocation,
      locationExact: true,
      path: path ?? null,
    };
  }

  const scalarPath = resolveQuotedScalarPath(message, draftText);
  if (scalarPath) {
    const resolution = resolveJsonPathLocation(draftText, scalarPath);
    if (resolution) {
      return {
        location: resolution.location,
        locationExact: resolution.exact,
        path: scalarPath,
      };
    }
  }

  return null;
}

function extractServerJsonPath(message: string): string | null {
  const match = message.match(/Path:\s*([^|]+?)(?:\s*\||$)/i);
  if (!match) {
    return null;
  }

  let path = match[1].trim();
  if (!path) {
    return null;
  }

  path = path
    .replace(/^\$/, "")
    .replace(/^\./, "")
    .replace(/\[['"]([^'"]+)['"]\]/g, ".$1");

  return path || "schema";
}

function extractServerLineByteLocation(
  message: string,
  draftText: string,
): JsonDraftLocation | null {
  const match = message.match(
    /LineNumber:\s*(\d+)\s*\|\s*BytePositionInLine:\s*(\d+)/i,
  );
  if (!match) {
    return null;
  }

  const lineNumber = Number.parseInt(match[1], 10);
  const bytePosition = Number.parseInt(match[2], 10);
  if (
    !Number.isFinite(lineNumber) ||
    !Number.isFinite(bytePosition) ||
    lineNumber < 0 ||
    bytePosition < 0
  ) {
    return null;
  }

  const line = lineNumber + 1;
  const column = bytePosition + 1;
  if (line > draftText.split("\n").length) {
    return null;
  }

  return { line, column };
}

function resolveQuotedScalarPath(
  message: string,
  draftText: string,
): string | null {
  const quotedValues = [...message.matchAll(/['"]([^'"]+)['"]/g)]
    .map((match) => match[1])
    .filter((value) => value.length > 0);

  if (quotedValues.length === 0) {
    return null;
  }

  try {
    const document = JSON.parse(draftText) as unknown;
    for (const value of quotedValues) {
      const path = findScalarValuePath(document, value);
      if (path) {
        return path;
      }
    }
  } catch {
    return null;
  }

  return null;
}

function findScalarValuePath(
  value: unknown,
  target: string,
  base = "",
): string | null {
  if (value === target) {
    return base || "schema";
  }

  if (Array.isArray(value)) {
    for (let index = 0; index < value.length; index += 1) {
      const path = findScalarValuePath(
        value[index],
        target,
        `${base}[${index}]`,
      );
      if (path) {
        return path;
      }
    }

    return null;
  }

  if (value && typeof value === "object") {
    for (const [key, child] of Object.entries(value)) {
      const path = findScalarValuePath(
        child,
        target,
        base ? `${base}.${key}` : key,
      );
      if (path) {
        return path;
      }
    }
  }

  return null;
}

function asSchemaModelDocument(value: unknown): SchemaModelDocument | null {
  if (!value || typeof value !== "object") {
    return null;
  }

  const candidate = value as Partial<SchemaModelDocument>;
  if (!Array.isArray(candidate.tables) || !Array.isArray(candidate.relations)) {
    return null;
  }

  return candidate as SchemaModelDocument;
}

function getColumnBadgeLabels(column: SchemaColumn): string[] {
  const labels: string[] = [];

  if (column.primaryKey) {
    labels.push("PK");
  }

  if (column.unique) {
    labels.push("Unique");
  }

  if (column.nullable === false) {
    labels.push("Required");
  }

  return labels;
}

function getRelationEndpoint(
  relation: SchemaRelation,
  preferredKey: "from" | "to",
): string {
  const fallbackKey = preferredKey === "from" ? "fromTable" : "toTable";
  const directValue = relation[preferredKey];
  const fallbackValue = relation[fallbackKey];

  return typeof directValue === "string"
    ? directValue
    : typeof fallbackValue === "string"
      ? fallbackValue
      : "Unknown";
}

function getColumnChangeKey(tableName: string, columnName: string): string {
  return `${tableName}.${columnName}`;
}

function createEmptyMiniErdChangeMap(): MiniErdChangeMap {
  return {
    columns: new Map(),
    hasChanges: false,
    relations: new Map(),
    tables: new Map(),
  };
}

function setMiniErdChangeAnnotation(
  map: Map<string, MiniErdChangeAnnotation>,
  key: string | null | undefined,
  annotation: MiniErdChangeAnnotation,
) {
  if (!key) {
    return;
  }

  const current = map.get(key);
  if (
    current?.tone === "removed" ||
    (current?.destructive && !annotation.destructive)
  ) {
    return;
  }

  map.set(key, annotation);
}

function getChangedAnnotation(change: SchemaChange): MiniErdChangeAnnotation {
  return {
    tone: "changed",
    label: change.isDestructive ? "Destructive" : "Changed",
    destructive: change.isDestructive,
  };
}

function formatMiniErdTypeValue(value: unknown): string {
  if (typeof value === "string") {
    return value.trim().toLowerCase();
  }

  if (typeof value === "number" && Number.isInteger(value)) {
    return numericColumnTypeLabels[value] ?? String(value);
  }

  return formatPreviewValue(value);
}

function buildMiniErdChangeMap(changes: SchemaChange[]): MiniErdChangeMap {
  const changeMap = createEmptyMiniErdChangeMap();
  changeMap.hasChanges = changes.length > 0;

  for (const change of changes) {
    switch (change.changeType) {
      case "AddTable":
        setMiniErdChangeAnnotation(changeMap.tables, change.table, {
          tone: "added",
          label: "Added",
          destructive: false,
        });
        break;
      case "DropTable":
        setMiniErdChangeAnnotation(changeMap.tables, change.table, {
          tone: "removed",
          label: "Removed",
          destructive: true,
        });
        break;
      case "RenameTable":
        setMiniErdChangeAnnotation(changeMap.tables, change.table, {
          tone: "changed",
          label: "Renamed",
          destructive: change.isDestructive,
        });
        break;
      case "AddColumn":
        setMiniErdChangeAnnotation(
          changeMap.columns,
          change.column
            ? getColumnChangeKey(change.table, change.column)
            : undefined,
          {
            tone: "added",
            label: "Added",
            destructive: false,
          },
        );
        break;
      case "DropColumn":
        setMiniErdChangeAnnotation(
          changeMap.columns,
          change.column
            ? getColumnChangeKey(change.table, change.column)
            : undefined,
          {
            tone: "removed",
            label: "Removed",
            destructive: true,
          },
        );
        break;
      case "RenameColumn":
        setMiniErdChangeAnnotation(
          changeMap.columns,
          change.column
            ? getColumnChangeKey(change.table, change.column)
            : undefined,
          {
            tone: "changed",
            label: "Renamed",
            destructive: change.isDestructive,
          },
        );
        break;
      case "AlterColumnType":
        setMiniErdChangeAnnotation(
          changeMap.columns,
          change.column
            ? getColumnChangeKey(change.table, change.column)
            : undefined,
          {
            tone: "changed",
            label: change.isDestructive ? "Destructive" : "Type changed",
            destructive: change.isDestructive,
            typeTransition: {
              from: formatMiniErdTypeValue(change.oldValue),
              to: formatMiniErdTypeValue(change.newValue),
            },
          },
        );
        break;
      case "AlterColumnNullability":
      case "AlterColumnDefault":
      case "AlterColumnUnique":
        setMiniErdChangeAnnotation(
          changeMap.columns,
          change.column
            ? getColumnChangeKey(change.table, change.column)
            : undefined,
          getChangedAnnotation(change),
        );
        break;
      case "AddRelation":
        setMiniErdChangeAnnotation(changeMap.relations, change.relation, {
          tone: "added",
          label: "Added",
          destructive: false,
        });
        break;
      case "DropRelation":
        setMiniErdChangeAnnotation(changeMap.relations, change.relation, {
          tone: "removed",
          label: "Removed",
          destructive: true,
        });
        break;
      case "AlterRelation":
        setMiniErdChangeAnnotation(
          changeMap.relations,
          change.relation,
          getChangedAnnotation(change),
        );
        break;
      case "AddIndex":
      case "DropIndex":
      case "RenameIndex":
      case "AlterIndex":
        setMiniErdChangeAnnotation(
          changeMap.tables,
          change.table,
          getChangedAnnotation(change),
        );
        break;
      default:
        break;
    }
  }

  return changeMap;
}

function getMiniErdAnnotationClass(
  baseClass: string,
  annotation: MiniErdChangeAnnotation | null | undefined,
): string {
  if (!annotation) {
    return baseClass;
  }

  return [
    baseClass,
    `${baseClass}--${annotation.tone}`,
    annotation.destructive ? `${baseClass}--destructive` : "",
  ]
    .filter(Boolean)
    .join(" ");
}

function findTableByName(
  document: SchemaModelDocument | null,
  tableName: string,
): SchemaTable | null {
  return document?.tables.find((table) => table.name === tableName) ?? null;
}

function findRelationByName(
  document: SchemaModelDocument | null,
  relationName: string,
): SchemaRelation | null {
  return (
    document?.relations.find((relation) => relation.name === relationName) ??
    null
  );
}

function buildMiniErdTables(
  document: SchemaModelDocument,
  baselineDocument: SchemaModelDocument | null,
  changeMap: MiniErdChangeMap,
): SchemaTable[] {
  const tables = [...document.tables];
  const names = new Set(tables.map((table) => table.name));

  changeMap.tables.forEach((annotation, tableName) => {
    if (annotation.tone !== "removed" || names.has(tableName)) {
      return;
    }

    const baselineTable = findTableByName(baselineDocument, tableName);
    if (baselineTable) {
      tables.push(baselineTable);
      names.add(tableName);
    }
  });

  return tables;
}

function buildMiniErdColumns(
  table: SchemaTable,
  baselineTable: SchemaTable | null,
  changeMap: MiniErdChangeMap,
): SchemaColumn[] {
  const columns = [...table.columns];
  const names = new Set(columns.map((column) => column.name));

  changeMap.columns.forEach((annotation, key) => {
    if (annotation.tone !== "removed" || !key.startsWith(`${table.name}.`)) {
      return;
    }

    const columnName = key.slice(table.name.length + 1);
    if (names.has(columnName)) {
      return;
    }

    const baselineColumn = baselineTable?.columns.find(
      (column) => column.name === columnName,
    );
    if (baselineColumn) {
      columns.push(baselineColumn);
      names.add(columnName);
    }
  });

  return columns;
}

function buildMiniErdRelations(
  document: SchemaModelDocument,
  baselineDocument: SchemaModelDocument | null,
  changeMap: MiniErdChangeMap,
): SchemaRelation[] {
  const relations = [...document.relations];
  const names = new Set(relations.map((relation) => relation.name));

  changeMap.relations.forEach((annotation, relationName) => {
    if (annotation.tone !== "removed" || names.has(relationName)) {
      return;
    }

    const baselineRelation = findRelationByName(baselineDocument, relationName);
    if (baselineRelation) {
      relations.push(baselineRelation);
      names.add(relationName);
    }
  });

  return relations;
}

function getMiniErdColumnTypeLabel(
  column: SchemaColumn,
  annotation: MiniErdChangeAnnotation | null | undefined,
): string {
  if (annotation?.typeTransition) {
    return `${annotation.typeTransition.from} -> ${annotation.typeTransition.to}`;
  }

  return formatMiniErdTypeValue(column.type);
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
  const canOpenApplyDialog =
    draftParse?.status === "valid" &&
    previewState.status === "ready" &&
    previewState.response !== null &&
    previewState.response.isValid &&
    previewState.response.hasChanges &&
    backendErrorDiagnostics.length === 0 &&
    !applyInFlight;
  const previewContentHiddenByErrors =
    draftParse?.status !== "valid" || previewState.status === "error";

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
            : "Unable to preview the schema draft right now.",
        errorCode:
          error instanceof ApiClientError
            ? (error.code ?? "preview_request_failed")
            : "preview_request_failed",
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
            errorCode: null,
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
          <Panel title="Schema Map" className="schema-workbench__side-panel">
            <SchemaMiniErd
              baselineDocument={baselineDocument}
              changeMap={miniErdChangeMap}
              document={miniErdDocument}
              syntaxInvalid={draftParse?.status !== "valid"}
            />
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
              ) : previewState.response ? (
                <PreviewSummary
                  errorCount={previewErrorDiagnostics.length}
                  warningCount={backendWarningDiagnostics.length}
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
              tone={applyDialog.confirmationRequired ? "warning" : "error"}
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
                errorCount={previewErrorDiagnostics.length}
                warningCount={backendWarningDiagnostics.length}
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

      <ConfirmDialog
        open={refreshConfirmOpen}
        title="Discard local draft edits?"
        description="Refreshing will replace your unsaved local edits in the Model JSON draft with the latest server snapshot."
        confirmLabel="Discard edits and refresh"
        cancelLabel="Keep editing"
        confirmVariant="danger"
        onConfirm={confirmSchemaRefresh}
        onCancel={() => setRefreshConfirmOpen(false)}
      />
    </div>
  );
}

type SchemaMiniErdProps = {
  baselineDocument: SchemaModelDocument | null;
  changeMap: MiniErdChangeMap;
  document: SchemaModelDocument | null;
  syntaxInvalid: boolean;
};

function SchemaMiniErd({
  baselineDocument,
  changeMap,
  document,
  syntaxInvalid,
}: SchemaMiniErdProps) {
  if (syntaxInvalid) {
    return (
      <EmptyState
        title="Fix JSON syntax to update the schema map"
        description="The map stays empty while the draft is not valid JSON, so it never shows a stale structure."
        icon="schema"
        bullets={[
          "The JSON editor remains editable",
          "The schema map updates again as soon as the draft parses",
        ]}
      />
    );
  }

  if (!document) {
    return (
      <EmptyState
        title="Draft structure is not ready for a schema map"
        description="The JSON parses, but the draft does not expose the tables and relations arrays the workbench needs for a read-only topology preview."
        icon="schema"
        bullets={[
          "Add a tables array",
          "Add a relations array, even when it is empty",
        ]}
      />
    );
  }

  const tables = buildMiniErdTables(document, baselineDocument, changeMap);
  const relations = buildMiniErdRelations(
    document,
    baselineDocument,
    changeMap,
  );

  if (tables.length === 0) {
    return (
      <EmptyState
        title="No tables in this draft"
        description="The schema map will appear here once the draft includes at least one table."
        icon="schema"
        bullets={[
          "Empty schemas are valid for a new workspace",
          "Relations appear after both endpoints exist in the draft",
        ]}
      />
    );
  }

  return (
    <div
      className="schema-workbench__mini-erd"
      aria-label="Read-only schema map"
      data-testid="schema-mini-erd"
    >
      <div className="schema-workbench__mini-erd-stats">
        <div className="schema-workbench__mini-erd-stat">
          <Icon name="table" size={14} />
          <strong>{tables.length}</strong>
          <span>{tables.length === 1 ? "table" : "tables"}</span>
        </div>
        <div className="schema-workbench__mini-erd-stat">
          <Icon name="link" size={14} />
          <strong>{relations.length}</strong>
          <span>{relations.length === 1 ? "relation" : "relations"}</span>
        </div>
      </div>

      <div className="schema-workbench__mini-erd-table-grid">
        {tables.map((table) => (
          <MiniErdTable
            key={table.name}
            annotation={changeMap.tables.get(table.name)}
            baselineTable={findTableByName(baselineDocument, table.name)}
            changeMap={changeMap}
            table={table}
          />
        ))}
      </div>

      {relations.length > 0 ? (
        <section
          className="schema-workbench__mini-erd-relations"
          aria-label="Schema relations"
        >
          <h3>Relations</h3>
          <ol className="schema-workbench__mini-erd-relation-list">
            {relations.map((relation) => (
              <MiniErdRelation
                key={relation.name}
                annotation={changeMap.relations.get(relation.name)}
                relation={relation}
              />
            ))}
          </ol>
        </section>
      ) : null}
    </div>
  );
}

type MiniErdTableProps = {
  annotation?: MiniErdChangeAnnotation;
  baselineTable: SchemaTable | null;
  changeMap: MiniErdChangeMap;
  table: SchemaTable;
};

function MiniErdTable({
  annotation,
  baselineTable,
  changeMap,
  table,
}: MiniErdTableProps) {
  const columns = buildMiniErdColumns(table, baselineTable, changeMap);
  const visibleColumns = columns.slice(0, 6);
  const hiddenColumnCount = columns.length - visibleColumns.length;

  return (
    <section
      className={getMiniErdAnnotationClass(
        "schema-workbench__mini-erd-table",
        annotation,
      )}
      aria-label={table.name}
    >
      <header className="schema-workbench__mini-erd-table-header">
        <Icon name="table" size={14} />
        <strong>{table.displayName ?? table.name}</strong>
        {annotation ? (
          <span
            className={getMiniErdAnnotationClass(
              "schema-workbench__mini-erd-change-badge",
              annotation,
            )}
          >
            {annotation.label}
          </span>
        ) : null}
      </header>

      <ol className="schema-workbench__mini-erd-column-list">
        {visibleColumns.map((column) => {
          const badges = getColumnBadgeLabels(column);
          const columnAnnotation = changeMap.columns.get(
            getColumnChangeKey(table.name, column.name),
          );

          return (
            <li
              key={column.name}
              className={getMiniErdAnnotationClass(
                "schema-workbench__mini-erd-column",
                columnAnnotation,
              )}
            >
              <span className="schema-workbench__mini-erd-column-name">
                {column.name}
              </span>
              <span className="schema-workbench__mini-erd-column-type">
                {getMiniErdColumnTypeLabel(column, columnAnnotation)}
              </span>
              {badges.length > 0 || columnAnnotation ? (
                <span className="schema-workbench__mini-erd-column-badges">
                  {columnAnnotation ? (
                    <span
                      className={getMiniErdAnnotationClass(
                        "schema-workbench__mini-erd-change-badge",
                        columnAnnotation,
                      )}
                    >
                      {columnAnnotation.label}
                    </span>
                  ) : null}
                  {badges.map((badge) => (
                    <span
                      key={badge}
                      className="schema-workbench__mini-erd-column-badge"
                    >
                      {badge}
                    </span>
                  ))}
                </span>
              ) : null}
            </li>
          );
        })}
      </ol>

      {hiddenColumnCount > 0 ? (
        <div className="schema-workbench__mini-erd-more">
          +{hiddenColumnCount} more
        </div>
      ) : null}
    </section>
  );
}

type MiniErdRelationProps = {
  annotation?: MiniErdChangeAnnotation;
  relation: SchemaRelation;
};

function MiniErdRelation({ annotation, relation }: MiniErdRelationProps) {
  const from = getRelationEndpoint(relation, "from");
  const to = getRelationEndpoint(relation, "to");

  return (
    <li
      className={getMiniErdAnnotationClass(
        "schema-workbench__mini-erd-relation",
        annotation,
      )}
    >
      <span className="schema-workbench__mini-erd-relation-name">
        {relation.name}
      </span>
      {annotation ? (
        <span
          className={getMiniErdAnnotationClass(
            "schema-workbench__mini-erd-change-badge",
            annotation,
          )}
        >
          {annotation.label}
        </span>
      ) : null}
      <span className="schema-workbench__mini-erd-relation-path">
        <span>{from}</span>
        <Icon name="arrow-right" size={13} />
        <span>{to}</span>
      </span>
      {relation.type ? (
        <span className="schema-workbench__mini-erd-relation-type">
          {relation.type}
        </span>
      ) : null}
    </li>
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
};

function PreviewSummary({
  errorCount,
  warningCount,
  response,
  groups,
}: PreviewSummaryProps) {
  return (
    <div className="schema-workbench__preview-summary">
      <p className="schema-workbench__preview-counts">
        {countLabel(errorCount, "error")} {countLabel(warningCount, "warning")}{" "}
        {countLabel(response.changes.length, "change")}
      </p>

      {response.hasChanges ? (
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
      ) : response.isValid ? (
        <div className="schema-workbench__preview-no-changes">
          <EmptyState
            title="No schema changes"
            description="Preview completed successfully. This draft matches the current schema, so there is nothing to apply."
            icon="check"
            bullets={["Apply stays disabled until the preview contains schema changes"]}
          />
        </div>
      ) : null}
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
      <h4>Example Casts (first 5 rows)</h4>
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
  singularLabel: "error" | "warning";
};

type PreviewDiagnosticsProps = {
  activeDiagnosticId: string | null;
  errorDiagnostics: WorkbenchDiagnostic[];
  onSelect: (diagnostic: WorkbenchDiagnostic) => void;
  warningDiagnostics: WorkbenchDiagnostic[];
};

function PreviewDiagnostics({
  activeDiagnosticId,
  errorDiagnostics,
  onSelect,
  warningDiagnostics,
}: PreviewDiagnosticsProps) {
  if (errorDiagnostics.length === 0 && warningDiagnostics.length === 0) {
    return null;
  }

  return (
    <div className="schema-workbench__preview-diagnostics">
      <DiagnosticsSection
        singularLabel="error"
        diagnostics={errorDiagnostics}
        activeDiagnosticId={activeDiagnosticId}
        onSelect={onSelect}
      />

      <DiagnosticsSection
        singularLabel="warning"
        diagnostics={warningDiagnostics}
        activeDiagnosticId={activeDiagnosticId}
        onSelect={onSelect}
      />
    </div>
  );
}

function DiagnosticsSection({
  activeDiagnosticId,
  diagnostics,
  onSelect,
  singularLabel,
}: DiagnosticsSectionProps) {
  if (diagnostics.length === 0) {
    return null;
  }

  const label = countLabel(diagnostics.length, singularLabel);

  return (
    <section
      className="schema-workbench__diagnostic-section"
      aria-label={label}
    >
      <div className="schema-workbench__diagnostic-heading">
        <h3>{label}</h3>
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
                    : diagnostic.source === "preview"
                      ? "Preview"
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
