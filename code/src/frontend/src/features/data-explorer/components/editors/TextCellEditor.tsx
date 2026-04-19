import { useEffect, useRef, useState, type KeyboardEvent } from "react";
import { EditorShell } from "./editorShell";
import type { CellEditorProps } from "./types";

export type TextCellEditorProps = CellEditorProps<string> & { multiline?: boolean };

export function TextCellEditor({
  value,
  column,
  autoFocus,
  onCommit,
  onCancel,
  error,
  multiline = false,
}: TextCellEditorProps) {
  const [draft, setDraft] = useState(value ?? "");
  const inputRef = useRef<HTMLInputElement | HTMLTextAreaElement | null>(null);

  useEffect(() => {
    if (autoFocus) inputRef.current?.focus();
  }, [autoFocus]);

  function commit() {
    if (draft === "" && (column.nullable ?? true)) {
      onCommit(null);
      return;
    }
    onCommit(draft);
  }

  function onKeyDown(event: KeyboardEvent<HTMLInputElement | HTMLTextAreaElement>) {
    if (event.key === "Escape") {
      event.preventDefault();
      onCancel();
      return;
    }
    if (event.key === "Enter" && (!multiline || event.metaKey || event.ctrlKey)) {
      event.preventDefault();
      commit();
      return;
    }
    if (event.key === "Backspace" && draft === "" && (column.nullable ?? true)) {
      event.preventDefault();
      onCommit(null);
    }
  }

  const sharedProps = {
    value: draft,
    onChange: (e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) =>
      setDraft(e.target.value),
    onKeyDown,
    onBlur: commit,
    "aria-label": column.displayName ?? column.name,
    "aria-invalid": Boolean(error) || undefined,
  };

  return (
    <EditorShell error={error}>
      {multiline ? (
        <textarea
          ref={(el) => {
            inputRef.current = el;
          }}
          rows={3}
          {...sharedProps}
        />
      ) : (
        <input
          ref={(el) => {
            inputRef.current = el;
          }}
          type="text"
          size={1}
          {...sharedProps}
        />
      )}
    </EditorShell>
  );
}
