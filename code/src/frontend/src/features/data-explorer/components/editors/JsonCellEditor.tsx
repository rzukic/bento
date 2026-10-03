import { useEffect, useRef, useState, type KeyboardEvent } from "react";
import { EditorShell } from "./editorShell";
import type { CellEditorProps } from "./types";

function stringifyInitial(value: unknown): string {
  if (value === null || value === undefined) return "";
  if (typeof value === "string") return value;
  try {
    return JSON.stringify(value, null, 2);
  } catch {
    return String(value);
  }
}

export function JsonCellEditor({
  value,
  column,
  autoFocus,
  onCommit,
  onCancel,
  error,
}: CellEditorProps<unknown>) {
  const [draft, setDraft] = useState(stringifyInitial(value));
  const [parseError, setParseError] = useState<string | null>(null);
  const inputRef = useRef<HTMLTextAreaElement | null>(null);

  useEffect(() => {
    if (autoFocus) inputRef.current?.focus();
  }, [autoFocus]);

  function commit() {
    if (draft.trim() === "" && (column.nullable ?? true)) {
      setParseError(null);
      onCommit(null);
      return;
    }
    try {
      const parsed: unknown = JSON.parse(draft);
      setParseError(null);
      onCommit(parsed);
    } catch {
      setParseError("This isn't valid JSON. Check the format and try again.");
    }
  }

  function onKeyDown(event: KeyboardEvent<HTMLTextAreaElement>) {
    if (event.key === "Escape") {
      event.preventDefault();
      onCancel();
      return;
    }
    if (event.key === "Enter" && (event.metaKey || event.ctrlKey)) {
      event.preventDefault();
      commit();
    }
  }

  return (
    <EditorShell kind="mono" error={error ?? parseError}>
      <textarea
        ref={inputRef}
        rows={4}
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        onKeyDown={onKeyDown}
        onBlur={commit}
        aria-label={column.displayName ?? column.name}
        aria-invalid={Boolean(error ?? parseError) || undefined}
      />
    </EditorShell>
  );
}
