import { apiClient } from "./client";

export type Problem = {
  id: number;
  loggedAt: string;
  /** slow: a query over the threshold; timeout: the database stopped it; error: an unexpected server error. */
  kind: "slow" | "timeout" | "error" | "import";
  method: string | null;
  path: string | null;
  status: number | null;
  durationMs: number;
  userId: number | null;
  username: string | null;
  via: string | null;
  /** The page the request came from, e.g. /dashboards/6?interval.net=week. */
  source: string | null;
  message: string;
  detail: string | null;
};

/** GET /api/v1/admin/problems — newest first, with the slow-query threshold. */
export async function listProblems(limit = 200): Promise<{ problems: Problem[]; slowMs: number }> {
  const response = await apiClient.get<{ data: Problem[]; meta: { slowMs: number } }>(`/admin/problems?limit=${limit}`);
  return { problems: response.data, slowMs: response.meta.slowMs };
}

/** DELETE /api/v1/admin/problems */
export async function clearProblems(): Promise<void> {
  await apiClient.delete(`/admin/problems`);
}
