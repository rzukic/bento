import { useEffect, useRef, useState, type KeyboardEvent } from "react";
import { EditorShell } from "./editorShell";
import type { CellEditorProps } from "./types";

function toDateTimeInputValue(value: string | null): string {
  if (!value) return "";
  const match = value.match(/^(\d{4}-\d{2}-\d{2})[T\s](\d{2}:\d{2})/);
  if (!match) return "";
  return `${match[1]}T${match[2]}`;
}

export function DateTimeCellEditor({
  value,
  column,
  autoFocus,
  onCommit,
  onCancel,
  error,
}: CellEditorProps<string>) {
  const [draft, setDraft] = useState(toDateTimeInputValue(value));
  const inputRef = useRef<HTMLInputElement | null>(null);

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

  function onKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (event.key === "Escape") {
      event.preventDefault();
      onCancel();
      return;
    }
    if (event.key === "Enter") {
      event.preventDefault();
      commit();
      return;
    }
    if (event.key === "Backspace" && draft === "" && (column.nullable ?? true)) {
      event.preventDefault();
      onCommit(null);
    }
  }

  return (
    <EditorShell error={error}>
      <input
        ref={inputRef}
        type="datetime-local"
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        onKeyDown={onKeyDown}
        onBlur={commit}
        aria-label={column.displayName ?? column.name}
        aria-invalid={Boolean(error) || undefined}
      />
    </EditorShell>
  );
}
