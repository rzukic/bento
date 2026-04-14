import type { InputHTMLAttributes } from "react";

type TextFieldProps = Omit<InputHTMLAttributes<HTMLInputElement>, "size"> & {
  label: string;
  hint?: string;
  hintTitle?: string;
  message?: string | null;
  error?: string | null;
  hideLabel?: boolean;
  compact?: boolean;
};

export function TextField({
  label,
  hint,
  hintTitle,
  message,
  error,
  hideLabel = false,
  compact = false,
  className = "",
  id,
  ...props
}: TextFieldProps) {
  const inputId = id ?? props.name ?? label.toLowerCase().replace(/\s+/g, "-");
  const messageText = error ?? message;

  return (
    <label className={`field ${compact ? "field--inline" : ""}`}>
      <span className="field__label-row">
        <span className={hideLabel ? "field__label field__label--sr-only" : "field__label"}>
          {label}
        </span>
        {hint ? (
          <span className="field__hint" title={hintTitle}>
            {hint}
          </span>
        ) : null}
      </span>
      <input
        id={inputId}
        className={[
          "field__control",
          compact ? "field__control--compact" : "",
          error ? "field__control--error" : "",
          className
        ]
          .filter(Boolean)
          .join(" ")}
        aria-invalid={error ? true : undefined}
        aria-describedby={messageText ? `${inputId}-message` : undefined}
        {...props}
      />
      {messageText ? (
        <span
          id={`${inputId}-message`}
          className={`field__message ${error ? "field__message--error" : ""}`}
        >
          {messageText}
        </span>
      ) : null}
    </label>
  );
}
