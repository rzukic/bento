import { apiClient } from "./client";

type DataEnvelope<T> = { data: T };

// ── Apps (Step 5.4): doors from calendar programs to Bento's tables ───────

export type AppFilter = { column: string; op: "eq" | "neq" | "gt" | "gte" | "lt" | "lte" | "like" | "in" | "null" | "notnull"; value?: string | null };

/** One calendar: the rows of a table as events. */
export type AppCalendar = {
  name: string;
  /** Its part of the addresses, set by the server from the name and kept when renamed. */
  slug?: string;
  table: string;
  title: string;
  start: string;
  end?: string | null;
  allDay?: string | null;
  description?: string | null;
  location?: string | null;
  /** A text column with the repetition (an iCal RRULE such as FREQ=WEEKLY;BYDAY=MO). */
  recurrence?: string | null;
  /** Minutes, for timed events without an end. */
  duration?: number | null;
  color?: string | null;
  filters?: AppFilter[] | null;
};

export type AppPassword = { id: number; name: string; prefix: string; createdAt: string; lastUsedAt: string | null };

export type BentoApp = {
  id: number;
  name: string;
  kind: "calendar";
  config: { calendars: AppCalendar[] };
  enabled: boolean;
  passwords: AppPassword[];
  createdAt: string;
  updatedAt: string;
};

export type AppLink = { calendar: string; kind: "ical" | "caldav"; url: string };

export type CreatedAppPassword = { password: AppPassword; secret: string; links: AppLink[] };

const path = "/admin/apps";

export async function listApps(): Promise<BentoApp[]> {
  return (await apiClient.get<DataEnvelope<BentoApp[]>>(path)).data;
}

export async function createApp(name: string, calendars: AppCalendar[]): Promise<BentoApp> {
  return (await apiClient.post<DataEnvelope<BentoApp>>(path, { name, kind: "calendar", config: { calendars } })).data;
}

export async function updateApp(id: number, changes: { name?: string; calendars?: AppCalendar[]; enabled?: boolean }): Promise<BentoApp> {
  const body = { name: changes.name, enabled: changes.enabled, config: changes.calendars ? { calendars: changes.calendars } : undefined };
  return (await apiClient.put<DataEnvelope<BentoApp>>(`${path}/${id}`, body)).data;
}

export async function deleteApp(id: number): Promise<void> {
  await apiClient.delete<void>(`${path}/${id}`);
}

/** A password for one device: shown once, with the addresses to enter there. */
export async function createAppPassword(id: number, name: string): Promise<CreatedAppPassword> {
  return (await apiClient.post<DataEnvelope<CreatedAppPassword>>(`${path}/${id}/passwords`, { name })).data;
}

export async function revokeAppPassword(id: number, passwordId: number): Promise<void> {
  await apiClient.delete<void>(`${path}/${id}/passwords/${passwordId}`);
}
