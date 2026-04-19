import { useEffect, useRef, useState, type KeyboardEvent } from "react";
import { EditorShell } from "./editorShell";
import { parseLenientNumber } from "./numericParsing";
import type { CellEditorProps } from "./types";

export function IntegerCellEditor({
  value,
  column,
  autoFocus,
  onCommit,
  onCancel,
  error,
}: CellEditorProps<number>) {
  const [draft, setDraft] = useState(value === null || value === undefined ? "" : String(value));
  const inputRef = useRef<HTMLInputElement | null>(null);

  useEffect(() => {
    if (autoFocus) inputRef.current?.focus();
  }, [autoFocus]);

  function commit() {
    const trimmed = draft.trim();
    if (trimmed === "" && (column.nullable ?? true)) {
      onCommit(null);
      return;
    }
    const next = parseLenientNumber(trimmed, { integer: true });
    if (next === null) {
      onCancel();
      return;
    }
    onCommit(next);
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
        type="text"
        inputMode="numeric"
        autoComplete="off"
        size={1}
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
