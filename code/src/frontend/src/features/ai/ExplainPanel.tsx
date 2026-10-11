import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import { createPortal } from "react-dom";
import { explain, type ExplainAnswer, type ExplainRequest } from "../../api/ai";
import { ApiClientError } from "../../api/client";
import { Icon } from "../../components/common/Icon";
import { Alert } from "../../components/ui/Alert";
import { Button } from "../../components/ui/Button";
import { SushiMascot } from "../../components/brand";
import { AiAnswer } from "./AiAnswer";
import "./Ai.css";

function formatCost(usd: number): string {
  return usd < 0.01 ? "< $0.01" : `$${usd.toFixed(2)}`;
}

/**
 * "Explain this" (Step 4.7): a side panel that asks Bento's AI what a widget's
 * number is made of and what changed, with room for a follow-up question.
 */
export function ExplainPanel({ request, onClose }: { request: ExplainRequest; onClose: () => void }) {
  const [answer, setAnswer] = useState<ExplainAnswer | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [asked, setAsked] = useState<string | null>(null);
  const [question, setQuestion] = useState("");
  const started = useRef(false);

  const run = useCallback(
    async (followUp?: string) => {
      setBusy(true);
      setError(null);
      try {
        setAnswer(await explain(followUp ? { ...request, question: followUp } : request));
        setAsked(followUp ?? null);
      } catch (cause) {
        setError(cause instanceof ApiClientError ? cause.message : "The explanation could not be loaded.");
      } finally {
        setBusy(false);
      }
    },
    [request],
  );

  useEffect(() => {
    // Once per panel, also under React's double effects in development.
    if (started.current) return;
    started.current = true;
    void run();
  }, [run]);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const ask = (event: FormEvent) => {
    event.preventDefault();
    const text = question.trim();
    if (!text || busy) return;
    setQuestion("");
    void run(text);
  };

  return createPortal(
    <aside className="widget-editor explain-panel" aria-label={`Explain ${request.title}`}>
      <header className="widget-editor__header">
        <h2>
          <Icon name="sparkles" size={15} /> {request.title || "Explain"}
        </h2>
        <Button type="button" variant="ghost" size="sm" onClick={onClose} aria-label="Close explanation">
          <Icon name="x" size={14} />
        </Button>
      </header>
      <div className="widget-editor__body" aria-live="polite">
        {asked ? <p className="explain-panel__question">{asked}</p> : null}
        {busy ? (
          <div className="explain-panel__busy" role="status">
            <SushiMascot width={40} bounce mood="excited" />
            <span>Looking at the numbers…</span>
          </div>
        ) : error ? (
          <Alert tone="error" message={error} />
        ) : answer ? (
          <>
            <AiAnswer text={answer.answer} />
            <p className="explain-panel__meta">
              {answer.model} · {answer.toolCalls} {answer.toolCalls === 1 ? "lookup" : "lookups"} · {formatCost(answer.costUsd)} ·{" "}
              {Math.round(answer.durationMs / 1000)} s
            </p>
          </>
        ) : null}
      </div>
      <form className="widget-editor__footer explain-panel__ask" onSubmit={ask}>
        <input
          className="field__control field__control--compact"
          value={question}
          placeholder="Ask about this, e.g. why did it rise?"
          aria-label="Question about this widget"
          disabled={busy}
          onChange={(event) => setQuestion(event.target.value)}
        />
        <Button type="submit" variant="primary" size="sm" disabled={busy || !question.trim()}>
          Ask
        </Button>
      </form>
    </aside>,
    document.body,
  );
}
