import { useRef, useState, type PointerEvent as ReactPointerEvent } from "react";
import { Icon } from "../../../components/common/Icon";
import { Button } from "../../../components/ui/Button";
import { EmptyState } from "../../../components/ui/EmptyState";
import type { SchemaModelDocument, SchemaTable } from "../../../types/api";
import {
  countLabel,
  formatMiniErdTypeValue,
} from "../schemaWorkbenchHelpers";
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
  getErdDragPath,
  getErdDropTargetTableName,
  getErdRelationHandlePoint,
  getNextVisualTableLayout,
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
                      aria-label={`Select table ${table.name}`}
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
