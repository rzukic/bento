import { apiClient } from "./client";

type DataEnvelope<T> = { data: T };

// ── Share links (Step 5.6): a dashboard for anyone with the link ─────────

/** Fixed filter values by column: the visitor always sees the dashboard filtered so. */
export type ShareFilters = Record<string, (string | number | boolean)[]>;

export type ShareLink = {
  id: number;
  name: string;
  prefix: string;
  status: "active" | "expired" | "revoked";
  createdBy: string | null;
  createdAt: string;
  expiresAt: string | null;
  lastUsedAt: string | null;
  views: number;
  filters: ShareFilters | null;
  embed: boolean;
  /** A form: entries one visitor may send an hour. */
  perHour?: number | null;
  thanks?: string | null;
};

export type ShareKind = "dashboard" | "form";

const base = (kind: ShareKind, id: number) => (kind === "form" ? `/forms/${id}/shares` : `/dashboards/${id}/shares`);

export type CreatedShare = { link: ShareLink; url: string; embed: string | null };

/** The link's address on this site, and the code to embed it. */
function addresses(token: string, embeddable: boolean): { url: string; embed: string | null } {
  const url = `${window.location.origin}/s/${token}`;
  return {
    url,
    embed: embeddable ? `<iframe src="${url}?embed=1" width="100%" height="600" style="border:0" loading="lazy"></iframe>` : null,
  };
}

export async function listShares(kind: ShareKind, id: number): Promise<ShareLink[]> {
  return (await apiClient.get<DataEnvelope<ShareLink[]>>(base(kind, id))).data;
}

/** The address comes back once. */
export async function createShare(
  kind: ShareKind,
  id: number,
  share: { name: string; expiresInDays?: number; filters?: ShareFilters; embed: boolean; perHour?: number; thanks?: string },
): Promise<CreatedShare> {
  const response = await apiClient.post<{ data: ShareLink; token: string }>(base(kind, id), share);
  return { link: response.data, ...addresses(response.token, share.embed) };
}

export async function revokeShare(id: number): Promise<void> {
  await apiClient.delete(`/shares/${id}`);
}

/** For a link's visitor: what it shows and how numbers and dates read. */
export type ShareInfo = {
  kind: ShareKind;
  dashboardId: number | null;
  formId: number | null;
  /** A form: what the visitor reads after sending. */
  thanks: string | null;
  name: string;
  filters: ShareFilters | null;
  embed: boolean;
  expiresAt: string | null;
  settings: { locale: string | null; currency: string; timeZone: string; weekStart: string; yearStart: number };
};

export async function getShareInfo(): Promise<ShareInfo> {
  return (await apiClient.get<DataEnvelope<ShareInfo>>("/share")).data;
}
