import { useCallback, useEffect, useState } from "react";
import { fetchTableRows } from "../../../../api/data";
import { useSchemaStore } from "../../../../stores/schemaStore";
import { DEFAULT_QUERY } from "../../../../types/explorer";
import { ComboboxCellEditor, type ComboboxOption } from "./ComboboxCellEditor";
import {
  findTable,
  getDisplayColumnForTable,
  getSecondaryColumnForTable,
  getReferencePrimaryKey,
  getReferenceTableName,
} from "./referenceMetadata";
import type { CellEditorProps } from "./types";

type FkValue = string | number;

const PAGE_SIZE = 20;

export function ReferenceCellEditor({
  value,
  column,
  autoFocus,
  onCommit,
  onCancel,
  error,
  displayLabel,
}: CellEditorProps<FkValue>) {
  const snapshot = useSchemaStore((s) => s.snapshot);
  const refTableName = getReferenceTableName(column);
  const refTable = findTable(snapshot, refTableName);
  const displayColumn = getDisplayColumnForTable(refTable);
  const secondaryColumn = getSecondaryColumnForTable(refTable);
  const pkColumn = getReferencePrimaryKey(column);
  const nullable = column.nullable ?? true;

  // Start from the label the cell already showed, so the chip does not flash
  // the raw id while the name is fetched.
  const [resolvedLabel, setResolvedLabel] = useState<string | null>(displayLabel ?? null);

  // Resolve the current value's display label by fetching the single row,
  // so the closed trigger shows a name rather than a raw FK id.
  useEffect(() => {
    if (value === null || value === undefined || !refTableName) {
      setResolvedLabel(null);
      return;
    }
    if (displayLabel) return;
    let cancelled = false;
    void fetchTableRows(refTableName, {
      ...DEFAULT_QUERY,
      perPage: 1,
      filters: [{ column: pkColumn, op: "eq", value: String(value) }],
    })
      .then((res) => {
        if (cancelled) return;
        const row = res.data[0];
        if (!row) return;
        const labelRaw = row[displayColumn];
        const label =
          labelRaw === null || labelRaw === undefined ? String(value) : String(labelRaw);
        setResolvedLabel(label);
      })
      .catch(() => {
        /* ignore — fall back to id */
      });
    return () => {
      cancelled = true;
    };
  }, [value, refTableName, pkColumn, displayColumn, displayLabel]);

  const loadOptions = useCallback(
    async (query: string): Promise<ComboboxOption[]> => {
      if (!refTableName) return [];
      const res = await fetchTableRows(refTableName, {
        ...DEFAULT_QUERY,
        perPage: PAGE_SIZE,
        search: query.trim(),
      });
      return res.data
        .map((row) => {
          const pk = row[pkColumn];
          if (pk === null || pk === undefined) return null;
          const id = typeof pk === "number" ? String(pk) : String(pk);
          const labelRaw = row[displayColumn];
          const label =
            labelRaw === null || labelRaw === undefined ? id : String(labelRaw);
          const secondaryRaw = secondaryColumn ? row[secondaryColumn] : null;
          const description =
            secondaryRaw === null || secondaryRaw === undefined || secondaryRaw === ""
              ? undefined
              : String(secondaryRaw);
          const option: ComboboxOption = description ? { value: id, label, description } : { value: id, label };
          return option;
        })
        .filter((c): c is ComboboxOption => c !== null);
    },
    [refTableName, pkColumn, displayColumn, secondaryColumn],
  );

  const stringValue =
    value === null || value === undefined ? null : String(value);

  return (
    <ComboboxCellEditor
      value={stringValue}
      displayLabel={resolvedLabel ?? stringValue}
      nullable={nullable}
      ariaLabel={column.displayName ?? column.name}
      loadOptions={loadOptions}
      asyncSearch
      error={error}
      autoFocus={autoFocus}
      renderValue={(_current, label) => <span className="data-grid__reference">{label}</span>}
      renderOption={(option) => (
        <>
          <span className="data-grid__reference">{option.label}</span>
          {option.description ? (
            <span className="cell-combobox__option-secondary">{option.description}</span>
          ) : null}
        </>
      )}
      onCommit={(next) => {
        if (next === null) {
          onCommit(null);
          return;
        }
        // Preserve numeric FKs as numbers when the original column type is
        // numeric (relation / int64). The combobox passes everything as
        // strings; coerce on commit.
        const asNumber = Number(next);
        if (Number.isInteger(asNumber) && String(asNumber) === next) {
          onCommit(asNumber);
        } else {
          onCommit(next);
        }
      }}
      onCancel={onCancel}
    />
  );
}
