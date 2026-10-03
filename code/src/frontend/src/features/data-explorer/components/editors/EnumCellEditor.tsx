import { useCallback } from "react";
import { renderCellValue } from "../cellValue";
import { ComboboxCellEditor, type ComboboxOption } from "./ComboboxCellEditor";
import type { CellEditorProps } from "./types";

export function EnumCellEditor({
  value,
  column,
  autoFocus,
  onCommit,
  onCancel,
  error,
}: CellEditorProps<string>) {
  const allowed = column.values ?? [];
  const nullable = column.nullable ?? true;

  const loadOptions = useCallback(
    (query: string): ComboboxOption[] => {
      const q = query.trim().toLowerCase();
      const base = allowed.map((option) => ({ value: option, label: option }));
      if (q === "") return base;
      return base.filter((opt) => opt.label.toLowerCase().includes(q));
    },
    [allowed],
  );

  return (
    <ComboboxCellEditor
      value={value ?? null}
      displayLabel={value ?? null}
      nullable={nullable}
      ariaLabel={column.displayName ?? column.name}
      searchPlaceholder="Type to filter"
      loadOptions={loadOptions}
      error={error}
      autoFocus={autoFocus}
      onCommit={onCommit}
      onCancel={onCancel}
      renderValue={(current) => renderCellValue(column, current)}
      renderOption={(option) => renderCellValue(column, option.value)}
    />
  );
}
