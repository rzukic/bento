import {
  useEffect,
  useMemo,
  useRef,
  useState,
  type PointerEvent as ReactPointerEvent,
} from "react";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { EmptyState } from "../../components/ui/EmptyState";
import { Panel } from "../../components/ui/Panel";
import { SelectField } from "../../components/ui/SelectField";
import { Spinner } from "../../components/ui/Spinner";
import { TextField } from "../../components/ui/TextField";
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
  SchemaChangeImpact,
  SchemaColumn,
  SchemaColumnType,
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
import {
  addColumn,
  createStarterTable,
  convertManyToManyRelationToOneToMany,
  convertOneToManyRelationToManyToMany,
  createManyToManyRelation,
  createOneToManyRelation,
  deleteRelationWithArtifacts,
  deleteTableWithDependents,
  duplicateTable,
  getDefaultManyToManyRelationDraft,
  getDefaultOneToManyRelationDraft,
  getTableLayout,
  setTableLayout,
  removeColumn,
  renameRelation,
  renameTable,
  reorderColumn,
  switchManyToManyRelationDirection,
  switchOneToManyRelationDirection,
  updateManyToManyRelation,
  updateColumn,
  updateOneToManyRelation,
  updateRelation,
  updateTable,
  validateVisualSchemaDraft,
  type VisualTableLayout,
  type VisualSchemaDraftIssue,
} from "./visualSchemaDraft";
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
  "uuid",
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
      return {
        title: `Update column details for ${getQualifiedColumnName(change)}`,
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
          message:
            "The backend reported that the draft already matches the current schema, so nothing was persisted.",
        });
        return;
      }

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
        title: applyResult.metadataOnly
          ? `Layout saved at version ${appliedSnapshot.version}`
          : `Applied schema version ${appliedSnapshot.version}`,
        message: applyResult.metadataOnly
          ? "Layout/metadata saved without a database migration."
          : `${countLabel(applyResult.changes.length, "change")} committed and the live schema snapshot was refreshed.`,
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
                  description="Fix the JSON draft before editing visual table and column details."
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

type VisualSchemaModeProps = {
  activeDiagnosticId: string | null;
  backendDiagnostics: WorkbenchDiagnostic[];
  document: SchemaModelDocument | null;
  issues: VisualSchemaDraftIssue[];
  selection: VisualCanvasSelection | null;
  syntaxInvalid: boolean;
  onBackendDiagnosticSelect: (
    diagnostic: WorkbenchDiagnostic,
    selection: VisualCanvasSelection | null,
  ) => void;
  onDocumentChange: (document: SchemaModelDocument) => void;
  onSelectionChange: (selection: VisualCanvasSelection | null) => void;
  onReturnToJson: () => void;
};

type VisualCanvasSelection =
  | {
      type: "table";
      tableName: string;
    }
  | {
      type: "column";
      tableName: string;
      columnName: string;
    }
  | {
      type: "relation";
      relationName: string;
    };

type VisualRelationDraft = {
  columnName: string;
  columnMode: "new" | "existing";
  fromAccessor: string;
  fromColumnName: string;
  fromTableName: string;
  indexName: string;
  junctionTableName: string;
  relationName: string;
  relationType: "1:N" | "N:M";
  toAccessor: string;
  toColumnName: string;
  toTableName: string;
};

type VisualCanvasViewport = {
  x: number;
  y: number;
  zoom: number;
};

type ErdTableNode = {
  height: number;
  table: SchemaTable;
  width: number;
  x: number;
  y: number;
};

type ErdRelationEdge = {
  fromTable: string;
  id: string;
  label: string;
  labelX: number;
  labelY: number;
  path: string;
  relation: SchemaRelation;
  selfReferential: boolean;
  toTable: string;
};

type ErdRelationHandleSide = "left" | "right";

type ErdRelationDragState = {
  fromTableName: string;
  side: ErdRelationHandleSide;
  startX: number;
  startY: number;
  targetTableName: string | null;
  x: number;
  y: number;
};

type ErdCanvasPanState = {
  startClientX: number;
  startClientY: number;
  startViewportX: number;
  startViewportY: number;
};

type ErdTableDragState = {
  originX: number;
  originY: number;
  startClientX: number;
  startClientY: number;
  tableName: string;
};

type VisualDiagnosticTarget = {
  label: string;
  selection: VisualCanvasSelection | null;
};

const erdTableWidth = 288;
const erdColumnRowHeight = 34;
const erdTableHeaderHeight = 88;
const erdTableBottomInset = 10;
const erdTableMinHeight = 144;
const erdGridSize = 48;
const erdSnapSize = erdGridSize / 4;
const erdAutoColumnGap = 344;
const erdAutoRowGap = 292;
const erdCanvasPadding = 56;
const visualColumnTypes: SchemaColumnType[] = [
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
  "uuid",
  "file",
];

const visualColumnTypeOptions = visualColumnTypes.map((type) => ({
  label: type,
  value: type,
}));

function getErdTableHeight(table: SchemaTable): number {
  return Math.max(
    erdTableMinHeight,
    erdTableHeaderHeight +
      Math.min(table.columns.length, 8) * erdColumnRowHeight +
      erdTableBottomInset,
  );
}

function buildErdTableNodes(document: SchemaModelDocument): ErdTableNode[] {
  return document.tables.map((table, index) => {
    const layout = getTableLayout(table);
    const fallbackColumn = index % 3;
    const fallbackRow = Math.floor(index / 3);

    return {
      table,
      width: erdTableWidth,
      height: getErdTableHeight(table),
      x: layout?.x ?? erdCanvasPadding + fallbackColumn * erdAutoColumnGap,
      y: layout?.y ?? erdCanvasPadding + fallbackRow * erdAutoRowGap,
    };
  });
}

function getErdCanvasSize(nodes: ErdTableNode[]): {
  height: number;
  width: number;
} {
  if (nodes.length === 0) {
    return { width: 720, height: 420 };
  }

  return {
    width:
      Math.max(...nodes.map((node) => node.x + node.width)) + erdCanvasPadding,
    height:
      Math.max(...nodes.map((node) => node.y + node.height)) + erdCanvasPadding,
  };
}

function buildErdRelationEdges(
  document: SchemaModelDocument,
  nodes: ErdTableNode[],
): ErdRelationEdge[] {
  const nodeByName = new Map(nodes.map((node) => [node.table.name, node]));

  return document.relations.flatMap<ErdRelationEdge>((relation) => {
    const fromTable = getRelationEndpoint(relation, "from");
    const toTable = getRelationEndpoint(relation, "to");
    const fromNode = nodeByName.get(fromTable);
    const toNode = nodeByName.get(toTable);

    if (!fromNode || !toNode) {
      return [];
    }

    const label = relation.type ?? "relation";

    if (relation.type === "N:M") {
      const junctionTable = getRelationJunctionTableName(relation);
      const junctionNode = junctionTable ? nodeByName.get(junctionTable) : null;

      if (junctionTable && junctionNode && junctionTable !== fromTable && junctionTable !== toTable) {
        return [
          buildErdRelationEdge(
            relation,
            fromTable,
            junctionTable,
            fromNode,
            junctionNode,
            label,
            nodes,
          ),
          buildErdRelationEdge(
            relation,
            junctionTable,
            toTable,
            junctionNode,
            toNode,
            label,
            nodes,
          ),
        ];
      }
    }

    if (fromTable === toTable) {
      return [buildSelfReferentialErdRelationEdge(relation, fromTable, fromNode, label, nodes)];
    }

    return [
      buildErdRelationEdge(relation, fromTable, toTable, fromNode, toNode, label, nodes),
    ];
  });
}

function getRelationJunctionTableName(relation: SchemaRelation): string | null {
  const explicitName = relation.junctionTable?.trim();
  if (explicitName) {
    return explicitName;
  }

  return relation.resolvedJunctionTableName?.trim() || null;
}

function snapToErdGrid(value: number): number {
  return Math.round(value / erdSnapSize) * erdSnapSize;
}

function getSuggestedJunctionLayout(
  document: SchemaModelDocument,
  fromTableName: string,
  toTableName: string,
): VisualTableLayout {
  const fromTable = document.tables.find((table) => table.name === fromTableName);
  const toTable = document.tables.find((table) => table.name === toTableName);
  const fromLayout = fromTable ? getTableLayout(fromTable) : null;
  const toLayout = toTable ? getTableLayout(toTable) : null;
  const fallbackLayout = getNextVisualTableLayout(document);

  return {
    x:
      typeof fromLayout?.x === "number" && typeof toLayout?.x === "number"
        ? snapToErdGrid((fromLayout.x + toLayout.x) / 2 + 40)
        : fallbackLayout.x,
    y:
      typeof fromLayout?.y === "number" && typeof toLayout?.y === "number"
        ? snapToErdGrid((fromLayout.y + toLayout.y) / 2 + 120)
        : fallbackLayout.y,
  };
}

const erdRelationChannelMargin = 18;
const erdRelationObstaclePadding = 6;

type ErdRouteSide = "left" | "right";
type ErdRoutePoint = { x: number; y: number };

function getErdRelationAnchorY(node: ErdTableNode): number {
  return node.y + Math.min(node.height - 42, 96);
}

function buildErdRelationEdge(
  relation: SchemaRelation,
  fromTable: string,
  toTable: string,
  fromNode: ErdTableNode,
  toNode: ErdTableNode,
  label: string,
  allNodes: ErdTableNode[],
): ErdRelationEdge {
  const fromCenterX = fromNode.x + fromNode.width / 2;
  const toCenterX = toNode.x + toNode.width / 2;
  const exitsRight = fromCenterX <= toCenterX;
  const startSide: ErdRouteSide = exitsRight ? "right" : "left";
  const endSide: ErdRouteSide = exitsRight ? "left" : "right";
  const startX = startSide === "right" ? fromNode.x + fromNode.width : fromNode.x;
  const endX = endSide === "right" ? toNode.x + toNode.width : toNode.x;
  const startY = getErdRelationAnchorY(fromNode);
  const endY = getErdRelationAnchorY(toNode);
  const obstacles = allNodes.filter(
    (node) => node !== fromNode && node !== toNode,
  );

  const route = routeOrthogonalRelationPath(
    { x: startX, y: startY },
    { x: endX, y: endY },
    startSide,
    endSide,
    obstacles,
    [fromNode, toNode],
  );

  return {
    relation,
    fromTable,
    id: `${relation.name}:${fromTable}:${toTable}`,
    toTable,
    label,
    selfReferential: false,
    path: route.path,
    labelX: route.labelPoint.x,
    labelY: route.labelPoint.y,
  };
}

function buildSelfReferentialErdRelationEdge(
  relation: SchemaRelation,
  tableName: string,
  node: ErdTableNode,
  label: string,
  allNodes: ErdTableNode[],
): ErdRelationEdge {
  const startX = node.x + node.width;
  const startY = node.y + Math.min(node.height - 36, 56);
  const endY = node.y + Math.min(node.height - 36, 150);
  const obstacles = allNodes.filter((candidate) => candidate !== node);
  const detourX = clearVerticalChannel(
    startX + erdRelationChannelMargin * 4,
    startY,
    endY,
    obstacles,
    erdRelationChannelMargin,
    "right",
  );

  const path = pointsToOrthogonalSvgPath([
    { x: startX, y: startY },
    { x: detourX, y: startY },
    { x: detourX, y: endY },
    { x: startX, y: endY },
  ]);

  return {
    relation,
    fromTable: tableName,
    id: `${relation.name}:${tableName}:${tableName}:self`,
    toTable: tableName,
    label,
    selfReferential: true,
    path,
    labelX: detourX,
    labelY: (startY + endY) / 2,
  };
}

function routeOrthogonalRelationPath(
  start: ErdRoutePoint,
  end: ErdRoutePoint,
  startSide: ErdRouteSide,
  endSide: ErdRouteSide,
  obstacles: ErdTableNode[],
  endpoints: ErdTableNode[],
): { path: string; labelPoint: ErdRoutePoint } {
  const margin = erdRelationChannelMargin;
  const exitX = startSide === "right" ? start.x + margin : start.x - margin;
  const entryX = endSide === "right" ? end.x + margin : end.x - margin;

  let midX: number;
  let preferDirection: ErdRouteSide | null = null;
  if (startSide === endSide) {
    midX =
      startSide === "right"
        ? Math.max(exitX, entryX) + margin
        : Math.min(exitX, entryX) - margin;
    preferDirection = startSide;
  } else {
    midX = (exitX + entryX) / 2;
  }

  midX = clampMidXOutsideEndpoints(
    midX,
    start.y,
    end.y,
    endpoints,
    margin,
    preferDirection,
  );
  midX = clearVerticalChannel(
    midX,
    start.y,
    end.y,
    obstacles,
    margin,
    preferDirection,
  );
  midX = clampMidXOutsideEndpoints(
    midX,
    start.y,
    end.y,
    endpoints,
    margin,
    preferDirection,
  );

  let waypoints: ErdRoutePoint[] = [
    start,
    { x: exitX, y: start.y },
    { x: midX, y: start.y },
    { x: midX, y: end.y },
    { x: entryX, y: end.y },
    end,
  ];

  waypoints = detourHorizontalSegments(waypoints, obstacles, margin);

  return {
    path: pointsToOrthogonalSvgPath(waypoints),
    labelPoint: { x: midX, y: (start.y + end.y) / 2 },
  };
}

function clampMidXOutsideEndpoints(
  midX: number,
  y1: number,
  y2: number,
  endpoints: ErdTableNode[],
  margin: number,
  preferDirection: ErdRouteSide | null,
): number {
  const yMin = Math.min(y1, y2);
  const yMax = Math.max(y1, y2);

  for (let i = 0; i < 4; i += 1) {
    const blocking = endpoints.find((node) => {
      const top = node.y;
      const bottom = node.y + node.height;
      const left = node.x;
      const right = node.x + node.width;
      const verticalOverlap = yMin < bottom && yMax > top;
      return verticalOverlap && midX > left && midX < right;
    });

    if (!blocking) {
      return midX;
    }

    const moveRight = blocking.x + blocking.width + margin;
    const moveLeft = blocking.x - margin;

    if (preferDirection === "right") {
      midX = moveRight;
    } else if (preferDirection === "left") {
      midX = moveLeft;
    } else {
      const distRight = Math.abs(moveRight - midX);
      const distLeft = Math.abs(midX - moveLeft);
      midX = distRight <= distLeft ? moveRight : moveLeft;
    }
  }

  return midX;
}

function clearVerticalChannel(
  initialX: number,
  y1: number,
  y2: number,
  obstacles: ErdTableNode[],
  margin: number,
  preferDirection: ErdRouteSide | null,
): number {
  let x = initialX;
  const yMin = Math.min(y1, y2);
  const yMax = Math.max(y1, y2);

  for (let i = 0; i < 8; i += 1) {
    const blocking = obstacles.find((obs) => {
      const left = obs.x - erdRelationObstaclePadding;
      const right = obs.x + obs.width + erdRelationObstaclePadding;
      const top = obs.y - erdRelationObstaclePadding;
      const bottom = obs.y + obs.height + erdRelationObstaclePadding;
      return (
        x >= left && x <= right && yMin <= bottom && yMax >= top
      );
    });

    if (!blocking) {
      return x;
    }

    const moveRight = blocking.x + blocking.width + margin;
    const moveLeft = blocking.x - margin;

    if (preferDirection === "right") {
      x = moveRight;
    } else if (preferDirection === "left") {
      x = moveLeft;
    } else {
      const distRight = Math.abs(moveRight - x);
      const distLeft = Math.abs(x - moveLeft);
      x = distRight <= distLeft ? moveRight : moveLeft;
    }
  }

  return x;
}

function detourHorizontalSegments(
  points: ErdRoutePoint[],
  obstacles: ErdTableNode[],
  margin: number,
): ErdRoutePoint[] {
  const result: ErdRoutePoint[] = [];

  for (let i = 0; i < points.length; i += 1) {
    const current = points[i];
    result.push(current);
    const next = points[i + 1];
    if (!next) {
      continue;
    }

    if (current.y !== next.y) {
      continue;
    }

    const segmentY = current.y;
    const segmentMinX = Math.min(current.x, next.x);
    const segmentMaxX = Math.max(current.x, next.x);

    const blocking = obstacles.find((obs) => {
      const top = obs.y - erdRelationObstaclePadding;
      const bottom = obs.y + obs.height + erdRelationObstaclePadding;
      const left = obs.x - erdRelationObstaclePadding;
      const right = obs.x + obs.width + erdRelationObstaclePadding;
      return (
        segmentY >= top &&
        segmentY <= bottom &&
        segmentMaxX >= left &&
        segmentMinX <= right
      );
    });

    if (!blocking) {
      continue;
    }

    const detourAbove = blocking.y - margin;
    const detourBelow = blocking.y + blocking.height + margin;
    const detourY =
      Math.abs(segmentY - detourAbove) <= Math.abs(segmentY - detourBelow)
        ? detourAbove
        : detourBelow;

    const detourEntryX = current.x;
    const detourExitX = next.x;
    result.push({ x: detourEntryX, y: detourY });
    result.push({ x: detourExitX, y: detourY });
  }

  return collapseCollinearPoints(result);
}

function collapseCollinearPoints(points: ErdRoutePoint[]): ErdRoutePoint[] {
  const result: ErdRoutePoint[] = [];
  for (const point of points) {
    const last = result[result.length - 1];
    if (last && last.x === point.x && last.y === point.y) {
      continue;
    }
    result.push(point);
  }
  return result;
}

function pointsToOrthogonalSvgPath(points: ErdRoutePoint[]): string {
  const collapsed = collapseCollinearPoints(points);
  if (collapsed.length === 0) {
    return "";
  }
  const [first, ...rest] = collapsed;
  return [`M ${first.x} ${first.y}`, ...rest.map((p) => `L ${p.x} ${p.y}`)].join(
    " ",
  );
}

function getErdRelationHandlePoint(
  node: ErdTableNode,
  side: ErdRelationHandleSide,
) {
  return {
    x: side === "right" ? node.x + node.width : node.x,
    y: node.y + node.height / 2,
  };
}

function getErdDragPath(drag: ErdRelationDragState): string {
  const direction = drag.side === "right" ? 1 : -1;
  const exitX = drag.startX + erdRelationChannelMargin * direction;
  const midX =
    Math.abs(drag.x - exitX) < erdRelationChannelMargin
      ? exitX
      : (exitX + drag.x) / 2;

  return pointsToOrthogonalSvgPath([
    { x: drag.startX, y: drag.startY },
    { x: exitX, y: drag.startY },
    { x: midX, y: drag.startY },
    { x: midX, y: drag.y },
    { x: drag.x, y: drag.y },
  ]);
}

function getErdDropTargetTableName(
  nodes: ErdTableNode[],
  fromTableName: string,
  point: { x: number; y: number },
): string | null {
  const target = nodes.find(
    (node) =>
      node.table.name !== fromTableName &&
      point.x >= node.x &&
      point.x <= node.x + node.width &&
      point.y >= node.y &&
      point.y <= node.y + node.height,
  );

  return target?.table.name ?? null;
}

function getNextVisualTableLayout(document: SchemaModelDocument) {
  const index = document.tables.length;
  return {
    x: snapToErdGrid(80 + (index % 3) * 320),
    y: snapToErdGrid(80 + Math.floor(index / 3) * 240),
  };
}

function getTableDeleteImpact(
  document: SchemaModelDocument,
  tableName: string,
) {
  const relationCount = document.relations.filter(
    (relation) => relation.from === tableName || relation.to === tableName,
  ).length;
  const referenceColumnCount = document.tables.reduce((count, table) => {
    if (table.name === tableName) {
      return count;
    }

    return (
      count +
      table.columns.filter(
        (column) =>
          column.type === "reference" && column.references === tableName,
      ).length
    );
  }, 0);

  return { referenceColumnCount, relationCount };
}

function createVisualRelationDraft(
  document: SchemaModelDocument,
  fromTableName: string,
  toTableName = document.tables[0]?.name ?? fromTableName,
): VisualRelationDraft {
  const defaults = getDefaultOneToManyRelationDraft(
    document,
    fromTableName,
    toTableName,
  );
  const manyToManyDefaults = getDefaultManyToManyRelationDraft(
    document,
    fromTableName,
    toTableName,
  );

  return {
    columnMode: "new",
    relationType: "1:N",
    fromTableName,
    toTableName,
    fromColumnName: manyToManyDefaults.fromColumnName,
    junctionTableName: manyToManyDefaults.junctionTableName,
    toColumnName: manyToManyDefaults.toColumnName,
    ...defaults,
  };
}

function getVisualDiagnosticTarget(
  document: SchemaModelDocument,
  path: string | null,
): VisualDiagnosticTarget {
  if (!path) {
    return { label: "Canvas", selection: null };
  }

  const tableMatch = /^tables\[(\d+)\](?:\.columns\[(\d+)\])?(?:\.indexes\[(\d+)\])?/.exec(
    path,
  );
  if (tableMatch) {
    const table = document.tables[Number(tableMatch[1])];
    if (!table) {
      return { label: "Canvas", selection: null };
    }

    if (tableMatch[2] !== undefined) {
      const column = table.columns[Number(tableMatch[2])];
      if (column) {
        return {
          label: `${table.name}.${column.name}`,
          selection: {
            type: "column",
            tableName: table.name,
            columnName: column.name,
          },
        };
      }
    }

    return {
      label: table.name,
      selection: { type: "table", tableName: table.name },
    };
  }

  const relationMatch = /^relations\[(\d+)\]/.exec(path);
  if (relationMatch) {
    const relation = document.relations[Number(relationMatch[1])];
    if (relation) {
      return {
        label: relation.name,
        selection: {
          type: "relation",
          relationName: relation.name,
        },
      };
    }
  }

  return { label: "Canvas", selection: null };
}

type VisualPreviewDiagnosticsProps = {
  activeDiagnosticId: string | null;
  diagnostics: WorkbenchDiagnostic[];
  document: SchemaModelDocument;
  onSelect: (
    diagnostic: WorkbenchDiagnostic,
    selection: VisualCanvasSelection | null,
  ) => void;
};

function VisualPreviewDiagnostics({
  activeDiagnosticId,
  diagnostics,
  document,
  onSelect,
}: VisualPreviewDiagnosticsProps) {
  return (
    <section
      className="schema-workbench__visual-diagnostics"
      aria-label="Visual preview diagnostics"
    >
      <h3>Preview diagnostics</h3>
      <div className="schema-workbench__diagnostic-list">
        {diagnostics.slice(0, 5).map((diagnostic) => {
          const target = getVisualDiagnosticTarget(document, diagnostic.path);
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
              onClick={() => onSelect(diagnostic, target.selection)}
            >
              <div className="schema-workbench__diagnostic-meta">
                <span className="schema-workbench__diagnostic-severity">
                  {diagnostic.severity === "warning" ? "Warning" : "Error"}
                </span>
                <code className="schema-workbench__diagnostic-code">
                  {diagnostic.code}
                </code>
                <span className="schema-workbench__diagnostic-location">
                  {target.label}
                </span>
              </div>
              <p className="schema-workbench__diagnostic-message">
                {diagnostic.message}
              </p>
              {diagnostic.path ? (
                <code className="schema-workbench__diagnostic-path">
                  {diagnostic.path}
                </code>
              ) : null}
            </button>
          );
        })}
      </div>
      {diagnostics.length > 5 ? (
        <p>
          {countLabel(diagnostics.length - 5, "additional diagnostic")} hidden.
        </p>
      ) : null}
    </section>
  );
}

function getReusableRelationColumnOptions(
  document: SchemaModelDocument,
  fromTableName: string,
  toTableName: string,
) {
  const toTable = document.tables.find((table) => table.name === toTableName);
  return (
    toTable?.columns.filter(
      (column) =>
        column.type === "reference" && column.references === fromTableName,
    ) ?? []
  );
}

function VisualSchemaMode({
  activeDiagnosticId,
  backendDiagnostics,
  document,
  issues,
  selection,
  syntaxInvalid,
  onBackendDiagnosticSelect,
  onDocumentChange,
  onSelectionChange,
  onReturnToJson,
}: VisualSchemaModeProps) {
  const [viewport, setViewport] = useState<VisualCanvasViewport>({
    x: 0,
    y: 0,
    zoom: 1,
  });
  const [relationDrag, setRelationDrag] = useState<ErdRelationDragState | null>(
    null,
  );
  const canvasRef = useRef<HTMLDivElement | null>(null);
  const relationDragRef = useRef<ErdRelationDragState | null>(null);
  const canvasPanRef = useRef<ErdCanvasPanState | null>(null);
  const tableDragRef = useRef<ErdTableDragState | null>(null);

  if (syntaxInvalid) {
    return (
      <div className="schema-workbench__visual-shell">
        <EmptyState
          title="Fix JSON syntax to use Visual mode"
          description="Visual mode reads the same draft as the JSON editor. The current draft stays untouched until the JSON parses again."
          icon="schema"
          bullets={[
            "Return to JSON to fix the syntax",
            "Preview and apply remain disabled for invalid drafts",
          ]}
        />
        <div className="schema-workbench__visual-actions">
          <Button variant="primary" onClick={onReturnToJson}>
            Return to JSON
          </Button>
        </div>
      </div>
    );
  }

  if (!document) {
    return (
      <div className="schema-workbench__visual-shell">
        <EmptyState
          title="Draft cannot be shown visually"
          description="The JSON parses, but it does not expose the tables and relations arrays the visual designer needs."
          icon="schema"
          bullets={["Add a tables array", "Add a relations array"]}
        />
        <div className="schema-workbench__visual-actions">
          <Button variant="primary" onClick={onReturnToJson}>
            Return to JSON
          </Button>
        </div>
      </div>
    );
  }

  const visualDocument = document;
  const nodes = buildErdTableNodes(visualDocument);
  const canvasSize = getErdCanvasSize(nodes);
  const edges = buildErdRelationEdges(visualDocument, nodes);
  const zoomPercent = Math.round(viewport.zoom * 100);

  function addNewTable() {
    const { document: nextDocument, tableName } = createStarterTable(
      visualDocument,
      getNextVisualTableLayout(visualDocument),
    );
    onDocumentChange(nextDocument);
    onSelectionChange({ type: "table", tableName });
  }

  function getCanvasPoint(clientX: number, clientY: number) {
    const canvas = canvasRef.current;
    if (!canvas) {
      return null;
    }

    const rect = canvas.getBoundingClientRect();
    return {
      x: (clientX - rect.left) / viewport.zoom,
      y: (clientY - rect.top) / viewport.zoom,
    };
  }

  function commitRelationDrag(fromTableName: string, toTableName: string) {
    const draft = createVisualRelationDraft(
      visualDocument,
      fromTableName,
      toTableName,
    );
    const result = createOneToManyRelation(visualDocument, {
      fromTableName: draft.fromTableName,
      toTableName: draft.toTableName,
      relationName: draft.relationName,
      fromAccessor: draft.fromAccessor,
      toAccessor: draft.toAccessor,
      indexName: draft.indexName,
      columnName: draft.columnName,
      reuseColumnName: null,
    });

    if (result.issues.length > 0 || !result.relationName) {
      onSelectionChange({ type: "table", tableName: fromTableName });
      return;
    }

    onDocumentChange(result.document);
    onSelectionChange({ type: "relation", relationName: result.relationName });
  }

  function startRelationDrag(
    event: ReactPointerEvent<HTMLButtonElement>,
    node: ErdTableNode,
    side: ErdRelationHandleSide,
  ) {
    event.preventDefault();
    event.stopPropagation();
    event.currentTarget.setPointerCapture?.(event.pointerId);

    const point = getCanvasPoint(event.clientX, event.clientY);
    const handlePoint = getErdRelationHandlePoint(node, side);

    const nextDrag = {
      fromTableName: node.table.name,
      side,
      startX: handlePoint.x,
      startY: handlePoint.y,
      targetTableName: point
        ? getErdDropTargetTableName(nodes, node.table.name, point)
        : null,
      x: point?.x ?? handlePoint.x,
      y: point?.y ?? handlePoint.y,
    };
    relationDragRef.current = nextDrag;
    setRelationDrag(nextDrag);
    onSelectionChange({ type: "table", tableName: node.table.name });

    function handlePointerMove(event: PointerEvent) {
      const nextPoint = getCanvasPoint(event.clientX, event.clientY);
      if (!nextPoint) {
        return;
      }

      setRelationDrag((current) => {
        if (!current) {
          relationDragRef.current = null;
          return current;
        }

        const nextDrag = {
          ...current,
          x: nextPoint.x,
          y: nextPoint.y,
          targetTableName: getErdDropTargetTableName(
            nodes,
            current.fromTableName,
            nextPoint,
          ),
        };
        relationDragRef.current = nextDrag;
        return nextDrag;
      });
    }

    function handlePointerUp(event: PointerEvent) {
      window.removeEventListener("pointermove", handlePointerMove);
      window.removeEventListener("pointerup", handlePointerUp);

      const nextPoint = getCanvasPoint(event.clientX, event.clientY);
      const currentDrag = relationDragRef.current;
      const dropTargetTableName =
        (nextPoint
          ? getErdDropTargetTableName(nodes, node.table.name, nextPoint)
          : null) ?? currentDrag?.targetTableName;
      relationDragRef.current = null;
      setRelationDrag(null);

      if (dropTargetTableName) {
        commitRelationDrag(node.table.name, dropTargetTableName);
      }
    }

    window.addEventListener("pointermove", handlePointerMove);
    window.addEventListener("pointerup", handlePointerUp);
  }

  function startCanvasPan(event: ReactPointerEvent<HTMLDivElement>) {
    if (event.button !== 0 || relationDragRef.current || tableDragRef.current) {
      return;
    }

    const target = event.target;
    if (
      target instanceof Element &&
      target.closest(
        ".schema-workbench__erd-table, .schema-workbench__erd-relation-button, .schema-workbench__erd-relation-handle, .schema-workbench__erd-relation-hit",
      )
    ) {
      return;
    }

    event.preventDefault();
    event.currentTarget.setPointerCapture?.(event.pointerId);

    const nextPan = {
      startClientX: event.clientX,
      startClientY: event.clientY,
      startViewportX: viewport.x,
      startViewportY: viewport.y,
    };
    canvasPanRef.current = nextPan;

    function handlePointerMove(moveEvent: PointerEvent) {
      const currentPan = canvasPanRef.current;
      if (!currentPan) {
        return;
      }

      setViewport((current) => ({
        ...current,
        x:
          currentPan.startViewportX +
          (moveEvent.clientX - currentPan.startClientX),
        y:
          currentPan.startViewportY +
          (moveEvent.clientY - currentPan.startClientY),
      }));
    }

    function handlePointerUp(upEvent: PointerEvent) {
      const currentPan = canvasPanRef.current;
      if (!currentPan) {
        return;
      }

      canvasPanRef.current = null;
      window.removeEventListener("pointermove", handlePointerMove);
      window.removeEventListener("pointerup", handlePointerUp);
    }

    window.addEventListener("pointermove", handlePointerMove);
    window.addEventListener("pointerup", handlePointerUp);
  }

  function startTableDrag(
    event: ReactPointerEvent<HTMLButtonElement>,
    table: SchemaTable,
  ) {
    if (event.button !== 0 || relationDragRef.current || canvasPanRef.current) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();
    event.currentTarget.setPointerCapture?.(event.pointerId);
    onSelectionChange({ type: "table", tableName: table.name });

    const layout = getTableLayout(table);
    const fallbackLayout = nodes.find((node) => node.table.name === table.name);
    if (!fallbackLayout) {
      return;
    }

    tableDragRef.current = {
      tableName: table.name,
      startClientX: event.clientX,
      startClientY: event.clientY,
      originX: layout?.x ?? fallbackLayout.x,
      originY: layout?.y ?? fallbackLayout.y,
    };

    function handlePointerMove(moveEvent: PointerEvent) {
      const currentDrag = tableDragRef.current;
      if (!currentDrag) {
        return;
      }

      const deltaX = (moveEvent.clientX - currentDrag.startClientX) / viewport.zoom;
      const deltaY = (moveEvent.clientY - currentDrag.startClientY) / viewport.zoom;

      onDocumentChange(
        setTableLayout(visualDocument, currentDrag.tableName, {
          x: snapToErdGrid(currentDrag.originX + deltaX),
          y: snapToErdGrid(currentDrag.originY + deltaY),
        }),
      );
    }

    function handlePointerUp(upEvent: PointerEvent) {
      const currentDrag = tableDragRef.current;
      if (!currentDrag) {
        return;
      }

      tableDragRef.current = null;
      window.removeEventListener("pointermove", handlePointerMove);
      window.removeEventListener("pointerup", handlePointerUp);
    }

    window.addEventListener("pointermove", handlePointerMove);
    window.addEventListener("pointerup", handlePointerUp);
  }

  function updateViewport(update: Partial<VisualCanvasViewport>) {
    setViewport((current) => ({
      ...current,
      ...update,
      zoom: Math.max(0.5, Math.min(1.6, update.zoom ?? current.zoom)),
    }));
  }

  function panBy(deltaX: number, deltaY: number) {
    setViewport((current) => ({
      ...current,
      x: current.x + deltaX,
      y: current.y + deltaY,
    }));
  }

  function resetCanvas() {
    setViewport({ x: 0, y: 0, zoom: 1 });
    relationDragRef.current = null;
    setRelationDrag(null);
    onSelectionChange(null);
  }

  function fitCanvas() {
    setViewport({
      x: 12,
      y: 12,
      zoom: visualDocument.tables.length > 3 ? 0.72 : 0.86,
    });
  }

  return (
    <div
      className="schema-workbench__visual-shell"
      data-testid="schema-visual-mode"
      aria-label="Visual schema draft"
    >
      {issues.length > 0 ? (
        <section
          className="schema-workbench__visual-issues"
          aria-label="Visual draft checks"
        >
          <h3>Visual draft checks</h3>
          <ol>
            {issues.slice(0, 5).map((issue) => (
              <li key={`${issue.code}:${issue.path}`}>
                <strong>{issue.code}</strong>
                <span>{issue.message}</span>
                <code>{issue.path}</code>
              </li>
            ))}
          </ol>
          {issues.length > 5 ? (
            <p>{countLabel(issues.length - 5, "additional issue")} hidden.</p>
          ) : null}
        </section>
      ) : null}

      {backendDiagnostics.length > 0 ? (
        <VisualPreviewDiagnostics
          activeDiagnosticId={activeDiagnosticId}
          diagnostics={backendDiagnostics}
          document={visualDocument}
          onSelect={(diagnostic, targetSelection) => {
            onBackendDiagnosticSelect(diagnostic, targetSelection);
          }}
        />
      ) : null}

      <div className="schema-workbench__erd-shell">
        <div
          className="schema-workbench__erd-toolbar"
          role="toolbar"
          aria-label="ERD canvas controls"
        >
          <Button
            size="icon"
            aria-label="Pan left"
            title="Pan left"
            disabled={visualDocument.tables.length === 0}
            onClick={() => panBy(72, 0)}
          >
            <Icon name="chevron-left" size={15} />
          </Button>
          <Button
            size="icon"
            aria-label="Pan right"
            title="Pan right"
            disabled={visualDocument.tables.length === 0}
            onClick={() => panBy(-72, 0)}
          >
            <Icon name="chevron-right" size={15} />
          </Button>
          <Button
            size="icon"
            aria-label="Pan up"
            title="Pan up"
            disabled={visualDocument.tables.length === 0}
            onClick={() => panBy(0, 72)}
          >
            <Icon name="chevron-up" size={15} />
          </Button>
          <Button
            size="icon"
            aria-label="Pan down"
            title="Pan down"
            disabled={visualDocument.tables.length === 0}
            onClick={() => panBy(0, -72)}
          >
            <Icon name="chevron-down" size={15} />
          </Button>
          <Button
            size="icon"
            aria-label="Zoom out"
            title="Zoom out"
            disabled={visualDocument.tables.length === 0}
            onClick={() => updateViewport({ zoom: viewport.zoom - 0.1 })}
          >
            <Icon name="minus" size={15} />
          </Button>
          <span
            className="schema-workbench__erd-zoom"
            aria-label={`Canvas zoom ${zoomPercent}%`}
          >
            {zoomPercent}%
          </span>
          <Button
            size="icon"
            aria-label="Zoom in"
            title="Zoom in"
            disabled={visualDocument.tables.length === 0}
            onClick={() => updateViewport({ zoom: viewport.zoom + 0.1 })}
          >
            <Icon name="plus" size={15} />
          </Button>
          <Button
            size="icon"
            aria-label="Fit canvas"
            title="Fit canvas"
            disabled={visualDocument.tables.length === 0}
            onClick={fitCanvas}
          >
            <Icon name="expand" size={15} />
          </Button>
          <Button
            size="icon"
            aria-label="Reset canvas"
            title="Reset canvas"
            disabled={visualDocument.tables.length === 0}
            onClick={resetCanvas}
          >
            <Icon name="refresh" size={15} />
          </Button>
          <Button
            size="sm"
            variant="primary"
            className="schema-workbench__erd-add-table"
            leadingIcon={<Icon name="plus" size={14} />}
            onClick={addNewTable}
          >
            Add table
          </Button>
        </div>

        <div
          className="schema-workbench__erd-viewport"
          data-testid="schema-erd-canvas"
        >
          {visualDocument.tables.length === 0 ? (
            <EmptyState
              title="No tables in this draft"
              description="The ERD canvas will render here once the shared Model JSON draft includes at least one table."
              icon="schema"
              bullets={[
                "JSON mode remains available",
                "Import, preview, and apply still use the shared draft",
              ]}
            />
          ) : (
            <div
              ref={canvasRef}
              className="schema-workbench__erd-canvas"
              data-testid="schema-erd-canvas-content"
              onPointerDown={startCanvasPan}
              style={{
                width: `${canvasSize.width}px`,
                height: `${canvasSize.height}px`,
                transform: `translate(${viewport.x}px, ${viewport.y}px) scale(${viewport.zoom})`,
              }}
            >
              <svg
                className="schema-workbench__erd-relations-layer"
                width={canvasSize.width}
                height={canvasSize.height}
              >
                {edges.map((edge) => {
                  const isSelected =
                    selection?.type === "relation" &&
                    selection.relationName === edge.relation.name;
                  return (
                    <g key={edge.id}>
                      <path
                        className="schema-workbench__erd-relation-hit"
                        data-testid={`schema-relation-line-${edge.relation.name}`}
                        d={edge.path}
                        onClick={(event) => {
                          event.stopPropagation();
                          onSelectionChange({
                            type: "relation",
                            relationName: edge.relation.name,
                          });
                        }}
                      />
                      <path
                        className={
                          isSelected
                            ? "schema-workbench__erd-relation-path schema-workbench__erd-relation-path--selected"
                            : "schema-workbench__erd-relation-path"
                        }
                        d={edge.path}
                        markerEnd="url(#schema-erd-arrow)"
                      />
                    </g>
                  );
                })}
                {relationDrag ? (
                  <path
                    className={
                      relationDrag.targetTableName
                        ? "schema-workbench__erd-relation-path schema-workbench__erd-relation-path--draft schema-workbench__erd-relation-path--droppable"
                        : "schema-workbench__erd-relation-path schema-workbench__erd-relation-path--draft"
                    }
                    d={getErdDragPath(relationDrag)}
                    markerEnd="url(#schema-erd-arrow)"
                  />
                ) : null}
                <defs>
                  <marker
                    id="schema-erd-arrow"
                    markerWidth="10"
                    markerHeight="10"
                    refX="8"
                    refY="3"
                    orient="auto"
                    markerUnits="strokeWidth"
                  >
                    <path d="M0,0 L0,6 L9,3 z" />
                  </marker>
                </defs>
              </svg>

              {edges.map((edge) => (
                <button
                  key={edge.id}
                  type="button"
                  className={
                    selection?.type === "relation" &&
                    selection.relationName === edge.relation.name
                      ? "schema-workbench__erd-relation-button schema-workbench__erd-relation-button--selected"
                      : "schema-workbench__erd-relation-button"
                  }
                  style={{
                    left: `${edge.labelX}px`,
                    top: `${edge.labelY}px`,
                  }}
                  aria-label={`${edge.relation.name} ${edge.label} relation from ${edge.fromTable} to ${edge.toTable}`}
                  onClick={() =>
                    onSelectionChange({
                      type: "relation",
                      relationName: edge.relation.name,
                    })
                  }
                >
                  <span>{edge.label}</span>
                  {edge.selfReferential ? <small>self</small> : null}
                </button>
              ))}

              {nodes.map((node) => {
                const table = node.table;
                const layout = getTableLayout(table);
                const selected =
                  selection?.type === "table" &&
                  selection.tableName === table.name;
                const dropTarget = relationDrag?.targetTableName === table.name;

                return (
                  <article
                    key={table.name}
                    className={[
                      "schema-workbench__erd-table",
                      selected ? "schema-workbench__erd-table--selected" : "",
                      dropTarget
                        ? "schema-workbench__erd-table--relation-target"
                        : "",
                    ]
                      .filter(Boolean)
                      .join(" ")}
                    style={{
                      left: `${node.x}px`,
                      top: `${node.y}px`,
                      width: `${node.width}px`,
                      minHeight: `${node.height}px`,
                      borderColor: layout?.color ?? undefined,
                    }}
                  >
                    {selected ? (
                      <>
                        <button
                          type="button"
                          className="schema-workbench__erd-relation-handle schema-workbench__erd-relation-handle--left"
                          aria-label="Start relation from left side"
                          title="Drag to another table to create a relation"
                          onPointerDown={(event) =>
                            startRelationDrag(event, node, "left")
                          }
                        />
                        <button
                          type="button"
                          className="schema-workbench__erd-relation-handle schema-workbench__erd-relation-handle--right"
                          aria-label="Start relation from right side"
                          title="Drag to another table to create a relation"
                          onPointerDown={(event) =>
                            startRelationDrag(event, node, "right")
                          }
                        />
                      </>
                    ) : null}
                    <button
                      type="button"
                      className="schema-workbench__erd-table-header"
                      onPointerDown={(event) => startTableDrag(event, table)}
                      onClick={() =>
                        onSelectionChange({
                          type: "table",
                          tableName: table.name,
                        })
                      }
                    >
                      <span className="schema-workbench__erd-table-title">
                        {table.displayName || table.name}
                      </span>
                      <span className="schema-workbench__erd-table-name">
                        {table.name}
                      </span>
                    </button>
                    <ol className="schema-workbench__erd-column-list">
                      {table.columns.slice(0, 8).map((column) => {
                        const columnSelected =
                          selection?.type === "column" &&
                          selection.tableName === table.name &&
                          selection.columnName === column.name;

                        return (
                          <li key={column.name}>
                            <button
                              type="button"
                              className={
                                columnSelected
                                  ? "schema-workbench__erd-column schema-workbench__erd-column--selected"
                                  : "schema-workbench__erd-column"
                              }
                              onClick={() =>
                                onSelectionChange({
                                  type: "column",
                                  tableName: table.name,
                                  columnName: column.name,
                                })
                              }
                            >
                              <span className="schema-workbench__erd-column-main">
                                <span
                                  className={
                                    column.primaryKey
                                      ? "schema-workbench__erd-column-name schema-workbench__erd-column-name--primary-key"
                                      : "schema-workbench__erd-column-name"
                                  }
                                >
                                  {column.name}
                                </span>
                                <span className="schema-workbench__erd-column-badges">
                                  {column.primaryKey ? <span>PK</span> : null}
                                  {column.type === "reference" ? (
                                    <span>FK</span>
                                  ) : null}
                                  {column.unique ? <span>UQ</span> : null}
                                </span>
                              </span>
                              <code>{formatMiniErdTypeValue(column.type)}</code>
                            </button>
                          </li>
                        );
                      })}
                    </ol>
                    {table.columns.length > 8 ? (
                      <div className="schema-workbench__erd-more">
                        +{table.columns.length - 8} more columns
                      </div>
                    ) : null}
                  </article>
                );
              })}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

type VisualSchemaInspectorProps = {
  baselineDocument: SchemaModelDocument | null;
  document: SchemaModelDocument;
  selection: VisualCanvasSelection | null;
  onDocumentChange: (document: SchemaModelDocument) => void;
  onSelectionChange: (selection: VisualCanvasSelection | null) => void;
};

type VisualTableColorFieldProps = {
  table: SchemaTable;
  onChange: (color: string | null) => void;
};

const erdDefaultLayoutColor = "#2563eb";

function VisualTableColorField({ table, onChange }: VisualTableColorFieldProps) {
  const layout = getTableLayout(table);
  const currentColor =
    typeof layout?.color === "string" && layout.color.length > 0
      ? layout.color
      : null;

  return (
    <div className="schema-workbench__visual-color-field">
      <div className="schema-workbench__visual-color-row">
        <span
          className="schema-workbench__visual-color-swatch"
          title="Table color"
        >
          <input
            type="color"
            aria-label="Table color"
            value={currentColor ?? erdDefaultLayoutColor}
            onChange={(event) => onChange(event.currentTarget.value)}
          />
          <span
            aria-hidden="true"
            style={{ backgroundColor: currentColor ?? "transparent" }}
          />
        </span>
        <span className="schema-workbench__visual-color-value">
          {currentColor ?? "No color"}
        </span>
        {currentColor ? (
          <Button
            size="sm"
            variant="ghost"
            onClick={() => onChange(null)}
            aria-label="Clear table color"
          >
            Clear
          </Button>
        ) : null}
      </div>
      <span className="schema-workbench__visual-color-hint">
        Layout-only metadata. Saved with the schema but ignored by migrations.
      </span>
    </div>
  );
}

function VisualSchemaInspector({
  baselineDocument,
  document,
  selection,
  onDocumentChange,
  onSelectionChange,
}: VisualSchemaInspectorProps) {
  const [relationDraft, setRelationDraft] =
    useState<VisualRelationDraft | null>(null);
  const [relationDraftIssues, setRelationDraftIssues] = useState<
    VisualSchemaDraftIssue[]
  >([]);

  function commit(nextDocument: SchemaModelDocument) {
    onDocumentChange(nextDocument);
  }

  if (!selection) {
    return (
      <aside
        className="schema-workbench__visual-inspector"
        aria-label="Visual schema inspector"
      >
        <EmptyState
          title="Select a table, column, or relation"
          description="Schema settings appear here and update the shared Model JSON draft immediately."
          icon="pencil"
        />
      </aside>
    );
  }

  if (selection.type === "relation") {
    const relation = document.relations.find(
      (candidate) => candidate.name === selection.relationName,
    );

    return (
      <aside
        className="schema-workbench__visual-inspector"
        aria-label="Visual schema inspector"
      >
        {relation ? (
          <VisualRelationInspector
            baselineDocument={baselineDocument}
            document={document}
            issues={relationDraftIssues}
            relation={relation}
            onChange={(nextDocument) => {
              commit(nextDocument);
              setRelationDraftIssues([]);
            }}
            onIssues={setRelationDraftIssues}
            onSelectionChange={onSelectionChange}
          />
        ) : null}
      </aside>
    );
  }

  const selectedTable = document.tables.find(
    (candidate) => candidate.name === selection.tableName,
  );

  if (!selectedTable) {
    return (
      <aside
        className="schema-workbench__visual-inspector"
        aria-label="Visual schema inspector"
      >
        <EmptyState
          title="Selection no longer exists"
          description="Choose another table or column from the ERD canvas."
          icon="schema"
        />
      </aside>
    );
  }

  const activeTable = selectedTable;
  const selectedColumn =
    selection.type === "column"
      ? (activeTable.columns.find(
          (candidate) => candidate.name === selection.columnName,
        ) ?? null)
      : null;

  function updateSelectedTable(update: Partial<SchemaTable>) {
    if (typeof update.name === "string" && update.name !== activeTable.name) {
      const baselineHasTable = Boolean(
        baselineDocument?.tables.some(
          (table) => table.name === activeTable.name,
        ),
      );
      const renameHint =
        activeTable.renamedFrom ??
        (baselineHasTable ? activeTable.name : undefined);
      const nextRenameHint =
        renameHint && renameHint !== update.name ? renameHint : null;

      commit(
        renameTable(document, activeTable.name, update.name, nextRenameHint),
      );
      onSelectionChange({ type: "table", tableName: update.name });
      return;
    }

    const nextName =
      typeof update.name === "string" ? update.name : activeTable.name;
    commit(updateTable(document, activeTable.name, update));
    onSelectionChange({ type: "table", tableName: nextName });
  }

  function duplicateSelectedTable() {
    const { document: nextDocument, tableName } = duplicateTable(
      document,
      activeTable.name,
    );
    commit(nextDocument);
    onSelectionChange({ type: "table", tableName });
  }

  function deleteSelectedTable() {
    const impact = getTableDeleteImpact(document, activeTable.name);
    const details = [
      `Delete table "${activeTable.name}" from the visual draft?`,
      impact.relationCount > 0
        ? `${countLabel(impact.relationCount, "relation")} will be removed.`
        : null,
      impact.referenceColumnCount > 0
        ? `${countLabel(impact.referenceColumnCount, "reference column")} will be removed.`
        : null,
    ]
      .filter(Boolean)
      .join(" ");

    const confirmed = window.confirm(details);
    if (!confirmed) {
      return;
    }

    commit(deleteTableWithDependents(document, activeTable.name));
    onSelectionChange(null);
  }

  function updateSelectedColumn(
    column: SchemaColumn,
    update: Partial<SchemaColumn> | ((current: SchemaColumn) => SchemaColumn),
  ) {
    const nextDocument = updateColumn(
      document,
      activeTable.name,
      column.name,
      update,
    );
    const nextColumn =
      typeof update === "function" ? update(column) : { ...column, ...update };
    commit(nextDocument);
    onSelectionChange({
      type: "column",
      tableName: activeTable.name,
      columnName: nextColumn.name,
    });
  }

  function addNewColumn() {
    const columnName = getUniqueVisualName(
      activeTable.columns.map((column) => column.name),
      "new_column",
    );
    const nextColumn: SchemaColumn = {
      name: columnName,
      type: "text",
      nullable: true,
      renamedFrom: columnName,
    };

    commit(addColumn(document, activeTable.name, nextColumn));
    onSelectionChange({
      type: "column",
      tableName: activeTable.name,
      columnName,
    });
  }

  function moveSelectedColumn(column: SchemaColumn, direction: -1 | 1) {
    const currentIndex = activeTable.columns.findIndex(
      (candidate) => candidate.name === column.name,
    );
    if (currentIndex === -1) {
      return;
    }

    commit(
      reorderColumn(
        document,
        activeTable.name,
        column.name,
        currentIndex + direction,
      ),
    );
  }

  function deleteSelectedColumn(column: SchemaColumn) {
    const confirmed = window.confirm(
      `Delete column "${activeTable.name}.${column.name}" from the visual draft?`,
    );
    if (!confirmed) {
      return;
    }

    commit(removeColumn(document, activeTable.name, column.name));
    onSelectionChange({ type: "table", tableName: activeTable.name });
  }

  const activeRelationDraft =
    relationDraft?.fromTableName === activeTable.name ? relationDraft : null;
  const activeTableRelations = document.relations.filter(
    (relation) =>
      relation.from === activeTable.name || relation.to === activeTable.name,
  );

  function startRelationDraft() {
    const defaultToTable =
      document.tables.find((table) => table.name !== activeTable.name)?.name ??
      activeTable.name;
    setRelationDraft(
      createVisualRelationDraft(document, activeTable.name, defaultToTable),
    );
    setRelationDraftIssues([]);
  }

  function submitRelationDraft(draft: VisualRelationDraft) {
    const junctionLayout =
      draft.relationType === "N:M"
        ? getSuggestedJunctionLayout(
            document,
            draft.fromTableName,
            draft.toTableName,
          )
        : null;
    const result =
      draft.relationType === "N:M"
        ? createManyToManyRelation(document, {
            fromTableName: draft.fromTableName,
            toTableName: draft.toTableName,
            relationName: draft.relationName,
            junctionTableName: draft.junctionTableName,
            fromColumnName: draft.fromColumnName,
            toColumnName: draft.toColumnName,
            fromAccessor: draft.fromAccessor,
            toAccessor: draft.toAccessor,
            layout: junctionLayout,
          })
        : createOneToManyRelation(document, {
            fromTableName: draft.fromTableName,
            toTableName: draft.toTableName,
            relationName: draft.relationName,
            fromAccessor: draft.fromAccessor,
            toAccessor: draft.toAccessor,
            indexName: draft.indexName,
            columnName: draft.columnMode === "new" ? draft.columnName : null,
            reuseColumnName:
              draft.columnMode === "existing" ? draft.columnName : null,
          });

    if (result.issues.length > 0 || !result.relationName) {
      setRelationDraftIssues(result.issues);
      return;
    }

    commit(result.document);
    setRelationDraft(null);
    setRelationDraftIssues([]);
    onSelectionChange({ type: "relation", relationName: result.relationName });
  }

  return (
    <aside
      className="schema-workbench__visual-inspector"
      aria-label="Visual schema inspector"
    >
      {selectedColumn ? (
        <VisualColumnInspector
          column={selectedColumn}
          document={document}
          table={activeTable}
          onBackToTable={() =>
            onSelectionChange({ type: "table", tableName: activeTable.name })
          }
          onChange={(update) => updateSelectedColumn(selectedColumn, update)}
          onDelete={() => deleteSelectedColumn(selectedColumn)}
          onMoveDown={() => moveSelectedColumn(selectedColumn, 1)}
          onMoveUp={() => moveSelectedColumn(selectedColumn, -1)}
        />
      ) : (
        <>
          <section className="schema-workbench__visual-inspector-section">
            <h3>Table</h3>
            <TextField
              label="Table name"
              value={activeTable.name}
              onChange={(event) =>
                updateSelectedTable({ name: event.currentTarget.value })
              }
            />
            <TextField
              label="Table display name"
              value={activeTable.displayName ?? ""}
              onChange={(event) =>
                updateSelectedTable({
                  displayName: optionalVisualText(event.currentTarget.value),
                })
              }
            />
            <TextField
              label="Table renamed from"
              value={activeTable.renamedFrom ?? ""}
              onChange={(event) =>
                updateSelectedTable({
                  renamedFrom: optionalVisualText(event.currentTarget.value),
                })
              }
            />
            <VisualTableColorField
              table={activeTable}
              onChange={(nextColor) => {
                const layout = getTableLayout(activeTable);
                const fallback = getNextVisualTableLayout(document);
                commit(
                  setTableLayout(document, activeTable.name, {
                    x: layout?.x ?? fallback.x,
                    y: layout?.y ?? fallback.y,
                    color: nextColor,
                  }),
                );
              }}
            />
            <div className="schema-workbench__visual-inspector-actions schema-workbench__visual-inspector-actions--wrap">
              <Button
                size="sm"
                variant="primary"
                leadingIcon={<Icon name="plus" size={14} />}
                onClick={startRelationDraft}
              >
                New relation
              </Button>
              <Button
                size="sm"
                leadingIcon={<Icon name="copy" size={14} />}
                onClick={duplicateSelectedTable}
              >
                Duplicate table
              </Button>
              <Button
                size="sm"
                variant="danger"
                leadingIcon={<Icon name="trash" size={14} />}
                onClick={deleteSelectedTable}
              >
                Delete table
              </Button>
            </div>
            <Button
              size="sm"
              variant="primary"
              leadingIcon={<Icon name="plus" size={14} />}
              onClick={addNewColumn}
            >
              Add column
            </Button>
          </section>
          <section className="schema-workbench__visual-inspector-section">
            <h3>Columns</h3>
            <ol className="schema-workbench__visual-column-picker">
              {activeTable.columns.map((column) => (
                <li key={column.name}>
                  <button
                    type="button"
                    onClick={() =>
                      onSelectionChange({
                        type: "column",
                        tableName: activeTable.name,
                        columnName: column.name,
                      })
                    }
                  >
                    <span>{column.name}</span>
                    <code>{formatMiniErdTypeValue(column.type)}</code>
                  </button>
                </li>
              ))}
            </ol>
          </section>
          <section className="schema-workbench__visual-inspector-section">
            <div className="schema-workbench__visual-inspector-subhead">
              <h3>Relations</h3>
              <Button
                size="sm"
                leadingIcon={<Icon name="plus" size={14} />}
                onClick={startRelationDraft}
              >
                Add relation
              </Button>
            </div>
            {activeRelationDraft ? (
              <VisualRelationCreator
                document={document}
                draft={activeRelationDraft}
                issues={relationDraftIssues}
                onCancel={() => {
                  setRelationDraft(null);
                  setRelationDraftIssues([]);
                }}
                onChange={(nextDraft) => {
                  setRelationDraft(nextDraft);
                  setRelationDraftIssues([]);
                }}
                onCreate={() => submitRelationDraft(activeRelationDraft)}
              />
            ) : activeTableRelations.length > 0 ? (
              <ol className="schema-workbench__visual-column-picker">
                {activeTableRelations.map((relation) => (
                  <li key={relation.name}>
                    <button
                      type="button"
                      onClick={() =>
                        onSelectionChange({
                          type: "relation",
                          relationName: relation.name,
                        })
                      }
                    >
                      <span>{`${relation.name} (${relation.type})`}</span>
                    </button>
                  </li>
                ))}
              </ol>
            ) : (
              <p className="schema-workbench__visual-muted">
                No relations attached to this table.
              </p>
            )}
          </section>
        </>
      )}
    </aside>
  );
}

type VisualRelationCreatorProps = {
  document: SchemaModelDocument;
  draft: VisualRelationDraft;
  issues: VisualSchemaDraftIssue[];
  onCancel: () => void;
  onChange: (draft: VisualRelationDraft) => void;
  onCreate: () => void;
};

type VisualRelationInspectorProps = {
  baselineDocument: SchemaModelDocument | null;
  document: SchemaModelDocument;
  issues: VisualSchemaDraftIssue[];
  relation: SchemaRelation;
  onChange: (document: SchemaModelDocument) => void;
  onIssues: (issues: VisualSchemaDraftIssue[]) => void;
  onSelectionChange: (selection: VisualCanvasSelection | null) => void;
};

function VisualRelationInspector({
  baselineDocument,
  document,
  issues,
  relation,
  onChange,
  onIssues,
  onSelectionChange,
}: VisualRelationInspectorProps) {
  const [pendingName, setPendingName] = useState(relation.name);

  useEffect(() => {
    setPendingName(relation.name);
  }, [relation.name]);

  const tableOptions = document.tables.map((table) => ({
    label: table.name,
    value: table.name,
  }));
  const relationTypeOptions =
    [
      { label: "One-to-many", value: "1:N" },
      { label: "Many-to-many", value: "N:M" },
    ];

  function commitRename() {
    const trimmed = pendingName.trim();
    if (trimmed === relation.name) {
      setPendingName(relation.name);
      return;
    }

    const result = renameRelation(document, relation.name, trimmed);
    if (result.issues.length > 0 || !result.relationName) {
      onIssues(result.issues);
      setPendingName(relation.name);
      return;
    }

    onChange(result.document);
    onSelectionChange({ type: "relation", relationName: result.relationName });
  }

  function commitOneToMany(update: {
    columnName?: string | null;
    fromAccessor?: string | null;
    fromTableName?: string;
    toAccessor?: string | null;
    toTableName?: string;
  }) {
    const result = updateOneToManyRelation(document, {
      baselineDocument,
      relationName: relation.name,
      fromTableName: update.fromTableName ?? relation.from,
      toTableName: update.toTableName ?? relation.to,
      columnName: update.columnName ?? relation.column ?? null,
      fromAccessor: update.fromAccessor ?? relation.fromAccessor ?? null,
      toAccessor: update.toAccessor ?? relation.toAccessor ?? null,
    });

    if (result.issues.length > 0 || !result.relationName) {
      onIssues(result.issues);
      return;
    }

    onChange(result.document);
    onSelectionChange({ type: "relation", relationName: result.relationName });
  }

  function commitManyToMany(update: {
    fromAccessor?: string | null;
    fromColumnName?: string | null;
    fromTableName?: string;
    junctionTableName?: string | null;
    toAccessor?: string | null;
    toColumnName?: string | null;
    toTableName?: string;
  }) {
    const result = updateManyToManyRelation(document, {
      relationName: relation.name,
      fromTableName: update.fromTableName ?? relation.from,
      toTableName: update.toTableName ?? relation.to,
      fromColumnName: update.fromColumnName ?? relation.fromColumn ?? null,
      toColumnName: update.toColumnName ?? relation.toColumn ?? null,
      junctionTableName:
        update.junctionTableName ??
        relation.junctionTable ??
        relation.resolvedJunctionTableName ??
        null,
      fromAccessor: update.fromAccessor ?? relation.fromAccessor ?? null,
      toAccessor: update.toAccessor ?? relation.toAccessor ?? null,
    });

    if (result.issues.length > 0 || !result.relationName) {
      onIssues(result.issues);
      return;
    }

    onChange(result.document);
    onSelectionChange({ type: "relation", relationName: result.relationName });
  }

  function commitEndpoint(update: {
    fromTableName?: string;
    toTableName?: string;
  }) {
    const fromTableName = update.fromTableName ?? relation.from;
    const toTableName = update.toTableName ?? relation.to;

    if (relation.type === "1:N") {
      const defaults = getDefaultOneToManyRelationDraft(
        document,
        fromTableName,
        toTableName,
      );
      commitOneToMany({
        fromTableName,
        toTableName,
        columnName: defaults.columnName,
        fromAccessor: defaults.fromAccessor,
        toAccessor: defaults.toAccessor,
      });
      return;
    }

    commitManyToMany({ fromTableName, toTableName });
  }

  function switchDirection() {
    if (relation.type === "1:N") {
      const result = switchOneToManyRelationDirection(
        document,
        relation.name,
        baselineDocument,
      );
      if (result.issues.length > 0 || !result.relationName) {
        onIssues(result.issues);
        return;
      }

      onChange(result.document);
      onSelectionChange({
        type: "relation",
        relationName: result.relationName,
      });
      return;
    }

    const result = switchManyToManyRelationDirection(document, relation.name);
    if (result.issues.length > 0 || !result.relationName) {
      onIssues(result.issues);
      return;
    }

    onChange(result.document);
    onSelectionChange({ type: "relation", relationName: result.relationName });
  }

  function updateRelationType(nextType: "1:N" | "N:M") {
    if (nextType === relation.type) {
      return;
    }

    const result =
      relation.type === "1:N" && nextType === "N:M"
        ? convertOneToManyRelationToManyToMany(document, {
            relationName: relation.name,
            layout: getSuggestedJunctionLayout(document, relation.from, relation.to),
          })
        : relation.type === "N:M" && nextType === "1:N"
          ? convertManyToManyRelationToOneToMany(document, {
              relationName: relation.name,
            })
          : null;

    if (!result) {
      return;
    }

    if (result.issues.length > 0 || !result.relationName) {
      onIssues(result.issues);
      return;
    }

    onChange(result.document);
    onSelectionChange({ type: "relation", relationName: result.relationName });
  }

  function deleteRelation() {
    const result = deleteRelationWithArtifacts(document, relation.name);
    if (result.issues.length > 0) {
      onIssues(result.issues);
      return;
    }

    onChange(result.document);
    onSelectionChange(null);
  }

  const advancedRelationFields = getAdvancedRelationFields(relation);
  const resolvedFromAccessor =
    relation.fromAccessor && relation.fromAccessor.trim().length > 0
      ? null
      : (relation.resolvedFromAccessor ?? null);
  const resolvedToAccessor =
    relation.toAccessor && relation.toAccessor.trim().length > 0
      ? null
      : (relation.resolvedToAccessor ?? null);
  const resolvedJunctionTable =
    relation.type === "N:M" &&
    !(relation.junctionTable && relation.junctionTable.trim().length > 0)
      ? (relation.resolvedJunctionTableName ?? null)
      : null;

  return (
    <section
      className="schema-workbench__visual-inspector-section"
      data-testid="visual-relation-inspector"
    >
      <header className="schema-workbench__visual-relation-summary-header">
        <span className="schema-workbench__visual-relation-summary-type">
          {relation.type}
        </span>
        <span className="schema-workbench__visual-relation-summary-arrow">
          {`${relation.from} → ${relation.to}`}
        </span>
      </header>
      <TextField
        label="Relation name"
        value={pendingName}
        onBlur={commitRename}
        onChange={(event) => setPendingName(event.currentTarget.value)}
        onKeyDown={(event) => {
          if (event.key === "Enter") {
            event.preventDefault();
            (event.currentTarget as HTMLInputElement).blur();
          }
        }}
      />
      <SelectField
        label="Relation type"
        options={relationTypeOptions}
        value={relation.type}
        onChange={(event) =>
          updateRelationType(event.currentTarget.value as "1:N" | "N:M")
        }
      />
      <SelectField
        label="From"
        options={tableOptions}
        value={relation.from}
        onChange={(event) =>
          commitEndpoint({ fromTableName: event.currentTarget.value })
        }
      />
      <SelectField
        label="To"
        options={tableOptions}
        value={relation.to}
        onChange={(event) =>
          commitEndpoint({ toTableName: event.currentTarget.value })
        }
      />
      <Button
        size="sm"
        leadingIcon={<Icon name="arrow-up-down" size={14} />}
        onClick={switchDirection}
      >
        Switch direction
      </Button>
      <Button
        size="sm"
        variant="danger"
        leadingIcon={<Icon name="trash" size={14} />}
        onClick={deleteRelation}
      >
        Delete relation
      </Button>
      {relation.type === "1:N" ? (
        <TextField
          label="Reference column"
          value={relation.column ?? ""}
          onChange={(event) =>
            commitOneToMany({ columnName: event.currentTarget.value })
          }
        />
      ) : (
        <>
          <TextField
            label="Junction table"
            value={relation.junctionTable ?? relation.resolvedJunctionTableName ?? ""}
            onChange={(event) =>
              commitManyToMany({
                junctionTableName: event.currentTarget.value,
              })
            }
          />
          <TextField
            label="Source column"
            value={relation.fromColumn ?? ""}
            onChange={(event) =>
              commitManyToMany({
                fromColumnName: event.currentTarget.value,
              })
            }
          />
          <TextField
            label="Target column"
            value={relation.toColumn ?? ""}
            onChange={(event) =>
              commitManyToMany({
                toColumnName: event.currentTarget.value,
              })
            }
          />
        </>
      )}
      <TextField
        label="Parent accessor"
        value={relation.fromAccessor ?? ""}
        onChange={(event) =>
          relation.type === "1:N"
            ? commitOneToMany({ fromAccessor: event.currentTarget.value })
            : commitManyToMany({ fromAccessor: event.currentTarget.value })
        }
      />
      <TextField
        label="Child accessor"
        value={relation.toAccessor ?? ""}
        onChange={(event) =>
          relation.type === "1:N"
            ? commitOneToMany({ toAccessor: event.currentTarget.value })
            : commitManyToMany({ toAccessor: event.currentTarget.value })
        }
      />
      {issues.length > 0 ? (
        <ol
          className="schema-workbench__visual-relation-errors"
          data-testid="visual-relation-inspector-errors"
        >
          {issues.map((issue) => (
            <li key={`${issue.code}:${issue.path}`}>
              <strong>{issue.code}</strong>
              <span>{issue.message}</span>
            </li>
          ))}
        </ol>
      ) : null}
      {resolvedFromAccessor || resolvedToAccessor || resolvedJunctionTable ? (
        <dl className="schema-workbench__visual-relation-resolved">
          {resolvedFromAccessor ? (
            <div>
              <dt>Resolved parent accessor</dt>
              <dd>
                <code>{resolvedFromAccessor}</code>
              </dd>
            </div>
          ) : null}
          {resolvedToAccessor ? (
            <div>
              <dt>Resolved child accessor</dt>
              <dd>
                <code>{resolvedToAccessor}</code>
              </dd>
            </div>
          ) : null}
          {resolvedJunctionTable ? (
            <div>
              <dt>Resolved junction table</dt>
              <dd>
                <code>{resolvedJunctionTable}</code>
              </dd>
            </div>
          ) : null}
        </dl>
      ) : null}
      {advancedRelationFields.length > 0 ? (
        <details
          className="schema-workbench__visual-relation-advanced"
          data-testid="visual-relation-advanced"
        >
          <summary>Advanced (read-only)</summary>
          <p className="schema-workbench__visual-muted">
            These fields are not editable from the visual designer yet. Use JSON
            mode if you need to change them.
          </p>
          <dl>
            {advancedRelationFields.map((field) => (
              <div key={field.key}>
                <dt>
                  <code>{field.key}</code>
                </dt>
                <dd>
                  <code>{field.value}</code>
                </dd>
              </div>
            ))}
          </dl>
        </details>
      ) : null}
    </section>
  );
}

const knownRelationFieldKeys = new Set([
  "name",
  "type",
  "from",
  "to",
  "fromAccessor",
  "toAccessor",
  "column",
  "fromColumn",
  "toColumn",
  "junctionTable",
  "resolvedFromAccessor",
  "resolvedToAccessor",
  "resolvedJunctionTableName",
]);

function getAdvancedRelationFields(
  relation: SchemaRelation,
): Array<{ key: string; value: string }> {
  const result: Array<{ key: string; value: string }> = [];
  for (const [key, value] of Object.entries(relation)) {
    if (knownRelationFieldKeys.has(key)) {
      continue;
    }
    if (value === undefined || value === null) {
      continue;
    }
    let formatted: string;
    try {
      formatted =
        typeof value === "string" ? value : JSON.stringify(value);
    } catch {
      formatted = String(value);
    }
    result.push({ key, value: formatted });
  }
  return result;
}

function VisualRelationCreator({
  document,
  draft,
  issues,
  onCancel,
  onChange,
  onCreate,
}: VisualRelationCreatorProps) {
  const tableOptions = document.tables.map((table) => ({
    label: table.name,
    value: table.name,
  }));
  const reusableColumns = getReusableRelationColumnOptions(
    document,
    draft.fromTableName,
    draft.toTableName,
  );
  const columnOptions = [
    { label: "Create new reference column", value: "__new__" },
    ...reusableColumns.map((column) => ({
      label: column.name,
      value: column.name,
    })),
  ];

  function updateToTable(toTableName: string) {
    const nextDraft = createVisualRelationDraft(
      document,
      draft.fromTableName,
      toTableName,
    );
    if (draft.relationType === "N:M") {
      const manyDefaults = getDefaultManyToManyRelationDraft(
        document,
        draft.fromTableName,
        toTableName,
      );
      onChange({
        ...nextDraft,
        relationType: "N:M",
        ...manyDefaults,
      });
      return;
    }

    onChange(nextDraft);
  }

  function updateRelationType(relationType: "1:N" | "N:M") {
    if (relationType === draft.relationType) {
      return;
    }

    const nextDraft = createVisualRelationDraft(
      document,
      draft.fromTableName,
      draft.toTableName,
    );
    if (relationType === "N:M") {
      const manyDefaults = getDefaultManyToManyRelationDraft(
        document,
        draft.fromTableName,
        draft.toTableName,
      );
      onChange({
        ...nextDraft,
        relationType,
        ...manyDefaults,
      });
      return;
    }

    onChange(nextDraft);
  }

  function updateColumnMode(value: string) {
    if (value === "__new__") {
      const defaults = getDefaultOneToManyRelationDraft(
        document,
        draft.fromTableName,
        draft.toTableName,
      );
      onChange({
        ...draft,
        columnMode: "new",
        columnName: defaults.columnName,
        indexName: defaults.indexName,
      });
      return;
    }

    const indexName = getUniqueVisualName(
      document.tables
        .find((table) => table.name === draft.toTableName)
        ?.indexes?.map((index) => index.name) ?? [],
      `idx_${draft.toTableName}_${value}`,
    );
    onChange({
      ...draft,
      columnMode: "existing",
      columnName: value,
      indexName,
    });
  }

  return (
    <div className="schema-workbench__visual-relation-creator">
      <SelectField
        label="Relation type"
        options={[
          { label: "One-to-many", value: "1:N" },
          { label: "Many-to-many", value: "N:M" },
        ]}
        value={draft.relationType}
        onChange={(event) =>
          updateRelationType(event.currentTarget.value as "1:N" | "N:M")
        }
      />
      <SelectField
        label={draft.relationType === "N:M" ? "Target table" : "Child table"}
        options={tableOptions}
        value={draft.toTableName}
        onChange={(event) => updateToTable(event.currentTarget.value)}
      />
      <TextField
        label="Relation name"
        value={draft.relationName}
        onChange={(event) =>
          onChange({ ...draft, relationName: event.currentTarget.value })
        }
      />
      {draft.relationType === "N:M" ? (
        <>
          <TextField
            label="Junction table"
            value={draft.junctionTableName}
            onChange={(event) =>
              onChange({
                ...draft,
                junctionTableName: event.currentTarget.value,
              })
            }
          />
          <TextField
            label="Source column"
            value={draft.fromColumnName}
            onChange={(event) =>
              onChange({ ...draft, fromColumnName: event.currentTarget.value })
            }
          />
          <TextField
            label="Target column"
            value={draft.toColumnName}
            onChange={(event) =>
              onChange({ ...draft, toColumnName: event.currentTarget.value })
            }
          />
        </>
      ) : (
        <>
          <SelectField
            label="Reference column"
            options={columnOptions}
            value={draft.columnMode === "new" ? "__new__" : draft.columnName}
            onChange={(event) => updateColumnMode(event.currentTarget.value)}
          />
          <TextField
            label="Column name"
            disabled={draft.columnMode === "existing"}
            value={draft.columnName}
            onChange={(event) =>
              onChange({ ...draft, columnName: event.currentTarget.value })
            }
          />
        </>
      )}
      <TextField
        label="Parent accessor"
        value={draft.fromAccessor}
        onChange={(event) =>
          onChange({ ...draft, fromAccessor: event.currentTarget.value })
        }
      />
      <TextField
        label="Child accessor"
        value={draft.toAccessor}
        onChange={(event) =>
          onChange({ ...draft, toAccessor: event.currentTarget.value })
        }
      />
      {draft.relationType === "1:N" ? (
        <TextField
          label="Index name"
          value={draft.indexName}
          onChange={(event) =>
            onChange({ ...draft, indexName: event.currentTarget.value })
          }
        />
      ) : null}
      {issues.length > 0 ? (
        <ol className="schema-workbench__visual-relation-errors">
          {issues.map((issue) => (
            <li key={`${issue.code}:${issue.path}`}>
              <strong>{issue.code}</strong>
              <span>{issue.message}</span>
            </li>
          ))}
        </ol>
      ) : null}
      <div className="schema-workbench__visual-inspector-actions">
        <Button size="sm" variant="primary" onClick={onCreate}>
          Create relation
        </Button>
        <Button size="sm" onClick={onCancel}>
          Cancel
        </Button>
      </div>
    </div>
  );
}

type VisualColumnInspectorProps = {
  column: SchemaColumn;
  document: SchemaModelDocument;
  table: SchemaTable;
  onBackToTable: () => void;
  onChange: (
    update: Partial<SchemaColumn> | ((current: SchemaColumn) => SchemaColumn),
  ) => void;
  onDelete: () => void;
  onMoveDown: () => void;
  onMoveUp: () => void;
};

function VisualColumnInspector({
  column,
  document,
  table,
  onBackToTable,
  onChange,
  onDelete,
  onMoveDown,
  onMoveUp,
}: VisualColumnInspectorProps) {
  const columnIndex = table.columns.findIndex(
    (candidate) => candidate.name === column.name,
  );

  return (
    <section className="schema-workbench__visual-inspector-section">
      <div className="schema-workbench__visual-inspector-subhead">
        <div className="schema-workbench__visual-inspector-title-group">
          <Button
            size="sm"
            variant="ghost"
            leadingIcon={<Icon name="chevron-left" size={14} />}
            onClick={onBackToTable}
          >
            Table columns
          </Button>
          <h3>Column</h3>
        </div>
        <div className="schema-workbench__visual-inspector-actions">
          <Button
            size="icon"
            aria-label="Move column up"
            title="Move column up"
            disabled={columnIndex <= 0}
            onClick={onMoveUp}
          >
            <Icon name="chevron-up" size={14} />
          </Button>
          <Button
            size="icon"
            aria-label="Move column down"
            title="Move column down"
            disabled={
              columnIndex === -1 || columnIndex >= table.columns.length - 1
            }
            onClick={onMoveDown}
          >
            <Icon name="chevron-down" size={14} />
          </Button>
          <Button
            size="icon"
            variant="danger"
            aria-label="Delete column"
            title="Delete column"
            onClick={onDelete}
          >
            <Icon name="trash" size={14} />
          </Button>
        </div>
      </div>
      <TextField
        label="Column name"
        value={column.name}
        onChange={(event) => onChange({ name: event.currentTarget.value })}
      />
      <TextField
        label="Column display name"
        value={column.displayName ?? ""}
        onChange={(event) =>
          onChange({
            displayName: optionalVisualText(event.currentTarget.value),
          })
        }
      />
      <SelectField
        label="Column type"
        options={visualColumnTypeOptions}
        value={column.type}
        onChange={(event) =>
          onChange((current) =>
            normalizeVisualColumnType(current, event.currentTarget.value),
          )
        }
      />
      {column.type === "reference" ? (
        <SelectField
          label="Reference target"
          options={[
            { label: "Select table", value: "" },
            ...document.tables.map((candidate) => ({
              label: candidate.name,
              value: candidate.name,
            })),
          ]}
          value={typeof column.references === "string" ? column.references : ""}
          onChange={(event) =>
            onChange({
              references: event.currentTarget.value || null,
            })
          }
        />
      ) : null}
      <div className="schema-workbench__visual-checkbox-grid">
        <VisualCheckbox
          label="Primary key"
          checked={Boolean(column.primaryKey)}
          onChange={(checked) => onChange({ primaryKey: checked || undefined })}
        />
        <VisualCheckbox
          label="Required"
          checked={column.nullable === false}
          onChange={(checked) => onChange({ nullable: checked ? false : true })}
        />
        <VisualCheckbox
          label="Unique"
          checked={Boolean(column.unique)}
          onChange={(checked) => onChange({ unique: checked || undefined })}
        />
      </div>
      <TextField
        label="Default value"
        value={stringifyVisualDefault(column.default)}
        onChange={(event) =>
          onChange((current) => ({
            ...current,
            default: optionalVisualText(event.currentTarget.value),
          }))
        }
      />
      <TextField
        label="Renamed from"
        value={column.renamedFrom ?? ""}
        onChange={(event) =>
          onChange({
            renamedFrom: optionalVisualText(event.currentTarget.value),
          })
        }
      />
      {column.type === "enum" ? (
        <TextField
          label="Enum values"
          value={(column.values ?? []).join(", ")}
          message="Comma-separated values"
          onChange={(event) =>
            onChange({
              values: parseVisualEnumValues(event.currentTarget.value),
            })
          }
        />
      ) : null}
    </section>
  );
}

type VisualCheckboxProps = {
  checked: boolean;
  label: string;
  onChange: (checked: boolean) => void;
};

function VisualCheckbox({ checked, label, onChange }: VisualCheckboxProps) {
  return (
    <label className="schema-workbench__visual-checkbox">
      <input
        type="checkbox"
        checked={checked}
        onChange={(event) => onChange(event.currentTarget.checked)}
      />
      <span>{label}</span>
    </label>
  );
}

function getUniqueVisualName(
  existingNames: string[],
  baseName: string,
): string {
  const names = new Set(existingNames);
  if (!names.has(baseName)) {
    return baseName;
  }

  let suffix = 2;
  while (names.has(`${baseName}_${suffix}`)) {
    suffix += 1;
  }

  return `${baseName}_${suffix}`;
}

function optionalVisualText(value: string): string | undefined {
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : undefined;
}

function parseVisualEnumValues(value: string): string[] {
  return value
    .split(",")
    .map((item) => item.trim())
    .filter(Boolean);
}

function stringifyVisualDefault(value: unknown): string {
  if (value === undefined || value === null) {
    return "";
  }

  return typeof value === "string" ? value : JSON.stringify(value);
}

function normalizeVisualColumnType(
  column: SchemaColumn,
  typeValue: string,
): SchemaColumn {
  const nextType = typeValue as SchemaColumnType;
  const nextColumn: SchemaColumn = {
    ...column,
    type: nextType,
  };

  if (nextType !== "enum") {
    delete nextColumn.values;
  } else if (!Array.isArray(nextColumn.values)) {
    nextColumn.values = [];
  }

  if (nextType !== "reference") {
    delete nextColumn.references;
  } else {
    nextColumn.references =
      typeof nextColumn.references === "string" ? nextColumn.references : null;
  }

  return nextColumn;
}

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
              <span
                className={
                  column.primaryKey
                    ? "schema-workbench__mini-erd-column-name schema-workbench__mini-erd-column-name--primary-key"
                    : "schema-workbench__mini-erd-column-name"
                }
              >
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
  isLayoutOnlyDraft?: boolean;
  response: SchemaPreviewResponse;
  groups: PreviewChangeGroup[];
};

function PreviewSummary({
  errorCount,
  warningCount,
  isLayoutOnlyDraft = false,
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
        <div
          className="schema-workbench__preview-no-changes"
          data-testid={
            isLayoutOnlyDraft
              ? "schema-preview-layout-only"
              : "schema-preview-no-changes"
          }
        >
          {isLayoutOnlyDraft ? (
            <EmptyState
              title="Layout-only changes"
              description="No database migration is required. Apply will save the updated layout/metadata in place without bumping the schema version."
              icon="check"
              bullets={[
                "Migration plan is empty so no SQL will run",
                "Layout color, position, and other inert metadata will be persisted",
              ]}
            />
          ) : (
            <EmptyState
              title="No schema changes"
              description="Preview completed successfully. This draft matches the current schema, so there is nothing to apply."
              icon="check"
              bullets={[
                "Apply stays disabled until the preview contains schema changes",
              ]}
            />
          )}
        </div>
      ) : null}
    </div>
  );
}

function PreviewChangeCard({ change }: PreviewChangeCardProps) {
  const description = describePreviewChange(change);
  const isDropChange =
    change.changeType === "DropTable" ||
    change.changeType === "DropColumn" ||
    change.changeType === "DropRelation" ||
    change.changeType === "DropIndex";

  return (
    <li
      className={[
        "schema-workbench__preview-change",
        change.isDestructive && isDropChange
          ? "schema-workbench__preview-change--destructive schema-workbench__preview-change--drop-destructive"
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
            <span
              className={[
                "schema-workbench__preview-badge",
                isDropChange
                  ? "schema-workbench__preview-badge--destructive"
                  : "schema-workbench__preview-badge--warning",
              ].join(" ")}
            >
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
      {change.impact ? <RelationImpact impact={change.impact} /> : null}
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

type RelationImpactProps = {
  impact: SchemaChangeImpact;
};

function RelationImpact({ impact }: RelationImpactProps) {
  const sampleRows = impact.sampleRows ?? [];
  const sampleValues = impact.sampleValues ?? [];
  const sampleCount =
    sampleRows.length > 0 ? sampleRows.length : sampleValues.length;
  const hasMoreSamples =
    typeof impact.rowCount === "number" && impact.rowCount > sampleCount;
  const isDropImpact =
    impact.kind === "column_drop" ||
    impact.kind === "many_to_many_junction_drop" ||
    impact.kind === "table_drop";

  return (
    <div
      className={[
        "schema-workbench__relation-impact",
        isDropImpact ? "schema-workbench__relation-impact--drop" : "",
      ]
        .filter(Boolean)
        .join(" ")}
    >
      <h4>
        {impact.objectType === "column"
          ? "Column Impact"
          : impact.objectType === "table"
            ? "Table Impact"
            : "Relation Impact"}
      </h4>
      <p>{impact.message}</p>
      <dl className="schema-workbench__relation-impact-summary">
        <div>
          <dt>
            {impact.objectType === "table"
              ? "Table"
              : impact.objectType === "column"
                ? "Column"
                : "Object"}
          </dt>
          <dd>{impact.objectName}</dd>
        </div>
        {typeof impact.rowCount === "number" ? (
          <div>
            <dt>Rows at risk</dt>
            <dd>{impact.rowCount}</dd>
          </div>
        ) : null}
      </dl>
      {sampleRows.length > 0 ? (
        <ol className="schema-workbench__relation-impact-rows">
          {sampleRows.map((row, index) => (
            <li key={`${impact.objectName}:${index}`}>
              <Icon name="trash" size={12} aria-hidden="true" />
              <code className="mono">
                {Object.entries(row)
                  .map(([key, value]) => `${key}: ${formatPreviewValue(value)}`)
                  .join(", ")}
              </code>
            </li>
          ))}
          {hasMoreSamples ? (
            <li className="schema-workbench__relation-impact-more">
              <Icon name="trash" size={12} aria-hidden="true" />
              <span>...</span>
            </li>
          ) : null}
        </ol>
      ) : null}
      {sampleValues.length > 0 ? (
        <ol className="schema-workbench__relation-impact-rows">
          {sampleValues.map((value, index) => (
            <li key={`${impact.objectName}:value:${index}`}>
              <Icon name="trash" size={12} aria-hidden="true" />
              <code className="mono">{formatPreviewValue(value)}</code>
            </li>
          ))}
          {hasMoreSamples ? (
            <li className="schema-workbench__relation-impact-more">
              <Icon name="trash" size={12} aria-hidden="true" />
              <span>...</span>
            </li>
          ) : null}
        </ol>
      ) : null}
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
