import { apiClient } from "./client";

/** Step 5.2: where Bento reaches you. Secrets (password, webhook address, signing secret) go in and never come back. */
export type ChannelKind = "email" | "slack" | "webhook";

export type ChannelConfig = {
  to?: string[];
  from?: string | null;
  smtpHost?: string | null;
  smtpPort?: number | null;
  smtpSecurity?: "starttls" | "ssl" | "none" | null;
  smtpUser?: string | null;
  /** Write-only: left out keeps the stored one, "" removes it. */
  smtpPassword?: string;
  url?: string;
  secret?: string;
};

export type NotificationDelivery = {
  id: number;
  event: string;
  title: string;
  status: "sent" | "failed";
  error: string | null;
  durationMs: number;
  createdAt: string;
};

export type NotificationChannel = {
  id: number;
  name: string;
  kind: ChannelKind;
  config: ChannelConfig;
  /** The addresses, or the webhook's host. */
  target: string;
  hasPassword: boolean;
  hasUrl: boolean;
  hasSecret: boolean;
  enabled: boolean;
  lastDelivery: NotificationDelivery | null;
  createdAt: string;
  updatedAt: string;
};

type DataEnvelope<T> = { data: T };
const path = "/admin/notifications/channels";

export async function listChannels(): Promise<NotificationChannel[]> {
  return (await apiClient.get<DataEnvelope<NotificationChannel[]>>(path)).data;
}

export async function createChannel(name: string, kind: ChannelKind, config: ChannelConfig): Promise<NotificationChannel> {
  return (await apiClient.post<DataEnvelope<NotificationChannel>>(path, { name, kind, config })).data;
}

export async function updateChannel(id: number, changes: { name?: string; config?: ChannelConfig; enabled?: boolean }): Promise<NotificationChannel> {
  return (await apiClient.put<DataEnvelope<NotificationChannel>>(`${path}/${id}`, changes)).data;
}

export async function deleteChannel(id: number): Promise<void> {
  await apiClient.delete<void>(`${path}/${id}`);
}

/** Sends a test; a failed one comes back with its error, not as an exception. */
export async function testChannel(id: number): Promise<NotificationDelivery> {
  return (await apiClient.post<DataEnvelope<NotificationDelivery>>(`${path}/${id}/test`, {})).data;
}

export async function listDeliveries(id: number, limit = 10): Promise<NotificationDelivery[]> {
  return (await apiClient.get<DataEnvelope<NotificationDelivery[]>>(`${path}/${id}/deliveries?limit=${limit}`)).data;
}
