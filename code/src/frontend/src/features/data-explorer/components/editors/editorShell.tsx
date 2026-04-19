import type { ReactNode } from "react";

/**
 * Wraps an editor input so all editors render error state the same way:
 * a red-bordered control and an inline message below.
 */
export function EditorShell({
  error,
  children,
}: {
  error?: string | null;
  children: ReactNode;
}) {
  return (
    <span
      className={`cell-editor${error ? " cell-editor--error" : ""}`}
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
