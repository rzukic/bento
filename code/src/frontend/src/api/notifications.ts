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

// ── Rules (Step 5.3): when Bento speaks ────────────────────────────────────

export type RuleKind = "condition" | "target" | "event" | "report";
export type RulePeriod = "today" | "this-week" | "this-month" | "this-quarter" | "this-year";
export type RuleOp = ">" | ">=" | "<" | "<=" | "=" | "<>";

export type RuleConfig = {
  source?: string | null;
  formula?: string | null;
  period?: RulePeriod | null;
  dateColumn?: string | null;
  op?: RuleOp | null;
  threshold?: number | null;
  /** Conditions: hour or day. Reports: day, week, month, quarter or year. */
  every?: "hour" | "day" | ReportUnit | null;
  at?: string | null;
  dashboardId?: number | null;
  widgetId?: string | null;
  percent?: number | null;
  event?: "import.failed" | "price.failed" | null;
  /** Report: weekday 1 (Monday) … 7 for weekly ones, day of the month 1–28 otherwise. */
  day?: number | null;
  range?: "last" | "current" | null;
  compare?: "previous" | "year" | "none" | null;
  intro?: string | null;
  ai?: boolean | null;
  aiInstructions?: string | null;
  blocks?: ReportBlock[] | null;
};

// ── Reports (Step 5.3): a message on a schedule, made of blocks ───────────

export type ReportUnit = "day" | "week" | "month" | "quarter" | "year";
export type ReportBlockType = "value" | "top" | "dashboard" | "rules" | "text";
export type ReportFormat = "auto" | "currency" | "number" | "integer" | "percent";

export type ReportBlock = {
  type: ReportBlockType;
  label?: string | null;
  source?: string | null;
  formula?: string | null;
  /** The rows of the period by this date; none = all rows (no comparison). */
  dateColumn?: string | null;
  format?: ReportFormat | null;
  higherIsBetter?: boolean | null;
  groupBy?: string | null;
  limit?: number | null;
  order?: "desc" | "asc" | null;
  dashboardId?: number | null;
  /** Some of the dashboard's key figures; none = all. */
  widgets?: string[] | null;
  text?: string | null;
};

export type NotificationFact = {
  label: string;
  value: string;
  detail?: string | null;
  tone?: "good" | "bad" | null;
  number?: number | null;
  previous?: number | null;
};

export type NotificationSection = { title?: string | null; text?: string | null; facts?: NotificationFact[] | null };

/** What Bento says: a title, text, facts, and for reports sections. */
export type NotificationMessage = {
  event: string;
  title: string;
  text: string;
  link?: string | null;
  facts?: NotificationFact[] | null;
  sections?: NotificationSection[] | null;
};

export type RuleState = {
  active?: boolean;
  value?: string | null;
  checkedAt?: string | null;
  firedAt?: string | null;
  error?: string | null;
};

export type NotificationRule = {
  id: number;
  name: string;
  kind: RuleKind;
  config: RuleConfig;
  channels: number[];
  enabled: boolean;
  state: RuleState;
  summary: string;
  createdAt: string;
  updatedAt: string;
};

export type RuleCheck = {
  state: RuleState;
  fired: boolean;
  notification: NotificationMessage | null;
  deliveries: NotificationDelivery[];
};

const rulesPath = "/admin/notifications/rules";

export async function listRules(): Promise<NotificationRule[]> {
  return (await apiClient.get<DataEnvelope<NotificationRule[]>>(rulesPath)).data;
}

export async function createRule(name: string, kind: RuleKind, config: RuleConfig, channels: number[]): Promise<NotificationRule> {
  return (await apiClient.post<DataEnvelope<NotificationRule>>(rulesPath, { name, kind, config, channels })).data;
}

export async function updateRule(id: number, changes: { name?: string; config?: RuleConfig; channels?: number[]; enabled?: boolean }): Promise<NotificationRule> {
  return (await apiClient.put<DataEnvelope<NotificationRule>>(`${rulesPath}/${id}`, changes)).data;
}

export async function deleteRule(id: number): Promise<void> {
  await apiClient.delete<void>(`${rulesPath}/${id}`);
}

/** Checks now; speaks if the condition newly holds. */
export async function checkRule(id: number): Promise<RuleCheck> {
  return (await apiClient.post<DataEnvelope<RuleCheck>>(`${rulesPath}/${id}/check`, {})).data;
}

/** Sends what the rule would say now, whether or not it holds. */
export async function testRule(id: number): Promise<RuleCheck> {
  return (await apiClient.post<DataEnvelope<RuleCheck>>(`${rulesPath}/${id}/test`, {})).data;
}

/** A report as it would go out today, from a configuration not saved yet (without the AI's sentences). */
export async function previewReport(name: string, config: RuleConfig): Promise<NotificationMessage> {
  return (await apiClient.post<DataEnvelope<NotificationMessage>>(`${rulesPath}/preview`, { name, config })).data;
}
