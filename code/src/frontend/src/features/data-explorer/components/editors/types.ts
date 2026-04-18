import type { ModelColumn } from "../../../../types/api";

/** Schema metadata an editor needs to render and validate a value. */
export type ColumnSchema = ModelColumn;

/**
 * Shared contract every cell editor implements. Editors are pure presentation:
 * they render the value, accept edits, and report a typed value back via
 * `onCommit`. They never call the API directly — the parent (inline grid editor
 * or full-screen record editor) owns the save round-trip.
 *
 * Conventions across all editors:
 *  - Enter and blur fire `onCommit`
 *  - Escape fires `onCancel`
 *  - On nullable columns, Backspace on an already-empty value commits `null`
 *  - When `error` is set, the editor renders a red border and a small message
 *    slot underneath the input
 */
export type CellEditorProps<T> = {
  value: T | null;
  column: ColumnSchema;
  autoFocus?: boolean;
  onCommit: (next: T | null) => void;
  onCancel: () => void;
  error?: string | null;
};
