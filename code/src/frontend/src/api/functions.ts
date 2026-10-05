import { apiClient } from "./client";

/** float, integer, currency, text, boolean, date, datetime — arguments may also be "any". */
export type FunctionType = "float" | "integer" | "currency" | "text" | "boolean" | "date" | "datetime";
export type ArgumentType = FunctionType | "any";

export const RESULT_TYPES: FunctionType[] = ["float", "integer", "currency", "text", "boolean", "date", "datetime"];
export const ARGUMENT_TYPES: ArgumentType[] = [...RESULT_TYPES, "any"];

export type FunctionArgument = { name: string; type: ArgumentType };

export type FunctionUsage = {
  kind: "column" | "dashboard";
  name: string;
  table?: string | null;
  column?: string | null;
  dashboardId?: number | null;
};

/** A custom function (Step 4.1): JavaScript called from formulas with CUSTOM("name", …). */
export type CustomFunction = {
  name: string;
  description: string | null;
  arguments: FunctionArgument[];
  resultType: FunctionType;
  /** The function's body; the arguments are its parameters. */
  code: string;
  version: number;
  createdAt: string;
  updatedAt: string;
  usedBy: FunctionUsage[];
};

export type FunctionVersion = {
  version: number;
  arguments: FunctionArgument[];
  resultType: FunctionType;
  code: string;
  createdAt: string;
};

export type FunctionDraft = {
  name?: string;
  description?: string | null;
  arguments: FunctionArgument[];
  resultType: FunctionType;
  code: string;
};

export type FunctionTestResult = {
  value: string | number | boolean | null;
  resultType: FunctionType;
  error: string | null;
  logs: string[];
  durationMs: number;
};

export type StoredValueFailure = { table: string; column: string; function: string; rows: number; error: string };

/** The background recalculation of computed columns that call functions. */
export type FunctionStatus = {
  pending: boolean;
  lastRunAt: string | null;
  lastUpdated: number;
  failures: StoredValueFailure[];
};

type DataEnvelope<T> = { data: T };

export async function listFunctions(): Promise<CustomFunction[]> {
  return (await apiClient.get<DataEnvelope<CustomFunction[]>>("/functions")).data;
}

export async function getFunctionVersions(name: string): Promise<FunctionVersion[]> {
  return (await apiClient.get<DataEnvelope<FunctionVersion[]>>(`/functions/${encodeURIComponent(name)}/versions`)).data;
}

export async function createFunction(draft: FunctionDraft): Promise<CustomFunction> {
  return (await apiClient.post<DataEnvelope<CustomFunction>>("/functions", draft)).data;
}

export async function updateFunction(name: string, draft: FunctionDraft): Promise<CustomFunction> {
  const { name: _ignored, ...changes } = draft;
  void _ignored;
  return (await apiClient.put<DataEnvelope<CustomFunction>>(`/functions/${encodeURIComponent(name)}`, changes)).data;
}

export async function deleteFunction(name: string): Promise<void> {
  await apiClient.delete(`/functions/${encodeURIComponent(name)}`);
}

/** Runs unsaved code with test values in the server's sandbox. */
export async function testFunction(
  draft: FunctionDraft,
  values: (string | number | boolean | null)[],
): Promise<FunctionTestResult> {
  return (
    await apiClient.post<DataEnvelope<FunctionTestResult>>("/functions/test", {
      arguments: draft.arguments,
      resultType: draft.resultType,
      code: draft.code,
      values,
    })
  ).data;
}

export async function getFunctionStatus(): Promise<FunctionStatus> {
  return (await apiClient.get<DataEnvelope<FunctionStatus>>("/functions/status")).data;
}
