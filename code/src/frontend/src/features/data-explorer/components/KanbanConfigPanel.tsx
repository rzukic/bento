import { useEffect, useMemo, useState } from "react";
import { Icon } from "../../../components/common/Icon";
import type { ModelTable } from "../../../types/api";

const MAX_CARD_FIELDS = 3;

type KanbanConfigPanelProps = {
  open: boolean;
  table: ModelTable;
  kanbanColumn: string | null;
  cardFieldNames: string[];
  hiddenLaneValues?: string[];
  onApply: (patch: {
    kanbanColumn: string | null;
    cardFieldNames: string[];
    hiddenLaneValues: string[];
  }) => void;
  onClose: () => void;
};

export function KanbanConfigPanel({
  open,
  table,
  kanbanColumn,
  cardFieldNames,
  hiddenLaneValues = [],
  onApply,
  onClose,
}: KanbanConfigPanelProps) {
  const enumColumns = useMemo(
    () => table.columns.filter((c) => c.type === "enum"),
    [table.columns],
  );

  // Draft state — edits stay local until the user clicks Apply.
  const [draftColumn, setDraftColumn] = useState<string | null>(kanbanColumn);
  const [draftFields, setDraftFields] = useState<string[]>(cardFieldNames);
  const [draftHiddenLanes, setDraftHiddenLanes] =
    useState<string[]>(hiddenLaneValues);

  // Resync drafts when the panel opens against a possibly-changed view.
  useEffect(() => {
    if (open) {
      setDraftColumn(kanbanColumn);
      setDraftFields(cardFieldNames);
      setDraftHiddenLanes(hiddenLaneValues);
    }
  }, [open, kanbanColumn, cardFieldNames, hiddenLaneValues]);

  // Pre-select the sole enum column when opening an unconfigured view.
  useEffect(() => {
    if (!open) return;
    if (draftColumn !== null) return;
    if (enumColumns.length === 1) {
      setDraftColumn(enumColumns[0].name);
    }
  }, [open, draftColumn, enumColumns]);

  // Close on Escape.
  useEffect(() => {
    if (!open) return;
    function onKey(event: KeyboardEvent) {
      if (event.key === "Escape") onClose();
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open, onClose]);

  if (!open) return null;

  const candidateFieldColumns = table.columns.filter(
    (c) => !c.primaryKey && c.name !== draftColumn,
  );

  // Lane values come from the selected grouping column's enum values.
  const selectedEnumColumn = draftColumn
    ? enumColumns.find((c) => c.name === draftColumn)
    : null;
  const laneValues = selectedEnumColumn?.values ?? [];

  function toggleField(name: string) {
    setDraftFields((current) => {
      if (current.includes(name)) {
        return current.filter((n) => n !== name);
      }
      if (current.length >= MAX_CARD_FIELDS) return current;
      return [...current, name];
    });
  }

  function toggleLaneVisibility(value: string) {
    setDraftHiddenLanes((current) => {
      if (current.includes(value)) {
        return current.filter((v) => v !== value);
      }
      return [...current, value];
    });
  }

  function handleApply() {
    if (!draftColumn) return;
    onApply({
      kanbanColumn: draftColumn,
      cardFieldNames: draftFields.slice(0, MAX_CARD_FIELDS),
      hiddenLaneValues: draftHiddenLanes,
    });
    onClose();
  }

  const canApply = draftColumn !== null;
  const fieldLimitReached = draftFields.length >= MAX_CARD_FIELDS;

  return (
    <aside
      className="kanban-config"
      role="dialog"
      aria-label="Configure kanban view"
    >
      <div className="kanban-config__header">
        <h3 className="kanban-config__title">Configure kanban view</h3>
        <button
          type="button"
          className="kanban-config__close"
          onClick={onClose}
          aria-label="Close"
        >
          <Icon name="x" size={14} />
        </button>
      </div>

      <div className="kanban-config__body">
        {/* Group by */}
        <div className="kanban-config__field">
          <label
            className="kanban-config__label"
            htmlFor="kanban-config-group-by"
          >
            Group by
            <span className="kanban-config__required" aria-hidden="true">
              *
            </span>
          </label>
          {enumColumns.length === 0 ? (
            <p className="kanban-config__hint">
              This table has no choice columns to group by. Add one in the
              schema designer.
            </p>
          ) : (
            <select
              id="kanban-config-group-by"
              className="kanban-config__select"
              value={draftColumn ?? ""}
              onChange={(event) => {
                setDraftColumn(event.target.value || null);
                // Reset hidden lanes when switching grouping column.
                setDraftHiddenLanes([]);
              }}
            >
              <option value="">Select a column</option>
              {enumColumns.map((column) => (
                <option key={column.name} value={column.name}>
                  {column.displayName ?? column.name}
                </option>
              ))}
            </select>
          )}
        </div>

        {/* Visible lanes */}
        {laneValues.length > 0 ? (
          <div className="kanban-config__field">
            <label className="kanban-config__label">
              Visible lanes
              <span className="kanban-config__label-hint">
                {laneValues.length - draftHiddenLanes.length} of{" "}
                {laneValues.length}
              </span>
            </label>
            <ul className="kanban-config__field-list">
              {laneValues.map((value) => {
                const visible = !draftHiddenLanes.includes(value);
                return (
                  <li key={value} className="kanban-config__field-item">
                    <button
                      type="button"
                      className={
                        visible
                          ? "kanban-config__field-btn kanban-config__field-btn--on"
                          : "kanban-config__field-btn"
                      }
                      onClick={() => toggleLaneVisibility(value)}
                      aria-pressed={visible}
                    >
                      <span className="kanban-config__field-check">
                        {visible ? <Icon name="check" size={12} /> : null}
                      </span>
                      <span className="kanban-config__field-name">{value}</span>
                    </button>
                  </li>
                );
              })}
            </ul>
          </div>
        ) : null}

        {/* Card fields */}
        <div className="kanban-config__field">
          <label className="kanban-config__label">
            Card fields
            <span className="kanban-config__label-hint">
              Up to {MAX_CARD_FIELDS}
            </span>
          </label>
          <p className="kanban-config__hint">
            Shown as secondary rows on each card, in selection order.
          </p>
          <ul className="kanban-config__field-list">
            {candidateFieldColumns.map((column) => {
              const selected = draftFields.includes(column.name);
              const disabled = !selected && fieldLimitReached;
              const selectionIndex = draftFields.indexOf(column.name);
              return (
                <li key={column.name} className="kanban-config__field-item">
                  <button
                    type="button"
                    className={
                      selected
                        ? "kanban-config__field-btn kanban-config__field-btn--on"
                        : "kanban-config__field-btn"
                    }
                    onClick={() => toggleField(column.name)}
                    disabled={disabled}
                    aria-pressed={selected}
                  >
                    <span className="kanban-config__field-check">
                      {selected ? <Icon name="check" size={12} /> : null}
                    </span>
                    <span className="kanban-config__field-name">
                      {column.displayName ?? column.name}
                    </span>
                    {selected ? (
                      <span className="kanban-config__field-index">
                        {selectionIndex + 1}
                      </span>
                    ) : null}
                  </button>
                </li>
              );
            })}
            {candidateFieldColumns.length === 0 ? (
              <li className="kanban-config__hint">
                No eligible fields. Pick a grouping column first.
              </li>
            ) : null}
          </ul>
        </div>
      </div>

      <div className="kanban-config__footer">
        <button
          type="button"
          className="kanban-config__btn"
          onClick={onClose}
        >
          Cancel
        </button>
        <button
          type="button"
          className="kanban-config__btn kanban-config__btn--primary"
          onClick={handleApply}
          disabled={!canApply}
        >
          Apply
        </button>
      </div>
    </aside>
  );
}
