import {
  useEffect,
  useMemo,
  useRef,
  useState,
  type Dispatch,
  type ReactNode,
  type SetStateAction,
} from "react";
import { useNavigate } from "react-router-dom";
import { ApiClientError } from "../../api/client";
import { buildQueryString, createRow, deleteRow, fetchTableRows, patchRow } from "../../api/data";
import { Icon } from "../../components/common/Icon";
import { Button } from "../../components/ui/Button";
import { ConfirmDialog } from "../../components/ui/Dialog";
import { Spinner } from "../../components/ui/Spinner";
import {
  buildCompositeRowId,
  isWritableColumn,
  useExplorerStore,
} from "../../stores/explorerStore";
import { useSchemaStore } from "../../stores/schemaStore";
import { DEFAULT_QUERY } from "../../types/explorer";
import type { ModelColumn, ModelTable } from "../../types/api";
import { pickEditorForColumn } from "./components/editors";
import { getDisplayColumnForTable } from "./components/editors/referenceMetadata";
import { renderCellValue } from "./components/cellValue";
import {
  lookupReferenceLabel,
  lookupReferenceSecondary,
  useReferenceLabels,
  type ReferenceLabelMap,
} from "./components/useReferenceLabels";
import {
  getIncomingRelationSections,
  type IncomingRelationSection,
} from "./relationNavigation";

const EMPTY_CELL_ERRORS: Record<string, string> = Object.freeze({}) as Record<string, string>;
const EMPTY_PENDING_SAVES: string[] = Object.freeze([]) as unknown as string[];
const EMPTY_RELATION_SECTIONS = Object.freeze([]) as ReadonlyArray<IncomingRelationSection>;


function getRecordTitle(table: ModelTable, row: Record<string, unknown> | null): string {
  if (!row) return table.displayName ?? table.name;
  const id = row.id;
  // The table's record label; without one, a text column (a number or a
  // foreign key is no name, the title then reads "Orders #1").
  const configured = table.recordLabel?.column
    ? getDisplayColumnForTable(table)
    : null;
  const label = configured
    ? table.columns.find((column) => column.name === configured && !column.primaryKey)
    : table.columns.find(
        (column) =>
          !column.primaryKey && (column.type === "text" || column.type === "string"),
      );
  const labelValue = label ? row[label.name] : null;
  if (labelValue !== null && labelValue !== undefined && labelValue !== "") {
    return `${labelValue}`;
  }
  return `${table.displayName ?? table.name} #${String(id ?? "")}`;
}

function getFkTargetTable(column: ModelColumn): string | null {
  const references = column.references as { table?: string } | string | undefined;
  if (typeof references === "string" && references) return references;
  if (references && typeof references !== "string" && references.table) {
    return references.table;
  }
  const relationTable = (column as unknown as { relationTable?: string })
    .relationTable;
  return typeof relationTable === "string" && relationTable ? relationTable : null;
}

function coerceColumnValue(column: ModelColumn | null, value: unknown): unknown {
  if (
    column &&
    (column.type === "int64" ||
      column.type === "integer" ||
      column.type === "number") &&
    typeof value === "string"
  ) {
    const asNumber = Number(value);
    if (Number.isInteger(asNumber) && String(asNumber) === value) {
      return asNumber;
    }
  }
  return value;
}

function ensureRowId(table: ModelTable, row: Record<string, unknown>): Record<string, unknown> {
  if (row.id !== null && row.id !== undefined && row.id !== "") return row;
  const compositeId = buildCompositeRowId(table, row);
  return compositeId ? { ...row, id: compositeId } : row;
}

function getPersistedRowId(
  table: ModelTable,
  row: Record<string, unknown>,
  fallbackRowId?: string,
): string {
  const normalized = ensureRowId(table, row);
  const normalizedId = normalized.id;
  if (normalizedId !== null && normalizedId !== undefined && normalizedId !== "") {
    return String(normalizedId);
  }
  if (fallbackRowId) return fallbackRowId;
  return String(row.id ?? "");
}

function buildDraftJunctionRow(
  table: ModelTable,
  hiddenColumnName: string,
  hiddenValue: unknown,
): Record<string, unknown> {
  const row: Record<string, unknown> = {};
  for (const column of table.columns) {
    row[column.name] = column.name === hiddenColumnName ? hiddenValue : null;
  }
  return row;
}

/** Now as local wall time, the format the date editors write ("2026-10-03T15:29:43"). */
function localNowWallTime(): string {
  const now = new Date();
  const pad = (value: number) => String(value).padStart(2, "0");
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}T${pad(now.getHours())}:${pad(now.getMinutes())}:${pad(now.getSeconds())}`;
}

function buildJunctionPayload(
  table: ModelTable,
  row: Record<string, unknown>,
): Record<string, unknown> {
  const payload: Record<string, unknown> = {};
  for (const column of table.columns.filter(isWritableColumn)) {
    const value = row[column.name];
    if (value === undefined || value === null || value === "") continue;
    payload[column.name] = value;
  }
  return payload;
}

export function RecordEditor() {
  const navigate = useNavigate();
  const editorState = useExplorerStore((state) => state.recordEditor);
  const closeRecordEditor = useExplorerStore((state) => state.closeRecordEditor);
  const openRecordEditor = useExplorerStore((state) => state.openRecordEditor);
  const deleteRow = useExplorerStore((state) => state.deleteRow);
  const popRecordEditor = useExplorerStore((state) => state.popRecordEditor);
  const pushRecordEditor = useExplorerStore((state) => state.pushRecordEditor);
  const retryRecordEditorLoad = useExplorerStore(
    (state) => state.retryRecordEditorLoad,
  );
  const startCellEdit = useExplorerStore((state) => state.startCellEdit);
  const cancelCellEdit = useExplorerStore((state) => state.cancelCellEdit);
  const commitCellEdit = useExplorerStore((state) => state.commitCellEdit);
  const snapshot = useSchemaStore((state) => state.snapshot);

  // The top of the stack is the record currently rendered. Everything below is
  // breadcrumb context. The helper here also lets callers depend on a stable
  // reference when the top hasn't changed.
  const top = useMemo(() => {
    if (!editorState || editorState.stack.length === 0) return null;
    return editorState.stack[editorState.stack.length - 1] ?? null;
  }, [editorState]);
  const depth = editorState?.stack.length ?? 0;

  const view = useExplorerStore((state) =>
    top ? (state.views[top.table] ?? null) : null,
  );
  const cachedRow = useExplorerStore((state) =>
    top ? (state.recordCacheByTable[top.table]?.[top.rowId] ?? null) : null,
  );
  const liveRow = useMemo(() => {
    if (!top || !view) return null;
    return view.rows.find((row) => String(row.id ?? "") === top.rowId) ?? null;
  }, [top, view]);
  const currentRow = cachedRow ?? liveRow ?? top?.row ?? null;
  const cellErrors = useMemo<Record<string, string>>(() => {
    if (!top || !view) return EMPTY_CELL_ERRORS;
    return view.cellErrors[top.rowId] ?? EMPTY_CELL_ERRORS;
  }, [top, view]);
  const pendingCellSaves = view?.pendingCellSaves ?? EMPTY_PENDING_SAVES;
  const editingCell = view?.editingCell ?? null;

  const table = useMemo<ModelTable | null>(() => {
    if (!top || !snapshot) return null;
    return snapshot.tables.find((t) => t.name === top.table) ?? null;
  }, [top, snapshot]);
  const incomingRelationSections = useMemo(() => {
    if (!snapshot || !table) return EMPTY_RELATION_SECTIONS;
    return getIncomingRelationSections(snapshot, table);
  }, [snapshot, table]);

  const referenceRows = useMemo(
    () => (currentRow ? [currentRow] : []),
    [currentRow],
  );
  const referenceLabels = useReferenceLabels(
    table ?? ({ name: "", displayName: "", columns: [] } as unknown as ModelTable),
    referenceRows,
  );

  // Prev/Next walk the rows of the table view the editor was opened from
  // (only for the record at the root of the stack; drilled-in records have
  // no list to walk).
  const neighbours = useMemo(() => {
    if (!top || !view || depth !== 1) return { prev: null, next: null };
    const pending = new Set(view.pendingNewRowIds);
    const saved = view.rows.filter((row) => !pending.has(String(row.id ?? "")));
    const index = saved.findIndex((row) => String(row.id ?? "") === top.rowId);
    if (index === -1) return { prev: null, next: null };
    return { prev: saved[index - 1] ?? null, next: saved[index + 1] ?? null };
  }, [top, view, depth]);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);

  const previouslyFocusedRef = useRef<HTMLElement | null>(null);
  const panelRef = useRef<HTMLDivElement | null>(null);
  const closeButtonRef = useRef<HTMLButtonElement | null>(null);

  useEffect(() => {
    if (!editorState) return;
    previouslyFocusedRef.current =
      typeof document !== "undefined" && document.activeElement instanceof HTMLElement
        ? document.activeElement
        : null;
    closeButtonRef.current?.focus();
    return () => {
      const prev = previouslyFocusedRef.current;
      // Don't restore focus to the row-expand button — it keeps the row's
      // "actions" overlay visible via :focus-within even after the mouse has
      // moved away. Fall back to blurring so the next user action picks focus.
      if (prev && !prev.classList?.contains("data-grid__row-expand")) {
        prev.focus?.();
      } else if (typeof document !== "undefined") {
        (document.activeElement as HTMLElement | null)?.blur?.();
      }
    };
    // We intentionally re-run focus capture only when the editor opens/closes,
    // not on every stack push — otherwise every FK drill-in would reset focus
    // back to the close button and disrupt keyboard navigation.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [editorState === null]);

  useEffect(() => {
    if (!editorState) return;
    function onKey(event: KeyboardEvent) {
      if (event.key !== "Escape") return;
      if (editingCell) return;
      event.preventDefault();
      // TASK-099: at depth > 1 Esc pops one entry off the stack instead of
      // tearing the whole editor down. At depth 1 it still closes.
      if ((editorState?.stack.length ?? 0) > 1) {
        popRecordEditor();
      } else {
        closeRecordEditor();
      }
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [editorState, closeRecordEditor, popRecordEditor, editingCell]);

  if (!editorState || !top) return null;

  const title = table ? getRecordTitle(table, currentRow) : `Record #${top.rowId}`;
  const topRowId = top.rowId;
  const topTableName = top.table;
  const showBack = depth > 1;

  function openNeighbour(row: Record<string, unknown> | null) {
    if (!row) return;
    setDeleteError(null);
    void openRecordEditor({ table: topTableName, rowId: String(row.id ?? ""), row });
  }

  async function handleDelete() {
    setConfirmDelete(false);
    setDeleteError(null);
    const deleted = await deleteRow(topTableName, topRowId);
    if (!deleted) {
      setDeleteError("The record could not be deleted.");
      return;
    }
    if (depth > 1) popRecordEditor();
    else closeRecordEditor();
  }

  const hasRelations = Boolean(table && currentRow && incomingRelationSections.length > 0);
  const titleSuffix = ` #${topRowId}`;
  const titleMain = title.endsWith(titleSuffix) ? title.slice(0, -titleSuffix.length) : title;

  function handleGoToTable() {
    closeRecordEditor();
    navigate(`/data/${topTableName}`);
  }

  function handleViewAllRelated(section: IncomingRelationSection) {
    const query = buildQueryString({
      ...DEFAULT_QUERY,
      filters: [
        {
          column: section.filterColumn,
          op: "eq",
          value: topRowId,
        },
      ],
    });
    closeRecordEditor();
    navigate(`/data/${section.targetTable}?${query}`);
  }

  return (
    <div className="record-editor" role="presentation">
      <button
        type="button"
        className="record-editor__backdrop"
        aria-label="Close record editor"
        onClick={() => closeRecordEditor()}
      />
      <div
        className="record-editor__panel"
        role="dialog"
        aria-modal="true"
        aria-labelledby="record-editor-title"
        ref={panelRef}
      >
        <header className="record-editor__header">
          <div className="record-editor__title-block">
            {showBack ? (
              <button
                type="button"
                className="record-editor__back"
                aria-label="Back to previous record"
                onClick={() => popRecordEditor()}
                data-testid="record-editor-back"
              >
                <Icon name="chevron-left" size={14} />
                <span>Back</span>
              </button>
            ) : null}
            {depth > 1 ? (
              <RecordEditorBreadcrumbs
                stack={editorState.stack}
                snapshot={snapshot}
                onJump={(index) => {
                  // Pop until `index` is the top. popRecordEditor is a single
                  // step so we call it repeatedly in the event handler.
                  const popsNeeded = editorState.stack.length - 1 - index;
                  for (let i = 0; i < popsNeeded; i += 1) popRecordEditor();
                }}
              />
            ) : null}
            <span className="record-editor__eyebrow">
              {table?.displayName ?? table?.name ?? top.table} · record
            </span>
            <h2 id="record-editor-title" className="record-editor__title">
              {titleMain}
              {titleMain !== title ? (
                <span className="record-editor__title-id">{titleSuffix}</span>
              ) : null}
            </h2>
          </div>
          <div className="record-editor__header-actions">
            {depth === 1 ? (
              <>
                <button
                  type="button"
                  className="record-editor__tool"
                  onClick={() => openNeighbour(neighbours.prev)}
                  disabled={!neighbours.prev}
                  aria-label="Previous record"
                >
                  <Icon name="chevron-left" size={13} />
                  Prev
                </button>
                <button
                  type="button"
                  className="record-editor__tool"
                  onClick={() => openNeighbour(neighbours.next)}
                  disabled={!neighbours.next}
                  aria-label="Next record"
                >
                  Next
                  <Icon name="chevron-right" size={13} />
                </button>
              </>
            ) : null}
            <button type="button" className="record-editor__tool" onClick={handleGoToTable}>
              Go to table
            </button>
            <button
              ref={closeButtonRef}
              type="button"
              className="record-editor__close"
              aria-label="Close record editor"
              onClick={() => closeRecordEditor()}
            >
              <Icon name="x" size={16} />
            </button>
          </div>
        </header>

        <div
          className={[
            "record-editor__body",
            hasRelations ? "record-editor__body--split" : "",
          ]
            .filter(Boolean)
            .join(" ")}
        >
          <section className="record-editor__main" aria-label="Fields">
          {top.status === "loading" && !currentRow ? (
            <div className="record-editor__status" data-testid="record-editor-loading">
              <Spinner label="Loading record" />
            </div>
          ) : top.status === "error" ? (
            <div className="record-editor__error" role="alert">
              <p>{top.error ?? "Failed to load record."}</p>
              <Button
                type="button"
                variant="ghost"
                size="sm"
                onClick={() => void retryRecordEditorLoad()}
                leadingIcon={<Icon name="refresh" size={13} />}
              >
                Retry
              </Button>
            </div>
          ) : table && currentRow ? (
            <RecordFieldList
              table={table}
              row={currentRow}
              cellErrors={cellErrors}
              pendingCellSaves={pendingCellSaves}
              editingCell={editingCell}
              referenceLabels={referenceLabels}
              onStartEdit={(column) =>
                startCellEdit(top.table, top.rowId, column.name)
              }
              onOpenReference={(column, value) => {
                const targetTable = getFkTargetTable(column);
                if (!targetTable) return;
                if (value === null || value === undefined || value === "") return;
                void pushRecordEditor({
                  table: targetTable,
                  rowId: String(value),
                });
              }}
              onCancelEdit={() => cancelCellEdit(top.table)}
              onCommitField={(column, nextValue) =>
                void commitCellEdit(top.table, top.rowId, column.name, nextValue)
              }
            />
          ) : (
            <div className="record-editor__status">
              <Spinner label="Loading record" />
            </div>
          )}
          </section>

          {hasRelations && currentRow ? (
            <RecordEditorRelations
              currentRowId={top.rowId}
              currentRow={currentRow}
              sections={incomingRelationSections}
              snapshot={snapshot}
              onOpenRecord={(targetTable, rowId, row) =>
                void pushRecordEditor({ table: targetTable, rowId, row })
              }
              onViewAll={handleViewAllRelated}
            />
          ) : null}
        </div>

        <footer className="record-editor__actions">
          <div className="record-editor__actions-start">
            {currentRow ? (
              <button
                type="button"
                className="record-editor__pill record-editor__pill--danger"
                onClick={() => setConfirmDelete(true)}
              >
                Delete record
              </button>
            ) : null}
            {deleteError ? (
              <span className="record-editor__actions-error" role="alert">
                {deleteError}
              </span>
            ) : null}
          </div>
          <div className="record-editor__actions-end">
            <span className="record-editor__actions-hint">Changes save automatically</span>
            <button
              type="button"
              className="record-editor__pill"
              onClick={() => closeRecordEditor()}
            >
              Close
            </button>
          </div>
        </footer>
      </div>

      <ConfirmDialog
        open={confirmDelete}
        title={`Delete ${title}?`}
        description="You can undo this from the table right after."
        confirmLabel="Delete record"
        confirmVariant="danger"
        onConfirm={() => void handleDelete()}
        onCancel={() => setConfirmDelete(false)}
      />
    </div>
  );
}

type RecordEditorBreadcrumbsProps = {
  stack: ReadonlyArray<{
    table: string;
    rowId: string;
    row: Record<string, unknown> | null;
  }>;
  snapshot: ReturnType<typeof useSchemaStore.getState>["snapshot"];
  onJump: (index: number) => void;
};

/**
 * Renders the FK drill trail. Each crumb links back to an ancestor; the last
 * entry is rendered as plain text since it's already the active record.
 */
function RecordEditorBreadcrumbs({
  stack,
  snapshot,
  onJump,
}: RecordEditorBreadcrumbsProps) {
  return (
    <nav
      className="record-editor__breadcrumbs"
      aria-label="Record navigation history"
      data-testid="record-editor-breadcrumbs"
    >
      <ol>
        {stack.map((entry, index) => {
          const table =
            snapshot?.tables.find((t) => t.name === entry.table) ?? null;
          const label = table
            ? getRecordTitle(table, entry.row)
            : `#${entry.rowId}`;
          const isLast = index === stack.length - 1;
          return (
            <li key={`${entry.table}:${entry.rowId}:${index}`}>
              {isLast ? (
                <span className="record-editor__crumb record-editor__crumb--current">
                  {label}
                </span>
              ) : (
                <button
                  type="button"
                  className="record-editor__crumb"
                  onClick={() => onJump(index)}
                >
                  {label}
                </button>
              )}
              {!isLast ? (
                <span className="record-editor__crumb-sep" aria-hidden="true">
                  /
                </span>
              ) : null}
            </li>
          );
        })}
      </ol>
    </nav>
  );
}

type RecordEditorRelationsProps = {
  currentRowId: string;
  currentRow: Record<string, unknown>;
  sections: ReadonlyArray<IncomingRelationSection>;
  snapshot: ReturnType<typeof useSchemaStore.getState>["snapshot"];
  onOpenRecord: (
    targetTable: string,
    rowId: string,
    row?: Record<string, unknown>,
  ) => void;
  onViewAll: (section: IncomingRelationSection) => void;
};

function RecordEditorRelations({
  currentRowId,
  currentRow,
  sections,
  snapshot,
  onOpenRecord,
  onViewAll,
}: RecordEditorRelationsProps) {
  return (
    <section className="record-editor__side" aria-label="Related records">
      <div className="record-editor__relations">
        {sections.map((section) => {
          const relatedTable =
            snapshot?.tables.find((table) => table.name === section.targetTable) ?? null;
          if (!relatedTable) return null;

          return (
            <RecordEditorRelationSection
              key={`${section.targetTable}:${section.filterColumn}:${section.relationType ?? "1:N"}`}
              currentRowId={currentRowId}
              currentRow={currentRow}
              relatedTable={relatedTable}
              section={section}
              onOpenRecord={onOpenRecord}
              onViewAll={onViewAll}
            />
          );
        })}
      </div>
    </section>
  );
}

type RelationSectionLoadState = {
  status: "loading" | "ready" | "error";
  rows: Record<string, unknown>[];
  total: number | null;
  error: string | null;
  pendingNewRowIds: string[];
  editingCell: { rowId: string; columnName: string } | null;
  cellErrors: Record<string, Record<string, string>>;
};

type RecordEditorRelationSectionProps = {
  currentRowId: string;
  currentRow: Record<string, unknown>;
  relatedTable: ModelTable;
  section: IncomingRelationSection;
  onOpenRecord: (
    targetTable: string,
    rowId: string,
    row?: Record<string, unknown>,
  ) => void;
  onViewAll: (section: IncomingRelationSection) => void;
};

function RecordEditorRelationSection({
  currentRowId,
  currentRow,
  relatedTable,
  section,
  onOpenRecord,
  onViewAll,
}: RecordEditorRelationSectionProps) {
  const [state, setState] = useState<RelationSectionLoadState>({
    status: "loading",
    rows: [],
    total: null,
    error: null,
    pendingNewRowIds: [],
    editingCell: null,
    cellErrors: {},
  });

  useEffect(() => {
    let cancelled = false;

    setState({
      status: "loading",
      rows: [],
      total: null,
      error: null,
      pendingNewRowIds: [],
      editingCell: null,
      cellErrors: {},
    });

    void fetchTableRows(relatedTable.name, {
      ...DEFAULT_QUERY,
      perPage: 5,
      filters: [
        {
          column: section.filterColumn,
          op: "eq",
          value: currentRowId,
        },
      ],
    })
      .then((result) => {
        if (cancelled) return;
        setState({
          status: "ready",
          rows: result.data.map((row) => ensureRowId(relatedTable, row)),
          total: result.meta.total,
          error: null,
          pendingNewRowIds: [],
          editingCell: null,
          cellErrors: {},
        });
      })
      .catch((error) => {
        if (cancelled) return;
        setState({
          status: "error",
          rows: [],
          total: null,
          error: error instanceof Error ? error.message : "Failed to load related rows.",
          pendingNewRowIds: [],
          editingCell: null,
          cellErrors: {},
        });
      });

    return () => {
      cancelled = true;
    };
  }, [currentRowId, relatedTable.name, section.filterColumn]);

  const previewColumns = useMemo(
    () => pickRelationPreviewColumns(relatedTable, section.filterColumn),
    [relatedTable, section.filterColumn],
  );

  if (section.relationType === "N:M") {
    return (
      <ManyToManyRelationSection
        currentRowId={currentRowId}
        currentRow={currentRow}
        relatedTable={relatedTable}
        section={section}
        state={state}
        setState={setState}
        onOpenRecord={onOpenRecord}
      />
    );
  }

  return (
    <section
      className="record-editor__relation-section"
      aria-labelledby={`record-editor-relation-${relatedTable.name}-${section.filterColumn}`}
    >
      <header className="record-editor__relation-header">
        <div className="record-editor__relation-title-group">
          <h3
            id={`record-editor-relation-${relatedTable.name}-${section.filterColumn}`}
            className="record-editor__relation-title"
          >
            {section.label}
          </h3>
          <span className="record-editor__relation-count">
            {state.total ?? "…"}
          </span>
        </div>
        <Button
          type="button"
          variant="ghost"
          size="sm"
          onClick={() => onViewAll(section)}
        >
          View all
        </Button>
      </header>

      {state.status === "loading" ? (
        <div className="record-editor__relation-skeletons">
          <div className="record-editor__relation-skeleton" />
          <div className="record-editor__relation-skeleton" />
        </div>
      ) : state.status === "error" ? (
        <p
          className="record-editor__relation-error"
          role="alert"
          data-testid={`record-editor-relation-error-${relatedTable.name}`}
        >
          {state.error ?? "Failed to load related rows."}
        </p>
      ) : state.rows.length > 0 ? (
        <ol className="record-editor__relation-list">
          {state.rows.map((row) => {
            const rowId = String(row.id ?? "");
            const title = getRecordTitle(relatedTable, row);
            return (
              <li key={rowId}>
                <button
                  type="button"
                  className="record-editor__relation-row"
                  onClick={() => onOpenRecord(relatedTable.name, rowId)}
                >
                  <span className="record-editor__relation-row-title">{title}</span>
                  {previewColumns.length > 0 ? (
                    <span className="record-editor__relation-row-meta">
                      {previewColumns
                        .map((column) => {
                          const value = row[column.name];
                          if (value === null || value === undefined || value === "") {
                            return null;
                          }
                          return `${column.displayName ?? column.name}: ${String(value)}`;
                        })
                        .filter(Boolean)
                        .join(" • ")}
                    </span>
                  ) : null}
                </button>
              </li>
            );
          })}
        </ol>
      ) : null}
    </section>
  );
}

type ManyToManyRelationSectionProps = {
  currentRowId: string;
  currentRow: Record<string, unknown>;
  relatedTable: ModelTable;
  section: IncomingRelationSection;
  state: RelationSectionLoadState;
  setState: Dispatch<SetStateAction<RelationSectionLoadState>>;
  onOpenRecord: (
    targetTable: string,
    rowId: string,
    row?: Record<string, unknown>,
  ) => void;
};

/**
 * Many-to-many links, built for the editor's narrow side column: one row per
 * linked record (its name, plus any extra link fields underneath), "Link" to
 * add one through a search picker, a row opens to edit the link's own fields,
 * the arrow opens the linked record and the cross removes the link. No grid,
 * no horizontal scrolling.
 */
function ManyToManyRelationSection({
  currentRowId,
  relatedTable,
  section,
  state,
  setState,
  onOpenRecord,
}: ManyToManyRelationSectionProps) {
  const junction = relatedTable;
  const hiddenColumnName = section.hiddenColumn ?? section.filterColumn;
  const hiddenColumn = junction.columns.find((column) => column.name === hiddenColumnName);
  const linkColumn =
    junction.columns.find((column) => column.name === section.relatedColumn) ??
    junction.columns.find(
      (column) =>
        column.name !== hiddenColumnName &&
        (Boolean(column.references) || column.type === "relation"),
    ) ??
    null;
  const linkTargetTable = linkColumn ? getFkTargetTable(linkColumn) : null;
  const snapshotTables = useSchemaStore((store) => store.snapshot?.tables);
  const linkTargetModel = snapshotTables?.find((table) => table.name === linkTargetTable);
  const linkTargetLabel =
    linkTargetModel?.displayName ?? linkTargetModel?.name ?? linkColumn?.displayName ?? linkColumn?.name;
  const extraColumns = useMemo(
    () =>
      junction.columns.filter(
        (column) => column.name !== hiddenColumnName && column.name !== linkColumn?.name,
      ),
    [hiddenColumnName, junction.columns, linkColumn?.name],
  );
  // Fields a new link asks for. A junction's created_at is filled by the
  // database (every junction the server builds has it with a default).
  const draftExtraColumns = useMemo(
    () =>
      extraColumns.filter(
        (column) =>
          isWritableColumn(column) &&
          !(column.name === "created_at" && column.type === "datetime"),
      ),
    [extraColumns],
  );
  const extraTable = useMemo<ModelTable>(
    () => ({ ...junction, columns: extraColumns }),
    [extraColumns, junction],
  );
  // The current record's own id: the junction column points at it (reading
  // a same-named column of the record would link the wrong row).
  const hiddenValue = useMemo(
    () => coerceColumnValue(hiddenColumn ?? null, currentRowId),
    [currentRowId, hiddenColumn],
  );
  const [pendingRemoval, setPendingRemoval] = useState<Record<string, unknown> | null>(null);

  const [draft, setDraft] = useState<Record<string, unknown> | null>(null);
  const [expandedRowId, setExpandedRowId] = useState<string | null>(null);
  const draftRows = useMemo(
    () => (draft ? [...state.rows, draft] : state.rows),
    [draft, state.rows],
  );
  const labels = useReferenceLabels(junction, draftRows);
  const sectionId = `record-editor-relation-${junction.name}-${section.filterColumn}`;
  const draftRowId = "draft";

  function linkLabel(row: Record<string, unknown>): string {
    if (!linkColumn) return String(row.id ?? "");
    const value = row[linkColumn.name];
    return lookupReferenceLabel(labels, linkColumn, value) ?? String(value ?? "");
  }

  /** The record label's secondary value, then the link's own values. */
  function linkMeta(row: Record<string, unknown>): ReactNode {
    const secondary = linkColumn
      ? lookupReferenceSecondary(labels, linkColumn, row[linkColumn.name])
      : null;
    const parts: ReactNode[] = secondary
      ? [
          <span key="__secondary" className="record-editor__link-meta-item">
            {secondary}
          </span>,
        ]
      : [];
    const fieldParts = draftExtraColumns
      .map((column) => {
        const value = row[column.name];
        if (value === null || value === undefined || value === "") return null;
        return (
          <span key={column.name} className="record-editor__link-meta-item">
            {column.displayName ?? column.name} {renderCellValue(column, value)}
          </span>
        );
      })
      .filter(Boolean);
    parts.push(...fieldParts);
    return parts.length > 0 ? parts : null;
  }

  function setRowError(rowId: string, columnName: string, message: string | null) {
    setState((current) => {
      const rowErrors = { ...(current.cellErrors[rowId] ?? {}) };
      if (message) rowErrors[columnName] = message;
      else delete rowErrors[columnName];
      const cellErrors = { ...current.cellErrors };
      if (Object.keys(rowErrors).length > 0) cellErrors[rowId] = rowErrors;
      else delete cellErrors[rowId];
      return { ...current, cellErrors };
    });
  }

  /** The first required field the draft is still missing (link target first). */
  function nextMissingField(row: Record<string, unknown>): ModelColumn | null {
    const candidates = [linkColumn, ...draftExtraColumns].filter(
      (column): column is ModelColumn => Boolean(column) && isWritableColumn(column!),
    );
    return (
      candidates.find((column) => {
        const required =
          column.name === linkColumn?.name ||
          (column.nullable === false && column.default === undefined);
        const value = row[column.name];
        return required && (value === undefined || value === null || value === "");
      }) ?? null
    );
  }

  function startLink() {
    if (!linkColumn) return;
    setExpandedRowId(null);
    setDraft(buildDraftJunctionRow(junction, hiddenColumnName, hiddenValue));
    setState((current) => ({
      ...current,
      editingCell: { rowId: draftRowId, columnName: linkColumn.name },
    }));
  }

  function cancelDraft() {
    setDraft(null);
    setRowError(draftRowId, linkColumn?.name ?? "", null);
    setState((current) => ({ ...current, editingCell: null }));
  }

  async function commitDraftField(column: ModelColumn, value: unknown) {
    if (!draft) return;
    const next = { ...draft, [hiddenColumnName]: hiddenValue, [column.name]: value };
    setDraft(next);
    setRowError(draftRowId, column.name, null);
    const missing = nextMissingField(next);
    if (missing) {
      setState((current) => ({
        ...current,
        editingCell: missing ? { rowId: draftRowId, columnName: missing.name } : null,
      }));
      return;
    }
    try {
      const payload = buildJunctionPayload(junction, next);
      // Datetimes in the app are local wall time (what people type). The
      // database default for created_at would be UTC, so the link is stamped
      // here, the same way a typed value would be.
      const createdAtColumn = junction.columns.find(
        (candidate) => candidate.name === "created_at" && candidate.type === "datetime",
      );
      if (createdAtColumn && payload.created_at === undefined) {
        payload.created_at = localNowWallTime();
      }
      const created = ensureRowId(junction, await createRow(junction.name, payload));
      setDraft(null);
      setState((current) => ({
        ...current,
        rows: [...current.rows, created],
        total: (current.total ?? current.rows.length) + 1,
        editingCell: null,
      }));
    } catch (error) {
      setRowError(
        draftRowId,
        column.name,
        error instanceof ApiClientError && error.status === 409
          ? "Already linked."
          : error instanceof Error
            ? error.message
            : "Could not add the link.",
      );
      setState((current) => ({
        ...current,
        editingCell: { rowId: draftRowId, columnName: column.name },
      }));
    }
  }

  async function commitRowField(row: Record<string, unknown>, column: ModelColumn, value: unknown) {
    const rowId = String(row.id ?? "");
    try {
      const patched = ensureRowId(
        junction,
        await patchRow(junction.name, getPersistedRowId(junction, row, rowId), {
          [column.name]: value,
        }),
      );
      setRowError(rowId, column.name, null);
      setState((current) => ({
        ...current,
        rows: current.rows.map((candidate) =>
          String(candidate.id ?? "") === rowId ? patched : candidate,
        ),
        editingCell: null,
      }));
    } catch (error) {
      setRowError(
        rowId,
        column.name,
        error instanceof Error ? error.message : "Failed to save value.",
      );
    }
  }

  async function removeLink(row: Record<string, unknown>) {
    const rowId = String(row.id ?? "");
    try {
      await deleteRow(junction.name, getPersistedRowId(junction, row, rowId));
      if (expandedRowId === rowId) setExpandedRowId(null);
      setState((current) => ({
        ...current,
        error: null,
        rows: current.rows.filter((candidate) => String(candidate.id ?? "") !== rowId),
        total: Math.max(0, (current.total ?? current.rows.length) - 1),
      }));
    } catch (error) {
      setState((current) => ({
        ...current,
        error: error instanceof Error ? error.message : "Could not remove the link.",
      }));
    }
  }

  const hiddenRows = Math.max(0, (state.total ?? state.rows.length) - state.rows.length);

  return (
    <section
      className="record-editor__relation-section record-editor__relation-section--links"
      aria-labelledby={sectionId}
      data-testid={`record-editor-relation-links-${junction.name}`}
    >
      <header className="record-editor__relation-header">
        <div className="record-editor__relation-title-group">
          <h3 id={sectionId} className="record-editor__relation-title">
            {section.label}
          </h3>
          <span className="record-editor__relation-count">{state.total ?? "…"}</span>
        </div>
        {linkColumn ? (
          <Button
            type="button"
            variant="ghost"
            size="sm"
            leadingIcon={<Icon name="plus" size={13} />}
            aria-label={`Link ${section.label}`}
            disabled={draft !== null || state.status !== "ready"}
            onClick={startLink}
          >
            Link
          </Button>
        ) : null}
      </header>

      {state.status === "loading" ? (
        <div className="record-editor__relation-skeletons">
          <div className="record-editor__relation-skeleton" />
          <div className="record-editor__relation-skeleton" />
        </div>
      ) : state.status === "error" ? (
        <p
          className="record-editor__relation-error"
          role="alert"
          data-testid={`record-editor-relation-error-${junction.name}`}
        >
          {state.error ?? "Failed to load related rows."}
        </p>
      ) : (
        <>
          {state.error ? (
            <p className="record-editor__relation-error" role="alert">
              {state.error}
            </p>
          ) : null}
          {state.rows.length > 0 || draft ? (
            <ol className="record-editor__relation-list">
              {state.rows.map((row) => {
                const rowId = String(row.id ?? "");
                const label = linkLabel(row);
                const meta = linkMeta(row);
                const expanded = expandedRowId === rowId && extraColumns.length > 0;
                const targetId = linkColumn ? row[linkColumn.name] : null;
                return (
                  <li
                    key={rowId}
                    className={expanded ? "record-editor__link record-editor__link--open" : "record-editor__link"}
                  >
                    <div className="record-editor__link-row">
                      <button
                        type="button"
                        className="record-editor__relation-row record-editor__link-main"
                        aria-expanded={extraColumns.length > 0 ? expanded : undefined}
                        onClick={() => {
                          if (extraColumns.length === 0) {
                            if (linkTargetTable && targetId != null) {
                              onOpenRecord(linkTargetTable, String(targetId));
                            }
                            return;
                          }
                          setExpandedRowId(expanded ? null : rowId);
                          setState((current) => ({ ...current, editingCell: null }));
                        }}
                      >
                        <span className="record-editor__relation-row-title">{label}</span>
                        {meta ? <span className="record-editor__relation-row-meta">{meta}</span> : null}
                      </button>
                      {linkTargetTable && targetId != null ? (
                        <button
                          type="button"
                          className="record-editor__link-action"
                          aria-label={`Open ${label}`}
                          title={`Open ${label}`}
                          onClick={() => onOpenRecord(linkTargetTable, String(targetId))}
                        >
                          <Icon name="arrow-right" size={13} />
                        </button>
                      ) : null}
                      <button
                        type="button"
                        className="record-editor__link-action record-editor__link-action--remove"
                        aria-label={`Remove link to ${label}`}
                        title="Remove link"
                        onClick={() => {
                          // A link with its own values (amount, note, …) asks
                          // first: removing it deletes them too.
                          const holdsValues = draftExtraColumns.some((column) => {
                            const value = row[column.name];
                            return value !== null && value !== undefined && value !== "";
                          });
                          if (holdsValues) setPendingRemoval(row);
                          else void removeLink(row);
                        }}
                      >
                        <Icon name="x" size={13} />
                      </button>
                    </div>
                    {expanded ? (
                      <div className="record-editor__link-fields">
                        <RecordFieldList
                          table={extraTable}
                          row={row}
                          cellErrors={state.cellErrors[rowId] ?? EMPTY_CELL_ERRORS}
                          pendingCellSaves={EMPTY_PENDING_SAVES}
                          editingCell={state.editingCell}
                          referenceLabels={labels}
                          onStartEdit={(column) =>
                            setState((current) => ({
                              ...current,
                              editingCell: { rowId, columnName: column.name },
                            }))
                          }
                          onOpenReference={(column, value) => {
                            const target = getFkTargetTable(column);
                            if (target && value != null && value !== "") {
                              onOpenRecord(target, String(value));
                            }
                          }}
                          onCancelEdit={() =>
                            setState((current) => ({ ...current, editingCell: null }))
                          }
                          onCommitField={(column, value) => void commitRowField(row, column, value)}
                        />
                      </div>
                    ) : null}
                  </li>
                );
              })}
              {draft && linkColumn ? (
                <li className="record-editor__link record-editor__link--open record-editor__link--draft">
                  <div className="record-editor__link-fields">
                    <RecordFieldList
                      table={{
                        ...junction,
                        // The picker reads as the linked table, not as a key column.
                        columns: [
                          {
                            ...linkColumn,
                            primaryKey: false,
                            displayName: linkTargetLabel,
                          },
                          ...draftExtraColumns,
                        ],
                      }}
                      row={{ ...draft, id: draftRowId }}
                      cellErrors={state.cellErrors[draftRowId] ?? EMPTY_CELL_ERRORS}
                      pendingCellSaves={EMPTY_PENDING_SAVES}
                      editingCell={state.editingCell}
                      referenceLabels={labels}
                      onStartEdit={(column) =>
                        setState((current) => ({
                          ...current,
                          editingCell: { rowId: draftRowId, columnName: column.name },
                        }))
                      }
                      onOpenReference={() => undefined}
                      onCancelEdit={() => {
                        const chosen = draft[linkColumn.name];
                        if (chosen === null || chosen === undefined || chosen === "") {
                          cancelDraft();
                          return;
                        }
                        setState((current) => ({ ...current, editingCell: null }));
                      }}
                      onCommitField={(column, value) => void commitDraftField(column, value)}
                    />
                  </div>
                  <div className="record-editor__link-draft-actions">
                    <Button type="button" variant="ghost" size="sm" onClick={cancelDraft}>
                      Cancel
                    </Button>
                  </div>
                </li>
              ) : null}
            </ol>
          ) : (
            <p className="record-editor__relation-empty">No linked {section.label} yet.</p>
          )}
          {hiddenRows > 0 ? (
            <p className="record-editor__relation-more">+{hiddenRows} more</p>
          ) : null}
        </>
      )}
      <ConfirmDialog
        open={pendingRemoval !== null}
        title={`Remove link to ${pendingRemoval ? linkLabel(pendingRemoval) : ""}?`}
        description="The link's own values are deleted with it. The linked record stays."
        confirmLabel="Remove link"
        cancelLabel="Keep"
        confirmVariant="danger"
        onConfirm={() => {
          const row = pendingRemoval;
          setPendingRemoval(null);
          if (row) void removeLink(row);
        }}
        onCancel={() => setPendingRemoval(null)}
      />
    </section>
  );
}

function pickRelationPreviewColumns(
  table: ModelTable,
  filterColumn: string,
): ModelColumn[] {
  return table.columns
    .filter(
      (column) =>
        !column.primaryKey &&
        column.name !== filterColumn &&
        column.type !== "computed",
    )
    .slice(0, 2);
}

type RecordFieldListProps = {
  table: ModelTable;
  row: Record<string, unknown>;
  cellErrors: Record<string, string>;
  pendingCellSaves: string[];
  editingCell: { rowId: string; columnName: string } | null;
  referenceLabels: ReferenceLabelMap;
  onStartEdit: (column: ModelColumn) => void;
  onOpenReference: (column: ModelColumn, value: unknown) => void;
  onCancelEdit: () => void;
  onCommitField: (column: ModelColumn, nextValue: unknown) => void;
};

function RecordFieldList({
  table,
  row,
  cellErrors,
  pendingCellSaves,
  editingCell,
  referenceLabels,
  onStartEdit,
  onOpenReference,
  onCancelEdit,
  onCommitField,
}: RecordFieldListProps) {
  const rowId = String(row.id ?? "");
  return (
    <ol className="record-editor__fields">
      {table.columns.map((column) => {
        const writable = isWritableColumn(column);
        const value = row[column.name];
        const fieldId = `record-editor-field-${table.name}-${column.name}`;
        const pendingKey = `${rowId}:${column.name}`;
        const pending = pendingCellSaves.includes(pendingKey);
        const error = cellErrors[column.name] ?? null;
        const isEditing =
          editingCell?.rowId === rowId && editingCell.columnName === column.name;
        const isReference = Boolean(column.references) || column.type === "relation";
        const referenceLabel = isReference
          ? lookupReferenceLabel(referenceLabels, column, value)
          : null;
        const targetTable = isReference ? getFkTargetTable(column) : null;
        const hasReferenceValue =
          value !== null && value !== undefined && value !== "";

        return (
          <li
            key={column.name}
            className={[
              "record-editor__field",
              error ? "record-editor__field--error" : "",
              isEditing ? "record-editor__field--editing" : "",
            ]
              .filter(Boolean)
              .join(" ")}
          >
            <label className="record-editor__field-label" htmlFor={fieldId}>
              <span className="record-editor__field-name">
                {column.displayName ?? column.name}
              </span>
              {column.primaryKey ? (
                <span className="record-editor__field-tag">PK</span>
              ) : column.type === "computed" ? (
                <span className="record-editor__field-tag">Calculated</span>
              ) : null}
              {pending ? (
                <span className="record-editor__field-pending" aria-live="polite">
                  Saving…
                </span>
              ) : null}
            </label>
            {writable ? (
              isEditing ? (
                <RecordFieldEditor
                  column={column}
                  value={value}
                  error={error}
                  displayLabel={referenceLabel}
                  onCommit={(next) => onCommitField(column, next)}
                  onCancel={onCancelEdit}
                />
              ) : isReference && targetTable && hasReferenceValue ? (
                <div
                  id={fieldId}
                  role="button"
                  tabIndex={0}
                  className="record-editor__field-display record-editor__field-display--reference"
                  onClick={() => onStartEdit(column)}
                  onKeyDown={(event) => {
                    if (event.key === "Enter" || event.key === "F2") {
                      event.preventDefault();
                      onStartEdit(column);
                    }
                  }}
                >
                  <span className="record-editor__field-value">
                    <button
                      type="button"
                      className="data-grid__reference data-grid__reference--interactive"
                      title={referenceLabel ?? String(value)}
                      aria-label={`Open related ${referenceLabel ?? String(value)}`}
                      data-testid={`record-editor-reference-chip-${column.name}`}
                      onClick={(event) => {
                        event.preventDefault();
                        event.stopPropagation();
                        onOpenReference(column, value);
                      }}
                    >
                      {referenceLabel ?? String(value)}
                    </button>
                  </span>
                </div>
              ) : (
                <button
                  type="button"
                  id={fieldId}
                  className="record-editor__field-display"
                  onClick={() => onStartEdit(column)}
                  onKeyDown={(event) => {
                    if (event.key === "Enter" || event.key === "F2") {
                      event.preventDefault();
                      onStartEdit(column);
                    }
                  }}
                >
                  <span className="record-editor__field-value">
                    {isReference ? (
                      referenceLabel ? (
                        <span className="data-grid__reference" title={referenceLabel}>
                          {referenceLabel}
                        </span>
                      ) : (
                        renderCellValue(column, value)
                      )
                    ) : (
                      renderCellValue(column, value)
                    )}
                  </span>
                </button>
              )
            ) : (
              <div className="record-editor__field-readonly" id={fieldId}>
                {renderCellValue(column, value)}
              </div>
            )}
            {/* While editing, the editor itself shows the error. */}
            {error && !isEditing ? (
              <span className="record-editor__field-error" role="alert">
                {error}
              </span>
            ) : null}
          </li>
        );
      })}
    </ol>
  );
}

type RecordFieldEditorProps = {
  column: ModelColumn;
  value: unknown;
  error: string | null;
  displayLabel?: string | null;
  onCommit: (next: unknown) => void;
  onCancel: () => void;
};

function RecordFieldEditor({
  column,
  value,
  error,
  displayLabel,
  onCommit,
  onCancel,
}: RecordFieldEditorProps) {
  const Editor = pickEditorForColumn(column);
  return (
    <div className="record-editor__field-editor">
      <Editor
        value={value as never}
        column={column}
        autoFocus
        error={error}
        displayLabel={displayLabel}
        onCommit={(next: unknown) => onCommit(next)}
        onCancel={onCancel}
      />
    </div>
  );
}
