import type { ReactNode } from "react";

/** **bold** inside a line; everything else stays text (no HTML from the model). */
function inline(text: string): ReactNode[] {
  return text.split(/(\*\*[^*]+\*\*)/g).map((part, index) =>
    part.startsWith("**") && part.endsWith("**") && part.length > 4 ? <strong key={index}>{part.slice(2, -2)}</strong> : part,
  );
}

/** An AI answer: paragraphs, "- " or "* " lists and **bold** — the shapes the prompt asks for. */
export function AiAnswer({ text }: { text: string }) {
  const blocks: ReactNode[] = [];
  let list: string[] = [];
  const flush = () => {
    if (list.length === 0) return;
    blocks.push(
      <ul key={`l${blocks.length}`}>
        {list.map((item, index) => (
          <li key={index}>{inline(item)}</li>
        ))}
      </ul>,
    );
    list = [];
  };
  for (const line of text.split("\n")) {
    const trimmed = line.trim();
    if (/^[-*•]\s+/.test(trimmed)) {
      list.push(trimmed.replace(/^[-*•]\s+/, ""));
      continue;
    }
    flush();
    if (!trimmed) continue;
    blocks.push(<p key={blocks.length}>{inline(trimmed.replace(/^#+\s*/, ""))}</p>);
  }
  flush();
  return <div className="ai-answer">{blocks}</div>;
}
