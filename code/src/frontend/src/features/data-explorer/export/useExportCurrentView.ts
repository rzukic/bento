import { useEffect, useRef, useState } from "react";
import {
  fetchRelatedRows,
  fetchTableRows,
  type ExplorerDataSource,
} from "../../../api/data";
import { useSchemaStore } from "../../../stores/schemaStore";
import type { ModelColumn, ModelTable } from "../../../types/api";
import type { ExplorerQuery } from "../../../types/explorer";
import {
  findTable,
  getDisplayColumnForTable,
  getReferencePrimaryKey,
  getReferenceTableName,
} from "../components/editors/referenceMetadata";
import {
  buildExportFilename,
  rowsToCsv,
  rowsToJson,
  type ExportFormat,
  type ReferenceDisplayMap,
} from "./serializers";

export const EXPORT_PAGE_SIZE = 1000;
export const EXPORT_ROW_LIMIT = 100_000;

type ExportPhase = "paging" | "references" | "download";

type ExportState = {
  isExporting: boolean;
  format: ExportFormat | null;
  phase: ExportPhase | null;
  fetchedRows: number;
  totalRows: number | null;
};

type ExportCompletedResult = {
  status: "completed";
  format: ExportFormat;
  filename: string;
  exportedRows: number;
  totalRows: number | null;
  truncated: boolean;
};

type ExportCancelledResult = {
  status: "cancelled";
  format: ExportFormat;
  exportedRows: number;
  totalRows: number | null;
};

export type ExportCurrentViewResult =
  | ExportCompletedResult
  | ExportCancelledResult;

type UseExportCurrentViewArgs = {
  table: ModelTable;
  columns: ModelColumn[];
  query: ExplorerQuery;
  source: ExplorerDataSource;
  viewName: string | null;
};

type ExportJob = {
  cancelled: boolean;
};

type ReferenceLookupPlan = {
  columnName: string;
  referenceTable: string;
  primaryKeyColumn: string;
  displayColumn: string;
  ids: string[];
};

export function useExportCurrentView({
  table,
  columns,
  query,
  source,
  viewName,
}: UseExportCurrentViewArgs) {
  const snapshot = useSchemaStore((state) => state.snapshot);
  const jobRef = useRef<ExportJob | null>(null);
  const [state, setState] = useState<ExportState>({
    isExporting: false,
    format: null,
    phase: null,
    fetchedRows: 0,
    totalRows: null,
  });

  useEffect(
    () => () => {
      if (jobRef.current) {
        jobRef.current.cancelled = true;
      }
    },
    [],
  );

  async function exportCurrentView(
    format: ExportFormat,
  ): Promise<ExportCurrentViewResult> {
    if (jobRef.current) {
      return {
        status: "cancelled",
        format,
        exportedRows: state.fetchedRows,
        totalRows: state.totalRows,
      };
    }

    const job: ExportJob = { cancelled: false };
    jobRef.current = job;
    setState({
      isExporting: true,
      format,
      phase: "paging",
      fetchedRows: 0,
      totalRows: null,
    });

    try {
      const rows: Record<string, unknown>[] = [];
      let page = 1;
      let totalRows: number | null = null;
      let totalPages = 1;
      let truncated = false;

      while (true) {
        if (job.cancelled) {
          return {
            status: "cancelled",
            format,
            exportedRows: rows.length,
            totalRows,
          };
        }

        const response = await fetchExportPage(table.name, source, {
          ...query,
          page,
          perPage: EXPORT_PAGE_SIZE,
        });

        if (job.cancelled) {
          return {
            status: "cancelled",
            format,
            exportedRows: rows.length,
            totalRows: response.meta.total,
          };
        }

        totalRows = response.meta.total;
        totalPages = response.meta.total_pages;
        const remaining = EXPORT_ROW_LIMIT - rows.length;
        if (response.data.length > remaining) {
          rows.push(...response.data.slice(0, remaining));
          truncated = true;
        } else {
          rows.push(...response.data);
        }

        setState({
          isExporting: true,
          format,
          phase: "paging",
          fetchedRows: rows.length,
          totalRows,
        });

        if (
          response.data.length === 0 ||
          page >= totalPages ||
          rows.length >= EXPORT_ROW_LIMIT
        ) {
          truncated =
            truncated ||
            (totalRows !== null && totalRows > rows.length) ||
            page < totalPages;
          break;
        }

        page += 1;
      }

      setState({
        isExporting: true,
        format,
        phase: "references",
        fetchedRows: rows.length,
        totalRows,
      });

      const referenceDisplays = await resolveReferenceDisplays(
        table,
        columns,
        rows,
        snapshot,
        job,
      );
      if (job.cancelled) {
        return {
          status: "cancelled",
          format,
          exportedRows: rows.length,
          totalRows,
        };
      }

      setState({
        isExporting: true,
        format,
        phase: "download",
        fetchedRows: rows.length,
        totalRows,
      });

      const filename = buildExportFilename(table.name, viewName, format);
      const contents =
        format === "csv"
          ? rowsToCsv(rows, columns, referenceDisplays)
          : rowsToJson(rows, columns, referenceDisplays);
      const mimeType =
        format === "csv" ? "text/csv;charset=utf-8" : "application/json";
      downloadExportFile(filename, contents, mimeType);

      return {
        status: "completed",
        format,
        filename,
        exportedRows: rows.length,
        totalRows,
        truncated,
      };
    } finally {
      if (jobRef.current === job) {
        jobRef.current = null;
      }
      setState({
        isExporting: false,
        format: null,
        phase: null,
        fetchedRows: 0,
        totalRows: null,
      });
    }
  }

  function cancelExport() {
    if (jobRef.current) {
      jobRef.current.cancelled = true;
    }
  }

  return {
    ...state,
    exportCurrentView,
    cancelExport,
  };
}

async function fetchExportPage(
  tableName: string,
  source: ExplorerDataSource,
  query: ExplorerQuery,
) {
  return source.kind === "related"
    ? fetchRelatedRows(
        source.parentTable,
        source.parentRowId,
        source.accessor,
        query,
      )
    : fetchTableRows(tableName, query);
}

function buildReferenceLookupPlans(
  table: ModelTable,
  columns: ModelColumn[],
  rows: Record<string, unknown>[],
  snapshot: ReturnType<typeof useSchemaStore.getState>["snapshot"],
): ReferenceLookupPlan[] {
  const visibleColumns = new Set(columns.map((column) => column.name));
  const plans: ReferenceLookupPlan[] = [];

  for (const column of table.columns) {
    if (!visibleColumns.has(column.name)) continue;
    if (column.type !== "relation" && !column.references) continue;

    const referenceTable = getReferenceTableName(column);
    if (!referenceTable) continue;
    const referenceModel = findTable(snapshot, referenceTable);
    const displayColumn = getDisplayColumnForTable(referenceModel);
    const primaryKeyColumn = getReferencePrimaryKey(column);
    const ids = new Set<string>();

    for (const row of rows) {
      const value = row[column.name];
      if (value === null || value === undefined || value === "") continue;
      ids.add(String(value));
    }

    if (ids.size === 0) continue;

    plans.push({
      columnName: column.name,
      referenceTable,
      primaryKeyColumn,
      displayColumn,
      ids: [...ids],
    });
  }

  return plans;
}

async function resolveReferenceDisplays(
  table: ModelTable,
  columns: ModelColumn[],
  rows: Record<string, unknown>[],
  snapshot: ReturnType<typeof useSchemaStore.getState>["snapshot"],
  job: ExportJob,
): Promise<ReferenceDisplayMap> {
  if (!snapshot || rows.length === 0) {
    return {};
  }

  const plans = buildReferenceLookupPlans(table, columns, rows, snapshot);
  const entries: Array<[string, Record<string, string>]> = [];

  for (const plan of plans) {
    if (job.cancelled) {
      return {};
    }

    const response = await fetchTableRows(plan.referenceTable, {
      page: 1,
      perPage: Math.min(Math.max(plan.ids.length, 1), EXPORT_ROW_LIMIT),
      search: "",
      sort: [],
      filters: [
        {
          column: plan.primaryKeyColumn,
          op: "in",
          value: plan.ids.join(","),
        },
      ],
    });

    if (job.cancelled) {
      return {};
    }

    const displayById: Record<string, string> = {};
    for (const row of response.data) {
      const primaryKey = row[plan.primaryKeyColumn];
      if (primaryKey === null || primaryKey === undefined) continue;
      const displayValue = row[plan.displayColumn];
      displayById[String(primaryKey)] =
        displayValue === null || displayValue === undefined
          ? String(primaryKey)
          : String(displayValue);
    }

    entries.push([plan.columnName, displayById]);
  }

  return Object.fromEntries(entries);
}

function downloadExportFile(
  filename: string,
  contents: string,
  mimeType: string,
) {
  const blob = new Blob([contents], { type: mimeType });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = filename;
  anchor.style.display = "none";
  document.body.append(anchor);
  anchor.click();
  anchor.remove();
  window.setTimeout(() => {
    URL.revokeObjectURL(url);
  }, 0);
}
