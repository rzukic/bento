import { TemporalTextEditor } from "./TemporalTextEditor";
import type { CellEditorProps } from "./types";

export function DateCellEditor({
  value,
  column,
  autoFocus,
  onCommit,
  onCancel,
  error,
}: CellEditorProps<string>) {
  return (
    <TemporalTextEditor
      mode="date"
      value={value}
      column={column}
      autoFocus={autoFocus}
      onCommit={onCommit}
      onCancel={onCancel}
      error={error}
    />
  );
}
