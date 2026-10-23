import { apiClient } from "./client";

/** How a file is read; omitted fields are detected by the backend. */
export type ImportFileSettings = {
  encoding?: string;
  delimiter?: string;
  /** 0-based index of the header record. */
  headerLine?: number;
  decimalSeparator?: string;
  dateFormat?: string;
  /** Detected from the file when missing. */
  format?: ImportFormat;
  /** Excel: the sheet to read (the first when missing). */
  sheet?: string | null;
  /** JSON: where the list of records is, e.g. `data.transactions` (found when missing). */
  jsonPath?: string | null;
};

export type ImportFormat = "csv" | "xlsx" | "json";

export type ImportResolvedSettings = {
  encoding: string;
  delimiter: string;
  headerLine: number;
  decimalSeparator: string;
  dateFormat: string;
  format?: ImportFormat;
  sheet?: string | null;
  /** Excel: the workbook's sheets. */
  sheets?: string[] | null;
  jsonPath?: string | null;
};

export type ImportAnalyzedColumn = {
  index: number;
  name: string;
  samples: string[];
  inferredType: string;
  /** Empty cells in the column. */
  blank: number;
  /** Distinct values (case-insensitive) and up to 100 of them. */
  distinctCount: number;
  distinct: string[];
};

export type ImportAnalysis = {
  settings: ImportResolvedSettings;
  columns: ImportAnalyzedColumn[];
  preamble: string[][];
  previewRows: string[][];
  rowCount: number;
  options: {
    encodings: string[];
    delimiters: string[];
    decimalSeparators: string[];
    dateFormats: string[];
  };
};

/** A file column into a table column — or, with `value`, the same value for every row (e.g. the account of a bank export). */
export type ImportColumnMapping = { source?: string; target: string; value?: string };

export type ImportRunPayload = {
  content: string;
  fileName?: string;
  settings: ImportFileSettings;
  mapping: ImportColumnMapping[];
  keyColumns: string[];
  /** Also skip repeats within the file (filling a lookup table once per value). */
  distinctKeys?: boolean;
  /** With key columns: update rows whose other values changed at the source instead of skipping them. */
  updateChanged?: boolean;
  /** Values chosen per row in the review, by file line — e.g. categories (Step 4.8); they win over the file. */
  assign?: { column: string; values: Record<string, string> };
};

export type ImportRowStatus = "new" | "duplicate" | "changed" | "invalid";

export type ImportRowResult = {
  line: number;
  status: ImportRowStatus;
  display: Record<string, string | null>;
  errors: { field: string; message: string }[];
  /** A changed row: the values it has in the table now, for the columns that change. */
  before?: Record<string, string | null> | null;
};

export type ImportRunResult = {
  dryRun: boolean;
  total: number;
  new: number;
  duplicates: number;
  invalid: number;
  inserted: number;
  rows: ImportRowResult[];
  rowsTruncated: boolean;
  /** Rows in the table whose other values differ (written with `updateChanged`). */
  changed?: number;
  updated?: number;
};

export type ImportProfileConfig = {
  settings?: ImportFileSettings;
  mapping?: ImportColumnMapping[];
  keyColumns?: string[];
  updateChanged?: boolean;
};

export type ImportProfile = {
  id: number;
  name: string;
  tableName: string;
  config: ImportProfileConfig;
  createdAt: string;
  updatedAt: string;
};

type DataEnvelope<T> = { data: T };

const tablePath = (table: string) => `/import/${encodeURIComponent(table)}`;

/** POST /api/v1/import/analyze — detect the format (CSV, Excel, JSON), encoding, delimiter, header and locale. */
export async function analyzeImport(content: string, settings: ImportFileSettings = {}, fileName?: string): Promise<ImportAnalysis> {
  const response = await apiClient.post<DataEnvelope<ImportAnalysis>>("/import/analyze", { content, fileName, settings });
  return response.data;
}

/** POST /api/v1/import/{table}/preview — convert, de-duplicate and validate without writing. */
export async function previewImport(table: string, payload: ImportRunPayload): Promise<ImportRunResult> {
  const response = await apiClient.post<DataEnvelope<ImportRunResult>>(`${tablePath(table)}/preview`, payload);
  return response.data;
}

/** POST /api/v1/import/{table} — insert the new, valid rows in one transaction. */
export async function runImport(table: string, payload: ImportRunPayload): Promise<ImportRunResult> {
  const response = await apiClient.post<DataEnvelope<ImportRunResult>>(tablePath(table), payload);
  return response.data;
}

export async function listImportProfiles(table: string): Promise<ImportProfile[]> {
  const response = await apiClient.get<DataEnvelope<ImportProfile[]>>(`${tablePath(table)}/profiles`);
  return response.data;
}

export async function createImportProfile(table: string, name: string, config: ImportProfileConfig): Promise<ImportProfile> {
  const response = await apiClient.post<DataEnvelope<ImportProfile>>(`${tablePath(table)}/profiles`, { name, config });
  return response.data;
}

export async function updateImportProfile(table: string, id: number, name: string, config: ImportProfileConfig): Promise<ImportProfile> {
  const response = await apiClient.put<DataEnvelope<ImportProfile>>(`${tablePath(table)}/profiles/${id}`, { name, config });
  return response.data;
}

export async function deleteImportProfile(table: string, id: number): Promise<void> {
  await apiClient.delete<void>(`${tablePath(table)}/profiles/${id}`);
}

/** Reads a file as base64 (the backend needs the raw bytes to detect the encoding). */
export function readFileAsBase64(file: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => reject(reader.error ?? new Error("The file could not be read."));
    reader.onload = () => {
      const result = String(reader.result ?? "");
      resolve(result.slice(result.indexOf(",") + 1));
    };
    reader.readAsDataURL(file);
  });
}

// ── Import from a URL and saved imports (Step 5.1) ────────────────────────

/** A header sent with the request; `{{name}}` in the value (or the URL) is a credential from the vault. */
export type ImportHeader = { name: string; value: string };

export type ImportFetchResult = { content: string; fileName: string; contentType: string | null; bytes: number; finalUrl: string };

/** POST /api/v1/import/fetch — the server fetches the file (https, public addresses only) for the page to read like an upload. */
export async function fetchImportFile(url: string, headers: ImportHeader[]): Promise<ImportFetchResult> {
  const response = await apiClient.post<DataEnvelope<ImportFetchResult>>("/import/fetch", { url, headers });
  return response.data;
}

/** POST /api/v1/import/function — runs a function (Step 5.11); its rows come back like a fetched file. */
export async function runImportFunction(name: string): Promise<ImportFetchResult> {
  const response = await apiClient.post<DataEnvelope<ImportFetchResult>>("/import/function", { function: name });
  return response.data;
}

/** When a saved import runs on its own. */
export type ImportSchedule = { kind: "manual" } | { kind: "daily"; at: string; timeZone?: string } | { kind: "hours"; every: number };

export type ImportSourceConfig = {
  /** Where the file comes from: a URL, or (Step 5.11) a function whose result is the file. */
  url?: string | null;
  function?: string | null;
  headers?: ImportHeader[];
  settings?: ImportFileSettings;
  mapping: ImportColumnMapping[];
  keyColumns?: string[];
  updateChanged?: boolean;
  /** New rows get this column from the categorisation rules and earlier rows. */
  categoryColumn?: string | null;
  schedule?: ImportSchedule;
};

export type ImportSourceRun = {
  id: number;
  trigger: "schedule" | "manual";
  startedAt: string;
  finishedAt: string | null;
  status: "running" | "ok" | "failed";
  total: number;
  inserted: number;
  updated: number;
  duplicates: number;
  invalid: number;
  categorised: number;
  bytes: number;
  error: string | null;
};

export type ImportSource = {
  id: number;
  name: string;
  tableName: string;
  config: ImportSourceConfig;
  enabled: boolean;
  lastRun: ImportSourceRun | null;
  nextRunAt: string | null;
  createdAt: string;
  updatedAt: string;
};

const sourcesPath = (table: string) => `${tablePath(table)}/sources`;

export async function listImportSources(table: string): Promise<ImportSource[]> {
  const response = await apiClient.get<DataEnvelope<ImportSource[]>>(sourcesPath(table));
  return response.data;
}

export async function createImportSource(table: string, name: string, config: ImportSourceConfig): Promise<ImportSource> {
  const response = await apiClient.post<DataEnvelope<ImportSource>>(sourcesPath(table), { name, config });
  return response.data;
}

export async function updateImportSource(
  table: string,
  id: number,
  changes: { name?: string; config?: ImportSourceConfig; enabled?: boolean },
): Promise<ImportSource> {
  const response = await apiClient.put<DataEnvelope<ImportSource>>(`${sourcesPath(table)}/${id}`, changes);
  return response.data;
}

export async function deleteImportSource(table: string, id: number): Promise<void> {
  await apiClient.delete<void>(`${sourcesPath(table)}/${id}`);
}

/** Fetches and imports now; a failed run comes back with its error, not as an exception. */
export async function runImportSource(table: string, id: number): Promise<ImportSourceRun> {
  const response = await apiClient.post<DataEnvelope<ImportSourceRun>>(`${sourcesPath(table)}/${id}/run`, {});
  return response.data;
}

export async function listImportSourceRuns(table: string, id: number, limit = 10): Promise<ImportSourceRun[]> {
  const response = await apiClient.get<DataEnvelope<ImportSourceRun[]>>(`${sourcesPath(table)}/${id}/runs?limit=${limit}`);
  return response.data;
}
