import {
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type PointerEvent as ReactPointerEvent,
} from "react";
import { Icon } from "../../../components/common/Icon";
import { Button } from "../../../components/ui/Button";
import { EmptyState } from "../../../components/ui/EmptyState";
import type { SchemaModelDocument, SchemaTable } from "../../../types/api";
import { formatMiniErdTypeValue } from "../schemaWorkbenchHelpers";
import type { VisualCanvasSelection, WorkbenchDiagnostic } from "../types";
import {
  createOneToManyRelation,
  createStarterTable,
  getTableLayout,
  setTableLayout,
  type VisualSchemaDraftIssue,
} from "../visualSchemaDraft";
import {
  buildErdRelationEdges,
  buildErdTableNodes,
  getErdCanvasSize,
  getErdContentBounds,
  getErdDragPath,
  getErdDropTargetTableName,
  getErdRelationHandlePoint,
  getNextVisualTableLayout,
  smoothErdPath,
  snapToErdGrid,
  type ErdRelationDragState,
  type ErdRelationHandleSide,
  type ErdTableNode,
} from "./erdRouting";
import { createVisualRelationDraft } from "./visualDraftHelpers";
import { VisualPreviewDiagnostics } from "./VisualPreviewDiagnostics";

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

type VisualCanvasViewport = {
  x: number;
  y: number;
  zoom: number;
};

const MIN_ZOOM = 0.5;
const MAX_ZOOM = 1.6;
const FIT_PADDING = 56;
/** Dot colours for tables without a colour of their own (by position). */
const TABLE_DOTS = ["#60a5fa", "#2dd4bf", "#818cf8", "#fbbf24", "#f472b6", "#a3e635", "#fb923c", "#38bdf8"];

function clampZoom(zoom: number): number {
  return Math.max(MIN_ZOOM, Math.min(MAX_ZOOM, zoom));
}

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

/** The last canvas view in this browser tab, kept across remounts. */
let rememberedViewport: VisualCanvasViewport | null = null;

/** Tests start every render from a fresh canvas. */
export function resetRememberedCanvasViewport() {
  rememberedViewport = null;
}

export function VisualSchemaMode({
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
  const [issuesCollapsed, setIssuesCollapsed] = useState(false);
  const issueCount = issues.length + backendDiagnostics.length;
  const [viewport, setViewport] = useState<VisualCanvasViewport>(
    () => rememberedViewport ?? { x: 0, y: 0, zoom: 1 },
  );
  // Apply and Refresh reload the schema and remount this canvas; keep the
  // view the person was looking at instead of re-fitting.
  useEffect(() => {
    rememberedViewport = viewport;
  }, [viewport]);
  const [relationDrag, setRelationDrag] = useState<ErdRelationDragState | null>(
    null,
  );
  const canvasRef = useRef<HTMLDivElement | null>(null);
  const relationDragRef = useRef<ErdRelationDragState | null>(null);
  const canvasPanRef = useRef<ErdCanvasPanState | null>(null);
  const tableDragRef = useRef<ErdTableDragState | null>(null);
  const panMovedRef = useRef(false);

  // Node geometry and relation routing depend only on the draft document.
  // Pan/zoom/drag state lives in this component too, so without memoisation
  // every pointer move re-routed every relation line.
  const geometry = useMemo(() => {
    if (!document) {
      return null;
    }
    const nodes = buildErdTableNodes(document);
    return {
      nodes,
      canvasSize: getErdCanvasSize(nodes),
      edges: buildErdRelationEdges(document, nodes),
    };
  }, [document]);

  // The viewport element and the view that fits every table into it.
  const viewportRef = useRef<HTMLDivElement | null>(null);
  const autoFitDone = useRef(rememberedViewport !== null);
  const computeFit = (): VisualCanvasViewport | null => {
    const element = viewportRef.current;
    const bounds = geometry ? getErdContentBounds(geometry.nodes) : null;
    if (!element || !bounds || element.clientWidth === 0 || element.clientHeight === 0) {
      return null;
    }
    const zoom = clampZoom(
      Math.min(
        1,
        (element.clientWidth - FIT_PADDING * 2) / bounds.width,
        (element.clientHeight - FIT_PADDING * 2) / bounds.height,
      ),
    );
    return {
      zoom,
      x: (element.clientWidth - bounds.width * zoom) / 2 - bounds.x * zoom,
      y: (element.clientHeight - bounds.height * zoom) / 2 - bounds.y * zoom,
    };
  };

  // Open with every table in view.
  const hasTables = (geometry?.nodes.length ?? 0) > 0;
  useLayoutEffect(() => {
    if (autoFitDone.current || !hasTables) return;
    const fitted = computeFit();
    if (fitted) {
      autoFitDone.current = true;
      setViewport(fitted);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [hasTables, syntaxInvalid]);

  // Wheel / two-finger scroll pans; with ⌘/Ctrl (and trackpad pinch, which
  // arrives as ctrl+wheel) it zooms around the pointer.
  useEffect(() => {
    const element = viewportRef.current;
    if (!element) return;
    function onWheel(event: WheelEvent) {
      event.preventDefault();
      if (event.ctrlKey || event.metaKey) {
        const rect = element!.getBoundingClientRect();
        const cx = event.clientX - rect.left;
        const cy = event.clientY - rect.top;
        setViewport((current) => {
          // ~20 % per mouse-wheel notch (deltaY ≈ 100), smooth for trackpad pinch.
          const zoom = clampZoom(current.zoom * Math.exp(-event.deltaY * 0.0022));
          const ratio = zoom / current.zoom;
          return { zoom, x: cx - (cx - current.x) * ratio, y: cy - (cy - current.y) * ratio };
        });
        return;
      }
      setViewport((current) => ({ ...current, x: current.x - event.deltaX, y: current.y - event.deltaY }));
    }
    element.addEventListener("wheel", onWheel, { passive: false });
    return () => element.removeEventListener("wheel", onWheel);
  }, [hasTables, syntaxInvalid]);

  if (syntaxInvalid) {
    return (
      <div className="schema-workbench__visual-shell">
        <EmptyState
          title="Fix JSON syntax to use Visual mode"
          description="Fix the JSON errors to use the visual designer."
          icon="schema"
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
          description="The schema needs a tables list and a relations list."
          icon="schema"
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
  // `geometry` is non-null whenever `document` is; both guards above returned already.
  const { nodes, canvasSize, edges } = geometry!;
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
        ".schema-workbench__erd-table, .schema-workbench__erd-relation-button, .schema-workbench__erd-relation-handle, .schema-workbench__erd-relation-hit, .schema-workbench__erd-controls, .schema-workbench__erd-add-table",
      )
    ) {
      return;
    }

    event.preventDefault();
    event.currentTarget.setPointerCapture?.(event.pointerId);
    panMovedRef.current = false;

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

      if (
        Math.abs(moveEvent.clientX - currentPan.startClientX) +
          Math.abs(moveEvent.clientY - currentPan.startClientY) >
        3
      ) {
        panMovedRef.current = true;
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

    function handlePointerUp() {
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

    function handlePointerUp() {
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
      zoom: clampZoom(update.zoom ?? current.zoom),
    }));
  }

  function fitCanvas() {
    // Without a measured viewport (e.g. before layout) fall back to a fixed
    // overview zoom.
    setViewport(
      computeFit() ?? {
        x: 12,
        y: 12,
        zoom: visualDocument.tables.length > 3 ? 0.72 : 0.86,
      },
    );
  }

  return (
    <div
      className="schema-workbench__visual-shell"
      data-testid="schema-visual-mode"
      aria-label="Visual schema draft"
    >
      <div className="schema-workbench__erd-shell">
        {issueCount > 0 ? (
          <div
            className={
              issuesCollapsed
                ? "schema-workbench__canvas-issues schema-workbench__canvas-issues--collapsed"
                : "schema-workbench__canvas-issues"
            }
          >
            <button
              type="button"
              className="schema-workbench__canvas-issues-toggle"
              aria-expanded={!issuesCollapsed}
              onClick={() => setIssuesCollapsed((current) => !current)}
            >
              <span
                className={
                  issues.length > 0 || backendDiagnostics.some((diagnostic) => diagnostic.severity !== "warning")
                    ? "schema-workbench__issue-dot schema-workbench__issue-dot--error"
                    : "schema-workbench__issue-dot schema-workbench__issue-dot--warning"
                }
                aria-hidden="true"
              />
              {issueCount === 1 ? "1 issue" : `${issueCount} issues`}
              <Icon name={issuesCollapsed ? "chevron-down" : "chevron-up"} size={12} />
            </button>
            {issuesCollapsed ? null : (
              <>
                {issues.length > 0 ? (
                  <section
                    className="schema-workbench__visual-issues"
                    aria-label="Problems to fix"
                  >
                    <h3>Problems to fix</h3>
                    <ol>
                      {issues.slice(0, 5).map((issue) => (
                        <li key={`${issue.code}:${issue.path}`}>
                          <span
                            className="schema-workbench__issue-dot schema-workbench__issue-dot--error"
                            aria-hidden="true"
                          />
                          <span>{issue.message}</span>
                        </li>
                      ))}
                    </ol>
                    {issues.length > 5 ? <p>+{issues.length - 5} more</p> : null}
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
              </>
            )}
          </div>
        ) : null}
        <div
          ref={viewportRef}
          className="schema-workbench__erd-viewport"
          data-testid="schema-erd-canvas"
          style={{
            // The dot grid is part of the canvas: it pans and zooms with it.
            backgroundSize: `${24 * viewport.zoom}px ${24 * viewport.zoom}px`,
            backgroundPosition: `${viewport.x}px ${viewport.y}px`,
          }}
          onPointerDown={startCanvasPan}
          onClick={(event) => {
            // A click on empty canvas (not the end of a pan) clears the
            // selection, which closes the inspector.
            const target = event.target instanceof Element ? event.target : null;
            if (panMovedRef.current) return;
            if (
              target?.closest(
                ".schema-workbench__erd-table, .schema-workbench__erd-relation-button, .schema-workbench__erd-relation-hit, .schema-workbench__erd-controls, .schema-workbench__erd-add-table",
              )
            ) {
              return;
            }
            onSelectionChange(null);
          }}
        >
          <div
            className="schema-workbench__erd-controls"
            role="toolbar"
            aria-label="ERD canvas controls"
          >
            <button
              type="button"
              className="schema-workbench__erd-tool"
              aria-label="Zoom out"
              title="Zoom out"
              disabled={visualDocument.tables.length === 0}
              onClick={() => updateViewport({ zoom: viewport.zoom - 0.1 })}
            >
              <Icon name="minus" size={13} />
            </button>
            <span
              className="schema-workbench__erd-zoom"
              aria-label={`Canvas zoom ${zoomPercent}%`}
            >
              {zoomPercent}%
            </span>
            <button
              type="button"
              className="schema-workbench__erd-tool"
              aria-label="Zoom in"
              title="Zoom in"
              disabled={visualDocument.tables.length === 0}
              onClick={() => updateViewport({ zoom: viewport.zoom + 0.1 })}
            >
              <Icon name="plus" size={13} />
            </button>
            <button
              type="button"
              className="schema-workbench__erd-tool schema-workbench__erd-tool--text"
              aria-label="Fit canvas"
              title="Fit all tables in view"
              disabled={visualDocument.tables.length === 0}
              onClick={fitCanvas}
            >
              Fit
            </button>
          </div>
          <button
            type="button"
            className="schema-workbench__erd-add-table"
            onClick={addNewTable}
          >
            <Icon name="plus" size={13} />
            Add table
          </button>
          {visualDocument.tables.length === 0 ? (
            <EmptyState
              title="No tables in this draft"
              description="Add a table to start designing."
              icon="schema"
            />
          ) : (
            <div
              ref={canvasRef}
              className="schema-workbench__erd-canvas"
              data-testid="schema-erd-canvas-content"
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
                        className={[
                          "schema-workbench__erd-relation-path",
                          edge.relation.type === "N:M" ? "schema-workbench__erd-relation-path--many" : "",
                          isSelected ? "schema-workbench__erd-relation-path--selected" : "",
                        ]
                          .filter(Boolean)
                          .join(" ")}
                        d={smoothErdPath(edge.path)}
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
                    d={smoothErdPath(getErdDragPath(relationDrag))}
                  />
                ) : null}
              </svg>

              {edges.map((edge) => (
                <button
                  key={edge.id}
                  type="button"
                  className={[
                    "schema-workbench__erd-relation-button",
                    edge.relation.type === "N:M" ? "schema-workbench__erd-relation-button--many" : "",
                    selection?.type === "relation" && selection.relationName === edge.relation.name
                      ? "schema-workbench__erd-relation-button--selected"
                      : "",
                  ]
                    .filter(Boolean)
                    .join(" ")}
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

              {nodes.map((node, nodeIndex) => {
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
                      aria-label={`Select table ${table.name}`}
                      onPointerDown={(event) => startTableDrag(event, table)}
                      onClick={() =>
                        onSelectionChange({
                          type: "table",
                          tableName: table.name,
                        })
                      }
                    >
                      <span
                        className="schema-workbench__erd-table-dot"
                        aria-hidden="true"
                        style={{ background: layout?.color ?? TABLE_DOTS[nodeIndex % TABLE_DOTS.length] }}
                      />
                      <span className="schema-workbench__erd-table-title">
                        {table.displayName || table.name}
                      </span>
                      <span className="schema-workbench__erd-table-count" aria-label={`${table.columns.length} columns`}>
                        {table.columns.length}
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
