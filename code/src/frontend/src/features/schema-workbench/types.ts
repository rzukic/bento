import type { SchemaChange } from "../../types/api";
import type { JsonDraftLocation } from "./draftJson";

export type WorkbenchDiagnostic = {
  code: string;
  detail?: string;
  id: string;
  location: JsonDraftLocation | null;
  locationExact: boolean;
  message: string;
  path: string | null;
  severity: "error" | "warning";
  source: "backend" | "preview" | "syntax";
};

export type PreviewChangeGroup = {
  id: "tables" | "columns" | "relations" | "indexes";
  title: string;
  changes: SchemaChange[];
};

export type VisualCanvasSelection =
  | {
      type: "table";
      tableName: string;
    }
  | {
      type: "column";
      tableName: string;
      columnName: string;
    }
  | {
      type: "relation";
      relationName: string;
    };
