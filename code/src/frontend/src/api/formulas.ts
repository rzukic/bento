import { apiClient } from "./client";

/** A problem in a formula; start/end are character offsets (end exclusive). */
export type FormulaError = { message: string; start: number; end: number };

export type FormulaValidation = {
  valid: boolean;
  /** text, integer, float, currency, boolean, date or datetime; null when invalid. */
  type: string | null;
  errors: FormulaError[];
  /** The columns the formula reads. */
  columns: string[];
};

export type FormulaPreviewRow = {
  id: string | number | null;
  value: string | number | boolean | null;
  inputs: Record<string, string | number | boolean | null>;
};

export type FormulaPreview = {
  type: string;
  columns: string[];
  rows: FormulaPreviewRow[];
};

export type FormulaFunction = {
  name: string;
  category: string;
  signature: string;
  description: string;
  example: string;
  returns: string;
};

export type FormulaRequest = {
  table: string;
  formula: string;
  resultType?: string;
  /** The computed column the formula belongs to: using itself is reported as a cycle. */
  column?: string;
  /** Validate only: the table's draft columns (Model JSON) instead of the applied ones. */
  columns?: unknown[];
  /** Validate only: the whole drafted Model JSON (other tables and links too). */
  model?: unknown;
  limit?: number;
};

type DataEnvelope<T> = { data: T };

/** POST /api/v1/formulas/validate — always resolves; `valid` and `errors` tell the result. */
export async function validateFormula(request: FormulaRequest): Promise<FormulaValidation> {
  return (await apiClient.post<DataEnvelope<FormulaValidation>>("/formulas/validate", request)).data;
}

/** POST /api/v1/formulas/preview — the values on the first rows of the applied table. */
export async function previewFormula(request: FormulaRequest): Promise<FormulaPreview> {
  return (await apiClient.post<DataEnvelope<FormulaPreview>>("/formulas/preview", request)).data;
}

let functionsPromise: Promise<FormulaFunction[]> | null = null;

/** GET /api/v1/formulas/functions, loaded once per session. */
export function loadFormulaFunctions(): Promise<FormulaFunction[]> {
  functionsPromise ??= apiClient
    .get<DataEnvelope<FormulaFunction[]>>("/formulas/functions")
    .then((response) => response.data)
    .catch((error: unknown) => {
      functionsPromise = null;
      throw error;
    });
  return functionsPromise;
}
