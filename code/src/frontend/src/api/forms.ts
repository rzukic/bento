import { apiClient } from "./client";

/** How a field starts: today's date, the time now, the value used last time, or a fixed value. */
export type FieldDefault = { kind: "today" | "now" | "last" } | { kind: "value"; value: unknown };

/** A column the form asks for. */
export type FormField = {
  column: string;
  /** Shown instead of the column's name. */
  label?: string;
  /** A line under the field. */
  help?: string;
  /** Not shown; filled by its default. */
  hidden?: boolean;
  default?: FieldDefault;
  /** Numbers: a +/− switch starting at this sign (spending is negative). */
  sign?: "negative" | "positive";
};

/** A heading between fields. */
export type FormSection = { section: string };

export type FormItem = FormField | FormSection;

/** Form JSON (Step 4.4, Part 10): the table a form adds rows to and its fields in order. */
export type FormDefinition = {
  table: string;
  fields: FormItem[];
  /** Listed under "Quick add" in the sidebar and on dashboards that show the table. */
  pinned?: boolean;
};

export type SavedForm = {
  id: number;
  name: string;
  table: string;
  isShared: boolean;
  isOwner: boolean;
  form: FormDefinition;
  createdAt: string;
  updatedAt: string;
};

type DataEnvelope<T> = { data: T };

export function isSection(item: FormItem): item is FormSection {
  return "section" in item;
}

export async function listForms(): Promise<SavedForm[]> {
  return (await apiClient.get<DataEnvelope<SavedForm[]>>("/forms")).data;
}

export async function getForm(id: number): Promise<SavedForm> {
  return (await apiClient.get<DataEnvelope<SavedForm>>(`/forms/${id}`)).data;
}

export async function createForm(name: string, form: FormDefinition, isShared = false): Promise<SavedForm> {
  return (await apiClient.post<DataEnvelope<SavedForm>>("/forms", { name, form, isShared })).data;
}

export async function updateForm(id: number, changes: { name?: string; form?: FormDefinition; isShared?: boolean }): Promise<SavedForm> {
  return (await apiClient.put<DataEnvelope<SavedForm>>(`/forms/${id}`, changes)).data;
}

export async function deleteForm(id: number): Promise<void> {
  await apiClient.delete(`/forms/${id}`);
}
