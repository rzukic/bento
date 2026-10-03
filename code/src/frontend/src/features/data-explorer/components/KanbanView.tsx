import { useMemo, useRef, useState } from "react";
import { Icon } from "../../../components/common/Icon";
import type { ModelColumn, ModelTable } from "../../../types/api";
import { apiClient } from "../../../api/client";
import { renderCellValue } from "./cellValue";
import { lookupReferenceLabel, useReferenceLabels } from "./useReferenceLabels";

type KanbanViewProps = {
  table: ModelTable;
  rows: Record<string, unknown>[];
  loading?: boolean;
  kanbanColumn: string | null;
  cardFieldNames: string[];
  hiddenLaneValues?: string[];
  onConfigureView?: () => void;
  onNavigateReference?: (column: ModelColumn, value: unknown, row: Record<string, unknown>) => void;
  onOpenRecord?: (row: Record<string, unknown>) => void;
};

const UNASSIGNED_LANE_ID = "__kanban_unassigned__";
const MAX_SECONDARY_FIELDS = 3;

type Lane = {
  id: string;
  label: string;
  rows: Record<string, unknown>[];
};

export function KanbanView({
  table,
  rows,
  loading = false,
  kanbanColumn,
  cardFieldNames,
  hiddenLaneValues = [],
  onConfigureView,
  onNavigateReference,
  onOpenRecord,
}: KanbanViewProps) {
  const referenceLabels = useReferenceLabels(table, rows);
  const [overrides, setOverrides] = useState<Record<string, string>>({});
  const [saving, setSaving] = useState<Set<string>>(new Set());
  const [dragError, setDragError] = useState<{ rowId: string; msg: string } | null>(null);
  const [dragOverLane, setDragOverLane] = useState<string | null>(null);
  const [draggingRowId, setDraggingRowId] = useState<string | null>(null);
  const dragRef = useRef<{ rowId: string; sourceLane: string } | null>(null);

  const groupingColumn = useMemo<ModelColumn | null>(() => {
    if (!kanbanColumn) return null;
    return table.columns.find((c) => c.name === kanbanColumn) ?? null;
  }, [table.columns, kanbanColumn]);

  // Card columns: configured fields, kanban column excluded (it's the lane header).
  const cardColumns = useMemo<ModelColumn[]>(() => {
    return cardFieldNames
      .filter((name) => name !== kanbanColumn)
      .map((name) => table.columns.find((c) => c.name === name) ?? null)
      .filter((c): c is ModelColumn => c !== null);
  }, [cardFieldNames, kanbanColumn, table.columns]);

  // First configured field is the bold primary title; fall back to first non-PK non-kanban column.
  const primaryColumn = useMemo<ModelColumn | null>(() => {
    if (cardColumns.length > 0) return cardColumns[0];
    return (
      table.columns.find((c) => !c.primaryKey && c.name !== kanbanColumn) ??
      table.columns[0] ??
      null
    );
  }, [cardColumns, kanbanColumn, table.columns]);

  const secondaryColumns = useMemo<ModelColumn[]>(() => {
    return cardColumns.slice(1, MAX_SECONDARY_FIELDS + 1);
  }, [cardColumns]);

  const lanes = useMemo<Lane[]>(() => {
    if (!groupingColumn) return [];
    const declaredValues = groupingColumn.values ?? [];
    const laneMap = new Map<string, Lane>();
    for (const value of declaredValues) {
      laneMap.set(value, { id: value, label: value, rows: [] });
    }
    const unassigned: Lane = { id: UNASSIGNED_LANE_ID, label: "Unassigned", rows: [] };
    for (const row of rows) {
      const id = String(row.id ?? "");
      const raw = overrides[id] !== undefined ? overrides[id] : row[groupingColumn.name];
      const key = raw === null || raw === undefined || raw === "" ? null : String(raw);
      if (key !== null && laneMap.has(key)) {
        laneMap.get(key)!.rows.push(row);
      } else {
        unassigned.rows.push(row);
      }
    }
    const result = Array.from(laneMap.values()).filter(
      (lane) => !hiddenLaneValues.includes(lane.id),
    );
    if (unassigned.rows.length > 0 && !hiddenLaneValues.includes(UNASSIGNED_LANE_ID)) {
      result.push(unassigned);
    }
    return result;
  }, [groupingColumn, rows, hiddenLaneValues, overrides]);

  function handleDragStart(
    e: React.DragEvent<HTMLElement>,
    rowId: string,
    laneId: string,
  ) {
    // A reference chip inside the card starts no card drag.
    if ((e.target as HTMLElement).closest?.("button,a")) {
      e.preventDefault();
      return;
    }
    if (e.dataTransfer) {
      e.dataTransfer.effectAllowed = "move";
      e.dataTransfer.setData("text/plain", rowId);
    }
    dragRef.current = { rowId, sourceLane: laneId };
    setDraggingRowId(rowId);
  }

  function handleDragEnd() {
    dragRef.current = null;
    setDragOverLane(null);
    setDraggingRowId(null);
  }

  function handleDragOver(e: React.DragEvent<HTMLDivElement>, laneId: string) {
    e.preventDefault();
    if (e.dataTransfer) e.dataTransfer.dropEffect = "move";
    if (dragOverLane !== laneId) setDragOverLane(laneId);
  }

  function handleDragLeave(e: React.DragEvent<HTMLDivElement>) {
    // Only clear when leaving the lane list itself, not a child element
    if (!e.currentTarget.contains(e.relatedTarget as Node)) {
      setDragOverLane(null);
    }
  }

  async function handleDrop(e: React.DragEvent<HTMLDivElement>, targetLaneId: string) {
    e.preventDefault();
    setDragOverLane(null);
    // The card remounts in its new lane, so the dragend of the old element
    // never arrives; clear the dragging look here or the card stays faded.
    setDraggingRowId(null);
    const drag = dragRef.current;
    dragRef.current = null;
    if (!drag || drag.sourceLane === targetLaneId || targetLaneId === UNASSIGNED_LANE_ID) return;

    const { rowId, sourceLane } = drag;

    setOverrides((prev) => ({ ...prev, [rowId]: targetLaneId }));
    setSaving((prev) => new Set(prev).add(rowId));
    setDragError(null);

    try {
      await apiClient.patch(`/data/${table.name}/${rowId}`, {
        [kanbanColumn!]: targetLaneId,
      });
    } catch {
      setOverrides((prev) => {
        const next = { ...prev };
        // Restore: use the source lane as override so it snaps back correctly
        next[rowId] = sourceLane;
        // Clear override after a tick so the row returns to its original backend value
        setTimeout(() => setOverrides((p) => { const n = { ...p }; delete n[rowId]; return n; }), 0);
        return next;
      });
      setDragError({ rowId, msg: "Couldn't save this change. Try again." });
    } finally {
      setSaving((prev) => {
        const next = new Set(prev);
        next.delete(rowId);
        return next;
      });
    }
  }

  // Setup prompt: no grouping column configured.
  if (!kanbanColumn || !groupingColumn) {
    return (
      <div className="kanban-empty" role="status">
        <div className="kanban-empty__icon">
          <Icon name="layers" size={28} />
        </div>
        <h3 className="kanban-empty__title">Kanban view not configured</h3>
        <p className="kanban-empty__description">
          Pick a choice column to group cards into lanes.
        </p>
        {onConfigureView ? (
          <button type="button" className="kanban-empty__action" onClick={onConfigureView}>
            Configure view
          </button>
        ) : null}
      </div>
    );
  }

  // Loading skeleton state.
  if (loading && rows.length === 0) {
    return (
      <div className="kanban-board" aria-busy="true">
        {(groupingColumn.values ?? ["", "", ""]).map((value, index) => (
          <div key={value || index} className="kanban-lane kanban-lane--skeleton">
            <div className="kanban-lane__header">
              <span className="kanban-lane__label">&nbsp;</span>
              <span className="kanban-lane__count">&nbsp;</span>
            </div>
            <div className="kanban-lane__list">
              {[0, 1, 2].map((n) => (
                <div key={n} className="kanban-card kanban-card--skeleton" />
              ))}
            </div>
          </div>
        ))}
      </div>
    );
  }

  // No rows at all.
  if (rows.length === 0) {
    return (
      <div className="kanban-empty" role="status">
        <div className="kanban-empty__icon">
          <Icon name="database" size={28} />
        </div>
        <h3 className="kanban-empty__title">No records match the current filters</h3>
        <p className="kanban-empty__description">
          Adjust the search or filters to bring records into the board.
        </p>
      </div>
    );
  }

  return (
    <div className="kanban-board" role="list" aria-label="Kanban lanes">
      {dragError ? (
        <div className="kanban-drag-error" role="alert">
          <span>{dragError.msg}</span>
          <button
            type="button"
            className="kanban-drag-error__dismiss"
            onClick={() => setDragError(null)}
            aria-label="Dismiss"
          >
            <Icon name="x" size={13} />
          </button>
        </div>
      ) : null}
      {lanes.map((lane) => (
        <div
          key={lane.id}
          className={[
            "kanban-lane",
            dragOverLane === lane.id ? "kanban-lane--drag-over" : "",
          ].filter(Boolean).join(" ")}
          role="listitem"
          aria-label={`${lane.label} lane`}
          onDragOver={(e) => handleDragOver(e, lane.id)}
          onDragLeave={handleDragLeave}
          onDrop={(e) => void handleDrop(e, lane.id)}
        >
          <div className="kanban-lane__header">
            <span className="kanban-lane__label">
              {groupingColumn && lane.id !== UNASSIGNED_LANE_ID
                ? renderCellValue(groupingColumn, lane.label)
                : lane.label}
            </span>
            <span className="kanban-lane__count" aria-label={`${lane.rows.length} cards`}>
              {lane.rows.length}
            </span>
          </div>
          <div className="kanban-lane__list">
            {lane.rows.length === 0 ? (
              <div className="kanban-lane__empty">No entries</div>
            ) : (
              lane.rows.map((row, index) => {
                const rowId = String(row.id ?? index);
                const isSaving = saving.has(rowId);
                const allFields = primaryColumn
                  ? [primaryColumn, ...secondaryColumns]
                  : secondaryColumns;
                return (
                  <article
                    key={rowId}
                    className={[
                      "kanban-card",
                      isSaving ? "kanban-card--saving" : "",
                      onOpenRecord ? "kanban-card--interactive" : "",
                      draggingRowId === rowId ? "kanban-card--dragging" : "",
                    ]
                      .filter(Boolean)
                      .join(" ")}
                    draggable
                    onDragStart={(e) => handleDragStart(e, rowId, lane.id)}
                    onDragEnd={handleDragEnd}
                    tabIndex={0}
                    role={onOpenRecord ? "button" : undefined}
                    aria-label={onOpenRecord ? `Open record ${rowId}` : undefined}
                    onClick={(e) => {
                      if (!onOpenRecord) return;
                      // Ignore clicks that originated in the drag handle or any
                      // nested interactive control (FK reference buttons, etc.);
                      // those already call stopPropagation on their own handlers
                      // but we guard defensively for future additions.
                      const target = e.target as HTMLElement | null;
                      if (target?.closest("button,a,input,select,textarea")) return;
                      onOpenRecord(row);
                    }}
                    onKeyDown={(e) => {
                      if (!onOpenRecord) return;
                      if (e.key === "Enter" || e.key === " ") {
                        e.preventDefault();
                        onOpenRecord(row);
                      }
                    }}
                  >
                    <div className="kanban-card__body">
                      {allFields.length > 0 ? (
                        <dl className="kanban-card__fields">
                          {allFields.map((col, i) => (
                            <div
                              key={col.name}
                              className={[
                                "kanban-card__field",
                                i === 0 ? "kanban-card__field--primary" : "",
                                col.type === "relation" || col.references ? "kanban-card__field--reference" : "",
                              ].filter(Boolean).join(" ")}
                            >
                              {/* The headline value needs no label on screen;
                                  the other fields show a small one. */}
                              <dt className="kanban-card__field-label">
                                {col.displayName ?? col.name}
                              </dt>
                              <dd className="kanban-card__field-value">
                                {(() => {
                                  const isReferenceValue =
                                    (col.type === "relation" || col.references) &&
                                    row[col.name] !== null &&
                                    row[col.name] !== undefined &&
                                    row[col.name] !== "";
                                  if (!isReferenceValue) {
                                    return renderCellValue(col, row[col.name]);
                                  }

                                  const rawId = String(row[col.name]);
                                  const label =
                                    lookupReferenceLabel(referenceLabels, col, row[col.name]) ?? rawId;

                                  if (onNavigateReference) {
                                    return (
                                      <button
                                        type="button"
                                        className="data-grid__reference data-grid__reference--interactive"
                                        aria-label={`Open related ${label}`}
                                        title={`Open related ${label}`}
                                        onClick={() => onNavigateReference(col, row[col.name], row)}
                                      >
                                        {label}
                                      </button>
                                    );
                                  }

                                  return (
                                    <span className="data-grid__reference" title={label}>
                                      {label}
                                    </span>
                                  );
                                })()}
                              </dd>
                              {i === 0 ? (
                                <span className="kanban-card__id" aria-hidden="true">
                                  #{rowId}
                                </span>
                              ) : null}
                            </div>
                          ))}
                        </dl>
                      ) : null}
                    </div>
                  </article>
                );
              })
            )}
          </div>
        </div>
      ))}
    </div>
  );
}
