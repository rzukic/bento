type SpinnerProps = {
  label?: string;
};

export function Spinner({ label = "Loading" }: SpinnerProps) {
  return (
    <span className="spinner" role="status" aria-live="polite">
      <span className="spinner__ring" aria-hidden="true" />
      <span className="spinner__label">{label}</span>
    </span>
  );
}
