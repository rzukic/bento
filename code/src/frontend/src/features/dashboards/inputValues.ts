import type { DashboardVariable } from "../../api/dashboards";

/**
 * Variables viewers change on the dashboard (beta feedback: assumptions such as a
 * return or a savings rate could only be changed in the editor). The chosen value
 * lives in the address (`?var.rate=0.07`), so a bookmark keeps it, and replaces
 * the variable's formula (its default) for this view.
 */
const PARAM = "var.";

export function inputValuesFromSearch(params: URLSearchParams): Record<string, string> {
  const values: Record<string, string> = {};
  for (const [key, value] of params.entries()) if (key.startsWith(PARAM)) values[key.slice(PARAM.length)] = value;
  return values;
}

export function inputToSearch(name: string, value: string | null, params: URLSearchParams): URLSearchParams {
  const next = new URLSearchParams(params);
  if (value === null || value === "") next.delete(`${PARAM}${name}`);
  else next.set(`${PARAM}${name}`, value);
  return next;
}

export function clearInputs(params: URLSearchParams): URLSearchParams {
  const next = new URLSearchParams(params);
  for (const key of [...next.keys()]) if (key.startsWith(PARAM)) next.delete(key);
  return next;
}

/** A value as a formula literal: numbers as they are, anything else as quoted text. */
export function literal(value: string): string {
  return /^-?\d+(\.\d+)?$/.test(value.trim()) ? value.trim() : `"${value.replace(/"/g, '""')}"`;
}

/** The value a formula literal stands for, for the controls: 0.07, or base for "base". */
export function literalValue(formula: string): string {
  const trimmed = formula.trim();
  const quoted = /^"(.*)"$/s.exec(trimmed) ?? /^'(.*)'$/s.exec(trimmed);
  return quoted ? quoted[1].replace(/""/g, '"') : trimmed;
}

/** The variables as this view computes them: input variables with a chosen value use it. */
export function withInputs(variables: DashboardVariable[], values: Record<string, string>): DashboardVariable[] {
  return variables.map((variable) =>
    variable.input && values[variable.name] !== undefined ? { ...variable, source: undefined, formula: literal(values[variable.name]) } : variable,
  );
}
