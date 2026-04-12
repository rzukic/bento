import type { ModelColumn, ModelTable } from "../../../types/api";

type ReadOnlyDataGridProps = {
  table: ModelTable;
  rows: Record<string, unknown>[];
  page: number;
  perPage: number;
};

function formatTemporalValue(value: string, type: ModelColumn["type"]): string {
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) {
    return value;
  }

  if (type === "date") {
    return new Intl.DateTimeFormat(undefined, {
      year: "numeric",
      month: "short",
      day: "2-digit"
    }).format(parsed);
  }

  return new Intl.DateTimeFormat(undefined, {
    year: "numeric",
    month: "short",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit"
  }).format(parsed);
}

function getJsonPreview(value: unknown): string {
  try {
    const raw = JSON.stringify(value);
    if (!raw) return "—";
    return raw.length > 80 ? `${raw.slice(0, 77)}...` : raw;
  } catch {
    return String(value);
  }
}

function renderCellValue(column: ModelColumn, value: unknown) {
  if (value === null || value === undefined || value === "") {
    return <span className="data-grid__cell-empty">—</span>;
  }

  if (typeof value === "boolean") {
    return (
      <span
        className={`data-grid__boolean data-grid__boolean--${value ? "true" : "false"}`}
      >
        {value ? "True" : "False"}
      </span>
    );
  }

  if (typeof value === "number") {
    return <span className="data-grid__mono">{value}</span>;
  }

  if (typeof value === "string" && (column.type === "date" || column.type === "datetime")) {
    return <span className="data-grid__mono">{formatTemporalValue(value, column.type)}</span>;
  }

  if (column.type === "json" || typeof value === "object") {
    return (
      <code className="data-grid__code" title={getJsonPreview(value)}>
        {getJsonPreview(value)}
      </code>
    );
  }

  if (column.type === "relation" || column.references) {
    return (
      <span className="data-grid__reference" title={String(value)}>
        {String(value)}
      </span>
    );
  }

  if (column.primaryKey) {
    return <span className="data-grid__mono">{String(value)}</span>;
  }

  return <span>{String(value)}</span>;
}

export function ReadOnlyDataGrid({
  table,
  rows,
  page,
  perPage
}: ReadOnlyDataGridProps) {
  const startRowNumber = (page - 1) * perPage + 1;

  return (
    <div className="data-grid">
      <div className="data-grid__scroll">
        <table>
          <thead>
            <tr>
              <th className="data-grid__rownum data-grid__rownum--head" scope="col">
                #
              </th>
              {table.columns.map((column) => (
                <th key={column.name} scope="col">
                  <div className="data-grid__header-cell">
                    <span className="data-grid__header-name">
                      {column.displayName ?? column.name}
                    </span>
                    <span className="data-grid__header-type">
                      {column.primaryKey ? "PK · " : ""}
                      {column.references || column.type === "relation" ? "FK · " : ""}
                      {column.type}
                    </span>
                  </div>
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {rows.map((row, index) => (
              <tr key={`${table.name}-row-${index}`}>
                <td className="data-grid__rownum">{startRowNumber + index}</td>
                {table.columns.map((column) => (
                  <td key={`${table.name}-${index}-${column.name}`}>
                    <div className="data-grid__cell">
                      {renderCellValue(column, row[column.name])}
                    </div>
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
