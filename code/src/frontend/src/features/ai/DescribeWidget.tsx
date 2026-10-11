import { useState, type FormEvent } from "react";
import { describeWidget, type DescribeAnswer } from "../../api/ai";
import { ApiClientError } from "../../api/client";
import type { DashboardVariable, DashboardWidget } from "../../api/dashboards";
import { Icon } from "../../components/common/Icon";
import { Button } from "../../components/ui/Button";
import type { ResolvedRange } from "../dashboards/dateRange";
import "./Ai.css";

/** The AI's remark, cut when it is long (e.g. a raw error it quotes). */
function shorten(note: string | null): string | null {
  return note && note.length > 400 ? `${note.slice(0, 400)}…` : note;
}

function formatCost(usd: number): string {
  return usd < 0.01 ? "< $0.01" : `$${usd.toFixed(2)}`;
}

/**
 * "Describe instead of click" in the Lens editor (Step 4.9): a sentence becomes
 * the widget — its query already ran — shown live; Undo puts back what was there.
 */
export function DescribeWidget({
  widget,
  dashboard,
  otherWidgets,
  range,
  variables,
  values,
  onApply,
}: {
  widget: DashboardWidget;
  dashboard?: string;
  otherWidgets: string[];
  range: ResolvedRange;
  variables: DashboardVariable[];
  values: Record<string, unknown>;
  /** The new widget and the sentence, or the widget from before (undo) and null. */
  onApply: (widget: DashboardWidget, sentence: string | null) => void;
}) {
  const [sentence, setSentence] = useState("");
  const [busy, setBusy] = useState(false);
  const [answer, setAnswer] = useState<DescribeAnswer | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [before, setBefore] = useState<DashboardWidget | null>(null);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    const text = sentence.trim();
    if (!text || busy) return;
    setBusy(true);
    setError(null);
    setAnswer(null);
    try {
      const result = await describeWidget({
        language: navigator.language || "en",
        sentence: text,
        dashboard,
        widget,
        range: { from: range.from ?? null, to: range.to ?? null },
        variables: variables.map((v) => ({ name: v.name, label: v.label, value: values[v.name] ?? null })),
        otherWidgets,
      });
      setAnswer(result);
      if (result.widget) {
        setBefore(widget);
        onApply(result.widget, text);
      }
    } catch (cause) {
      setError(cause instanceof ApiClientError ? cause.message : "The widget could not be built.");
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="describe">
      <form className="describe__form" onSubmit={(event) => void submit(event)}>
        <Icon name="sparkles" size={15} />
        <input
          className="describe__input"
          value={sentence}
          disabled={busy}
          aria-label="Describe the widget"
          placeholder="Describe it, e.g. expenses by main category per quarter, without rent"
          onChange={(event) => setSentence(event.target.value)}
        />
        <Button type="submit" variant="primary" size="sm" disabled={busy || !sentence.trim()}>
          {busy ? "Building…" : "Build"}
        </Button>
      </form>
      {busy ? <p className="describe__status" role="status">Looking at your data and trying the query…</p> : null}
      {error ? (
        <p className="describe__status describe__status--error" role="alert">
          {error}
        </p>
      ) : null}
      {answer ? (
        <p className="describe__status" role="status">
          {shorten(answer.note) ?? (answer.widget ? "Done." : "No widget.")}
          <span className="describe__meta">
            {" "}
            · {answer.toolCalls} {answer.toolCalls === 1 ? "step" : "steps"} · {formatCost(answer.costUsd)}
          </span>
          {answer.widget && before ? (
            <button
              type="button"
              className="describe__undo"
              onClick={() => {
                onApply(before, null);
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
