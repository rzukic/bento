import { apiClient } from "./client";

/** Where a price source writes and how it runs (Step 4.2). */
export type PriceSourceConfig = {
  /** Fetches prices: (symbols, from, to) → [{ symbol, date, price, time? }]. */
  function: string;
  /** Optional: suggests symbols, (keys) → [{ key, symbol }] or { key: symbol }. */
  lookupFunction?: string | null;
  table: string;
  keyColumn: string;
  dateColumn: string;
  priceColumn: string;
  /** table.column the securities come from, e.g. finance_trades.isin. */
  keysFrom?: string | null;
  /** The provider's symbol per key. */
  symbols?: Record<string, string>;
  historyFrom?: string | null;
  freshMinutes?: number | null;
  dailyAt?: string | null;
  timeZone?: string | null;
  maxRequestsPerDay?: number | null;
};

export type PriceSourceKey = { key: string; symbol: string; mapped: boolean; lastDate: string | null; lastPrice: number | null };

export type PriceSourceStatus = {
  lastRunAt: string | null;
  lastSuccessAt: string | null;
  lastQuoteAt: string | null;
  lastError: string | null;
  requestsToday: number;
  running: boolean;
};

export type PriceSource = {
  id: number;
  name: string;
  enabled: boolean;
  config: PriceSourceConfig;
  status: PriceSourceStatus;
  keys: PriceSourceKey[];
};

export type PriceSourceRun = {
  id: number;
  kind: "latest" | "history" | "daily";
  startedAt: string;
  finishedAt: string | null;
  requests: number;
  rowsWritten: number;
  lastQuoteAt: string | null;
  error: string | null;
};

export type PriceRefresh = {
  changed: boolean;
  lastQuoteAt: string | null;
  sources: { id: number; name: string; table: string; fetched: boolean; rowsWritten: number; lastQuoteAt: string | null; error: string | null }[];
};

type DataEnvelope<T> = { data: T };

export async function listPriceSources(): Promise<PriceSource[]> {
  return (await apiClient.get<DataEnvelope<PriceSource[]>>("/price-sources")).data;
}

export async function createPriceSource(name: string, config: PriceSourceConfig, enabled = true): Promise<PriceSource> {
  return (await apiClient.post<DataEnvelope<PriceSource>>("/price-sources", { name, config, enabled })).data;
}

export async function updatePriceSource(id: number, changes: { name?: string; config?: PriceSourceConfig; enabled?: boolean }): Promise<PriceSource> {
  return (await apiClient.put<DataEnvelope<PriceSource>>(`/price-sources/${id}`, changes)).data;
}

export async function deletePriceSource(id: number): Promise<void> {
  await apiClient.delete(`/price-sources/${id}`);
}

export async function runPriceSource(id: number, kind: "latest" | "history" | "daily"): Promise<PriceSourceRun> {
  return (await apiClient.post<DataEnvelope<PriceSourceRun>>(`/price-sources/${id}/run?kind=${kind}`)).data;
}

export async function listPriceSourceRuns(id: number): Promise<PriceSourceRun[]> {
  return (await apiClient.get<DataEnvelope<PriceSourceRun[]>>(`/price-sources/${id}/runs`)).data;
}

export async function suggestSymbols(id: number): Promise<{ key: string; symbol: string }[]> {
  return (await apiClient.post<DataEnvelope<{ key: string; symbol: string }[]>>(`/price-sources/${id}/suggest`)).data;
}

/** Fetches prices older than their source's freshness window, for sources writing to these tables. */
export async function refreshPrices(tables: string[], force = false): Promise<PriceRefresh> {
  return (await apiClient.post<DataEnvelope<PriceRefresh>>("/price-sources/refresh", { tables, force })).data;
}
