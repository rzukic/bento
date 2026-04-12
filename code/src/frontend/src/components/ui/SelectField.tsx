import type { SelectHTMLAttributes } from "react";

type SelectOption = {
  label: string;
  value: string;
};

type SelectFieldProps = Omit<SelectHTMLAttributes<HTMLSelectElement>, "size"> & {
  label: string;
  options: SelectOption[];
  hint?: string;
  message?: string | null;
  error?: string | null;
  hideLabel?: boolean;
  compact?: boolean;
};

export function SelectField({
  label,
  options,
  hint,
  message,
  error,
  hideLabel = false,
  compact = false,
  className = "",
  id,
  ...props
}: SelectFieldProps) {
  const selectId = id ?? props.name ?? label.toLowerCase().replace(/\s+/g, "-");
  const messageText = error ?? message;

  return (
    <label className={`field ${compact ? "field--inline" : ""}`}>
      <span className="field__label-row">
        <span className={hideLabel ? "field__label field__label--sr-only" : "field__label"}>
          {label}
        </span>
        {hint ? <span className="field__hint">{hint}</span> : null}
      </span>
      <select
        id={selectId}
        className={[
          "field__control",
          compact ? "field__control--compact" : "",
          error ? "field__control--error" : "",
          className
        ]
          .filter(Boolean)
          .join(" ")}
        aria-invalid={error ? true : undefined}
        aria-describedby={messageText ? `${selectId}-message` : undefined}
        {...props}
      >
        {options.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </select>
      {messageText ? (
        <span
          id={`${selectId}-message`}
          className={`field__message ${error ? "field__message--error" : ""}`}
        >
          {messageText}
        </span>
      ) : null}
    </label>
  );
}
