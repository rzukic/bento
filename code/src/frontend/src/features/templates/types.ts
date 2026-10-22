import type { DashboardDocument } from "../../api/dashboards";
import type { FormDefinition } from "../../api/forms";
import type { SchemaColumn, SchemaRelation, SchemaTable } from "../../types/api";
import type { TemplateNames } from "./names";

export type CreateRow = (table: string, row: Record<string, unknown>) => Promise<Record<string, unknown>>;

/**
 * A template that is only data: tables and their links, dashboards, forms
 * and example rows. One installer sets up every such template.
 */
export type SimpleTemplate = {
  id: string;
  title: string;
  description: string;
  /** The template's tables by key → their name without a prefix. */
  base: TemplateNames;
  /** Prefixes to try when a name is taken. */
  prefixes: string[];
  tables(n: TemplateNames): SchemaTable[];
  /** Relation names are made unique against the model when installed. */
  relations(n: TemplateNames): SchemaRelation[];
  /** The first one opens after the install. */
  dashboards(n: TemplateNames): { name: string; dashboard: DashboardDocument }[];
  forms(n: TemplateNames): { name: string; form: FormDefinition }[];
  seed(n: TemplateNames, createRow: CreateRow, today: Date): Promise<void>;
};

export const idColumn: SchemaColumn = {
  name: "id",
  type: "integer",
  primaryKey: true,
};

export const at = (x: number, y: number, w: number, h: number) => ({
  x,
  y,
  w,
  h,
});

/** Creates rows one by one and returns their ids in order. */
export async function createAll(createRow: CreateRow, table: string, rows: Record<string, unknown>[]): Promise<unknown[]> {
  const ids: unknown[] = [];
  for (const row of rows) ids.push((await createRow(table, row)).id);
  return ids;
}
