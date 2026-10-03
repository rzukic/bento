import type { ReactNode } from "react";

/**
 * What the static value of this editor looks like, so the control can take
 * the same typography: plain text, a mono value (numbers, dates, JSON) or a
 * choice (enum, boolean, reference) shown as a chip.
 */
export type EditorKind = "text" | "mono" | "choice";

/**
 * Wraps an editor input so all editors render error state the same way:
 * a red-bordered control and an inline message below.
 */
export function EditorShell({
  error,
  kind = "text",
  children,
}: {
  error?: string | null;
  kind?: EditorKind;
  children: ReactNode;
}) {
  return (
    <span
      className={`cell-editor cell-editor--${kind}${error ? " cell-editor--error" : ""}`}
      data-testid="cell-editor"
    >
      {children}
      {error ? (
        <span className="cell-editor__error" role="alert">
          {error}
        </span>
      ) : null}
    </span>
  );
}
