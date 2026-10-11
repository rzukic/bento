import { useEffect, useState } from "react";
import type { DashboardVariable } from "../../api/dashboards";
import { Button } from "../../components/ui/Button";
import { formatNumber, toNumber } from "../../lib/format";
import { literalValue } from "./inputValues";

/**
 * The variables viewers may change, above the widgets: a number field, a slider
 * or a choice. Values go into the address; Reset goes back to the defaults.
 */
export function VariableInputs({
  variables,
  values,
  onChange,
  onReset,
}: {
  variables: DashboardVariable[];
  values: Record<string, string>;
  onChange: (name: string, value: string | null) => void;
  onReset: () => void;
}) {
  const inputs = variables.filter((v) => v.input);
  if (inputs.length === 0) return null;
  const changed = inputs.some((v) => values[v.name] !== undefined);
  return (
    <div className="dashboard-filters dashboard-inputs" role="group" aria-label="Assumptions">
      {inputs.map((variable) => (
        <VariableInput key={variable.name} variable={variable} value={values[variable.name] ?? literalValue(variable.formula)} onChange={(value) => onChange(variable.name, value)} />
      ))}
      {changed ? (
        <Button type="button" variant="ghost" size="sm" onClick={onReset}>
          Reset to defaults
        </Button>
      ) : null}
    </div>
  );
}

function VariableInput({ variable, value, onChange }: { variable: DashboardVariable; value: string; onChange: (value: string | null) => void }) {
  const input = variable.input!;
  const label = variable.label || variable.name;
  // Typing stays local until it is a number; a slider sends while it moves, after a short pause.
  const [draft, setDraft] = useState(value);
  useEffect(() => setDraft(value), [value]);
  useEffect(() => {
    if (draft === value || input.kind === "select") return;
    const timer = window.setTimeout(() => {
      if (toNumber(draft) !== null) onChange(draft);
    }, 300);
    return () => window.clearTimeout(timer);
  }, [draft, value, input.kind, onChange]);

  if (input.kind === "select") {
    return (
      <label className="dashboard-input">
        <span className="dashboard-input__label">{label}</span>
        <select className="field__control field__control--compact" aria-label={label} value={value} onChange={(event) => onChange(event.target.value)}>
          {(input.options ?? []).map((option) => (
            <option key={String(option)} value={String(option)}>
              {String(option)}
            </option>
          ))}
        </select>
      </label>
    );
  }

  const number = toNumber(draft);
  return (
    <label className="dashboard-input">
      <span className="dashboard-input__label">{label}</span>
      {input.kind === "slider" ? (
        <>
          <input
            type="range"
            aria-label={label}
            min={input.min ?? 0}
            max={input.max ?? 1}
            step={input.step ?? 0.01}
            value={number ?? 0}
            onChange={(event) => setDraft(event.target.value)}
          />
          <span className="dashboard-input__value">{number === null ? "—" : formatNumber(number, "number")}</span>
        </>
      ) : (
        <input
          className="field__control field__control--compact dashboard-input__number"
          type="number"
          aria-label={label}
          min={input.min}
          max={input.max}
          step={input.step ?? "any"}
          value={draft}
          onChange={(event) => setDraft(event.target.value)}
        />
      )}
    </label>
  );
}
