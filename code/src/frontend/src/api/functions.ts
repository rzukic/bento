import { apiClient } from "./client";

/** float, integer, currency, text, boolean, date, datetime — arguments may also be "any". */
export type FunctionType = "float" | "integer" | "currency" | "text" | "boolean" | "date" | "datetime" | "any";
export type ArgumentType = FunctionType;

/** "any" gives any JSON value: such functions run as import or price sources, not in formulas. */
export const RESULT_TYPES: FunctionType[] = ["float", "integer", "currency", "text", "boolean", "date", "datetime", "any"];
export const ARGUMENT_TYPES: ArgumentType[] = RESULT_TYPES;

export type FunctionArgument = { name: string; type: ArgumentType };

export type FunctionUsage = {
  kind: "column" | "dashboard" | "priceSource" | "importSource";
  name: string;
  table?: string | null;
  column?: string | null;
  dashboardId?: number | null;
  sourceId?: number | null;
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
  /** Hosts fetch may call (Step 4.2); empty: no network. */
  allowedHosts: string[];
  /** Stored credentials the code reads as credentials.name. */
  credentials: string[];
  /** No network and a declared result: callable as CUSTOM(…). */
  usableInFormulas: boolean;
  /** "rows" (Step 4.3b): one call per group of rows, arguments as lists — CUSTOM_ROWS(…). */
  mode?: FunctionMode;
};

/** "row": one call per row; "rows": one call per group of rows in order (CUSTOM_ROWS). */
export type FunctionMode = "row" | "rows";

export type FunctionVersion = {
  version: number;
  arguments: FunctionArgument[];
  resultType: FunctionType;
  code: string;
  createdAt: string;
  allowedHosts: string[];
  credentials: string[];
  mode?: FunctionMode;
};

export type FunctionDraft = {
  name?: string;
  description?: string | null;
  arguments: FunctionArgument[];
  resultType: FunctionType;
  code: string;
  allowedHosts: string[];
  credentials: string[];
  mode?: FunctionMode;
};

/** A starting point for a new function (e.g. prices from Yahoo Finance). */
export type FunctionTemplate = FunctionDraft & { id: string; title: string; description: string; name: string };

export type FunctionTestResult = {
  /** Rows mode and "any" results: a list or JSON object. */
  value: string | number | boolean | null | unknown[] | Record<string, unknown>;
  resultType: FunctionType;
  error: string | null;
  logs: string[];
  durationMs: number;
  /** Requests the run made (functions that call APIs). */
  requests: number;
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
export async function testFunction(draft: FunctionDraft, values: unknown[]): Promise<FunctionTestResult> {
  return (
    await apiClient.post<DataEnvelope<FunctionTestResult>>("/functions/test", {
      arguments: draft.arguments,
      resultType: draft.resultType,
      code: draft.code,
      allowedHosts: draft.allowedHosts,
      credentials: draft.credentials,
      mode: draft.mode ?? "row",
      values,
    })
  ).data;
}

export async function listFunctionTemplates(): Promise<FunctionTemplate[]> {
  return (await apiClient.get<DataEnvelope<FunctionTemplate[]>>("/functions/templates")).data;
}

export async function getFunctionStatus(): Promise<FunctionStatus> {
  return (await apiClient.get<DataEnvelope<FunctionStatus>>("/functions/status")).data;
}
