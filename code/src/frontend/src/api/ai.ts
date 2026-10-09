import { apiClient } from "./client";

type DataEnvelope<T> = { data: T };

/** Whether the AI actions show (Step 4.7). */
export type AiStatus = { enabled: boolean; configured: boolean; model: string; budgetLeft: boolean };

export type AiModelOption = { id: string; name: string; description: string };

export type AiSettings = {
  configured: boolean;
  enabled: boolean;
  model: string;
  monthlyLimitUsd: number;
  spentThisMonthUsd: number;
  models: AiModelOption[];
};

export type AiUsageRow = {
  id: number;
  calledAt: string;
  userId: number | null;
  username: string | null;
  feature: string;
  model: string;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens: number;
  costUsd: number;
  toolCalls: number;
  ok: boolean;
  error: string | null;
  durationMs: number;
};

export type ExplainRequest = {
  language: string;
  dashboardId?: number;
  dashboard?: string;
  title: string;
  type: string;
  /** The query the widget ran, with the range and filters in it. */
  query?: unknown;
  data?: unknown;
  result?: unknown;
  range?: unknown;
  filters?: unknown;
  point?: unknown;
  question?: string;
};

export type ExplainAnswer = { answer: string; model: string; costUsd: number; toolCalls: number; durationMs: number };

export async function getAiStatus(): Promise<AiStatus> {
  return (await apiClient.get<DataEnvelope<AiStatus>>("/ai/status")).data;
}

export async function getAiSettings(): Promise<AiSettings> {
  return (await apiClient.get<DataEnvelope<AiSettings>>("/ai/settings")).data;
}

/** apiKey: a new key; "" removes it; leave it out to keep the stored one. */
export async function saveAiSettings(changes: { apiKey?: string; model?: string; monthlyLimitUsd?: number; enabled?: boolean }): Promise<AiSettings> {
  return (await apiClient.put<DataEnvelope<AiSettings>>("/ai/settings", changes)).data;
}

export async function listAiUsage(limit = 50): Promise<AiUsageRow[]> {
  return (await apiClient.get<DataEnvelope<AiUsageRow[]>>(`/ai/usage?limit=${limit}`)).data;
}

export async function explain(request: ExplainRequest): Promise<ExplainAnswer> {
  return (await apiClient.post<DataEnvelope<ExplainAnswer>>("/ai/explain", request)).data;
}
