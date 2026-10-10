import { useEffect, useRef, useState, type KeyboardEvent } from "react";
import { EditorShell } from "./editorShell";
import { getCurrentLocale } from "./temporalEditing";
import { currentCurrency } from "../../../../lib/preferences";
import type { CellEditorProps, ColumnSchema } from "./types";

function getCurrencyMeta(column: ColumnSchema): { locale: string; currency: string } {
  const meta = (column as { currency?: { locale?: string; code?: string } }).currency;
  return {
    locale: meta?.locale ?? getCurrentLocale(),
    currency: meta?.code ?? currentCurrency(),
  };
}

export function CurrencyCellEditor({
  value,
  column,
  autoFocus,
  onCommit,
  onCancel,
  error,
}: CellEditorProps<number>) {
  const [draft, setDraft] = useState(value === null || value === undefined ? "" : String(value));
  const [editing, setEditing] = useState(false);
  const inputRef = useRef<HTMLInputElement | null>(null);
  const { locale, currency } = getCurrencyMeta(column);
  const formatter = new Intl.NumberFormat(locale, { style: "currency", currency });

  useEffect(() => {
    if (autoFocus) inputRef.current?.focus();
  }, [autoFocus]);

  function commit() {
    setEditing(false);
    const trimmed = draft.replace(/[^0-9.-]/g, "").trim();
    if (trimmed === "" && (column.nullable ?? true)) {
      onCommit(null);
      return;
    }
    const next = Number(trimmed);
    if (!Number.isFinite(next)) {
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

  const display =
    !editing && draft !== "" && Number.isFinite(Number(draft))
      ? formatter.format(Number(draft))
      : draft;

  return (
    <EditorShell kind="mono" error={error}>
      <input
        ref={inputRef}
        type="text"
        inputMode="decimal"
        size={1}
        value={display}
        onFocus={() => setEditing(true)}
        onChange={(e) => setDraft(e.target.value)}
        onKeyDown={onKeyDown}
        onBlur={commit}
        aria-label={column.displayName ?? column.name}
        aria-invalid={Boolean(error) || undefined}
      />
    </EditorShell>
  );
}
