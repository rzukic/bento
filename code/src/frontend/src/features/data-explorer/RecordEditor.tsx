import { useEffect, useMemo, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { buildQueryString, fetchTableRows } from "../../api/data";
import { Icon } from "../../components/common/Icon";
import { Button } from "../../components/ui/Button";
import { Spinner } from "../../components/ui/Spinner";
import { useExplorerStore } from "../../stores/explorerStore";
import { useSchemaStore } from "../../stores/schemaStore";
import { DEFAULT_QUERY } from "../../types/explorer";
import type { ModelColumn, ModelTable } from "../../types/api";
import {
  BooleanCellEditor,
  DateCellEditor,
  DateTimeCellEditor,
  EnumCellEditor,
  FloatCellEditor,
  IntegerCellEditor,
  JsonCellEditor,
  ReferenceCellEditor,
  TextCellEditor,
} from "./components/editors";
import { renderCellValue } from "./components/cellValue";
import {
  lookupReferenceLabel,
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

function pickEditorForColumn(column: ModelColumn) {
  if (column.references || column.type === "relation") return ReferenceCellEditor;
  switch (column.type) {
    case "boolean":
      return BooleanCellEditor;
    case "date":
      return DateCellEditor;
    case "datetime":
      return DateTimeCellEditor;
    case "enum":
      return EnumCellEditor;
    case "json":
      return JsonCellEditor;
    case "double":
    case "float":
      return FloatCellEditor;
    case "int64":
    case "integer":
    case "number":
      return IntegerCellEditor;
    default:
      return TextCellEditor;
  }
}

function isWritableColumn(column: ModelColumn): boolean {
  return !column.primaryKey && column.type !== "computed";
}

function getRecordTitle(table: ModelTable, row: Record<string, unknown> | null): string {
  if (!row) return table.displayName ?? table.name;
  const id = row.id;
  const label = table.columns.find(
    (column) => !column.primaryKey && column.type === "string",
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

export function RecordEditor() {
  const navigate = useNavigate();
  const editorState = useExplorerStore((state) => state.recordEditor);
  const closeRecordEditor = useExplorerStore((state) => state.closeRecordEditor);
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
  const liveRow = useMemo(() => {
    if (!top || !view) return null;
    return view.rows.find((row) => String(row.id ?? "") === top.rowId) ?? null;
  }, [top, view]);
  const currentRow = liveRow ?? top?.row ?? null;
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

  const titleRow = liveRow ?? top.row;
  const title = table ? getRecordTitle(table, titleRow) : `Record #${top.rowId}`;
  const topRowId = top.rowId;
  const topTableName = top.table;
  const showBack = depth > 1;

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
              {table?.displayName ?? table?.name ?? top.table}
            </span>
            <h2 id="record-editor-title" className="record-editor__title">
              {title}
            </h2>
          </div>
          <div className="record-editor__header-actions">
            <Button
              type="button"
              variant="ghost"
              size="sm"
              onClick={handleGoToTable}
            >
              Go to table
            </Button>
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

        <div className="record-editor__body">
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
        </div>

        {table && currentRow && incomingRelationSections.length > 0 ? (
          <RecordEditorRelations
            currentRowId={top.rowId}
            sections={incomingRelationSections}
            snapshot={snapshot}
            onOpenRecord={(targetTable, rowId) =>
              void pushRecordEditor({ table: targetTable, rowId })
            }
            onViewAll={handleViewAllRelated}
          />
        ) : null}
      </div>
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
  sections: ReadonlyArray<IncomingRelationSection>;
  snapshot: ReturnType<typeof useSchemaStore.getState>["snapshot"];
  onOpenRecord: (targetTable: string, rowId: string) => void;
  onViewAll: (section: IncomingRelationSection) => void;
};

function RecordEditorRelations({
  currentRowId,
  sections,
  snapshot,
  onOpenRecord,
  onViewAll,
}: RecordEditorRelationsProps) {
  return (
    <footer className="record-editor__footer record-editor__footer--relations">
      <div className="record-editor__relations">
        {sections.map((section) => {
          const relatedTable =
            snapshot?.tables.find((table) => table.name === section.targetTable) ?? null;
          if (!relatedTable) return null;

          return (
            <RecordEditorRelationSection
              key={`${section.targetTable}:${section.filterColumn}`}
              currentRowId={currentRowId}
              relatedTable={relatedTable}
              section={section}
              onOpenRecord={onOpenRecord}
              onViewAll={onViewAll}
            />
          );
        })}
      </div>
    </footer>
  );
}

type RelationSectionLoadState = {
  status: "loading" | "ready" | "error";
  rows: Record<string, unknown>[];
  total: number | null;
  error: string | null;
};

type RecordEditorRelationSectionProps = {
  currentRowId: string;
  relatedTable: ModelTable;
  section: IncomingRelationSection;
  onOpenRecord: (targetTable: string, rowId: string) => void;
  onViewAll: (section: IncomingRelationSection) => void;
};

function RecordEditorRelationSection({
  currentRowId,
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
  });

  useEffect(() => {
    let cancelled = false;

    setState({
      status: "loading",
      rows: [],
      total: null,
      error: null,
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
          rows: result.data,
          total: result.meta.total,
          error: null,
        });
      })
      .catch((error) => {
        if (cancelled) return;
        setState({
          status: "error",
          rows: [],
          total: null,
          error: error instanceof Error ? error.message : "Failed to load related rows.",
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
                <span className="record-editor__field-tag">computed</span>
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
            {error ? (
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
  onCommit: (next: unknown) => void;
  onCancel: () => void;
};

function RecordFieldEditor({
  column,
  value,
  error,
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
        onCommit={(next: unknown) => onCommit(next)}
        onCancel={onCancel}
      />
    </div>
  );
}
