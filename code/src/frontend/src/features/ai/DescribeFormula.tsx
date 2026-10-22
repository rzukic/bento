import { useState } from "react";
import { describeFormula, type DescribeAnswer } from "../../api/ai";
import { ApiClientError } from "../../api/client";
import { Icon } from "../../components/common/Icon";
import { Button } from "../../components/ui/Button";
import { useAiEnabled } from "./useAiEnabled";
import "./Ai.css";

/** The AI's remark, cut when it is long (e.g. a raw error it quotes). */
function shorten(note: string | null): string | null {
  return note && note.length > 400 ? `${note.slice(0, 400)}…` : note;
}

/**
 * ✨ under a formula field (Step 4.9): describe what it should compute, the AI
 * writes the formula and Bento checks it; the field then shows its own check
 * and preview. Undo puts back the formula from before.
 */
export function DescribeFormula({
  table,
  kind,
  current,
  resultType,
  onFormula,
}: {
  table: string;
  kind: "column" | "measure";
  current: string;
  resultType?: string;
  onFormula: (formula: string) => void;
}) {
  const enabled = useAiEnabled();
  const [open, setOpen] = useState(false);
  const [sentence, setSentence] = useState("");
  const [busy, setBusy] = useState(false);
  const [answer, setAnswer] = useState<DescribeAnswer | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [before, setBefore] = useState<string | null>(null);

  if (!enabled || !table) return null;

  const submit = async (event: { preventDefault(): void; stopPropagation(): void }) => {
    event.preventDefault();
    event.stopPropagation();
    const text = sentence.trim();
    if (!text || busy) return;
    setBusy(true);
    setError(null);
    setAnswer(null);
    try {
      const result = await describeFormula({ language: navigator.language || "en", sentence: text, table, kind, current: current || undefined, resultType });
      setAnswer(result);
      if (result.formula) {
        setBefore(current);
        onFormula(result.formula);
      }
    } catch (cause) {
      setError(cause instanceof ApiClientError ? cause.message : "The formula could not be written.");
    } finally {
      setBusy(false);
    }
  };

  if (!open) {
    return (
      <button type="button" className="describe__open" onClick={() => setOpen(true)}>
        <Icon name="sparkles" size={12} /> Describe it instead
      </button>
    );
  }

  return (
    <div className="describe describe--compact">
      {/* A div, not a form: the field may sit inside another form. */}
      <div className="describe__form">
        <Icon name="sparkles" size={13} />
        <input
          className="describe__input"
          value={sentence}
          disabled={busy}
          autoFocus
          aria-label="Describe the formula"
          placeholder={kind === "measure" ? "e.g. the share of billable hours" : "e.g. the days until the due date, only for open tasks"}
          onChange={(event) => setSentence(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === "Enter") void submit(event);
            if (event.key === "Escape") {
              event.stopPropagation();
              setOpen(false);
            }
          }}
        />
        <Button type="button" variant="primary" size="sm" disabled={busy || !sentence.trim()} onClick={(event) => void submit(event)}>
          {busy ? "Writing…" : "Write"}
        </Button>
      </div>
      {error ? (
        <p className="describe__status describe__status--error" role="alert">
          {error}
        </p>
      ) : null}
      {answer ? (
        <p className="describe__status" role="status">
          {shorten(answer.note) ?? (answer.formula ? "Done." : "No formula.")}
          {answer.formula && before !== null ? (
            <button
              type="button"
              className="describe__undo"
              onClick={() => {
                onFormula(before);
                setBefore(null);
                setAnswer(null);
              }}
            >
              Undo
            </button>
          ) : null}
        </p>
      ) : null}
    </div>
  );
}
