import { useCallback } from "react";
import { ComboboxCellEditor, type ComboboxOption } from "./ComboboxCellEditor";
import type { CellEditorProps } from "./types";

const BOOLEAN_OPTIONS: ComboboxOption[] = [
  { value: "true", label: "Yes" },
  { value: "false", label: "No" },
];

export function BooleanCellEditor({
  value,
  column,
  autoFocus,
  onCommit,
  onCancel,
  error,
}: CellEditorProps<boolean>) {
  const nullable = column.nullable ?? true;

  const loadOptions = useCallback((query: string): ComboboxOption[] => {
    const q = query.trim().toLowerCase();
    if (q === "") return BOOLEAN_OPTIONS;
    return BOOLEAN_OPTIONS.filter((opt) =>
      opt.label.toLowerCase().includes(q),
    );
  }, []);

  const stringValue =
    value === null || value === undefined ? null : value ? "true" : "false";
  const displayLabel = stringValue === "true" ? "Yes" : stringValue === "false" ? "No" : null;

  return (
    <ComboboxCellEditor
      value={stringValue}
      displayLabel={displayLabel}
      nullable={nullable}
      ariaLabel={column.displayName ?? column.name}
      searchPlaceholder={displayLabel ?? "Select…"}
      loadOptions={loadOptions}
      error={error}
      autoFocus={autoFocus}
      onCommit={(next) => {
        if (next === null) {
          onCommit(null);
          return;
        }
        onCommit(next === "true");
      }}
      onCancel={onCancel}
    />
  );
}
