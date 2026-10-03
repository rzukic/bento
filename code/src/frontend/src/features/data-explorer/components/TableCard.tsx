import { Link } from "react-router-dom";
import { Icon } from "../../../components/common/Icon";
import type { ModelTable } from "../../../types/api";

/** Same fallback palette as the schema canvas, for tables without a colour. */
const TABLE_DOTS = ["#3b82f6", "#14b8a6", "#8b5cf6", "#f59e0b", "#ec4899", "#22c55e"];

function tableColor(table: ModelTable, index: number): string {
  const layout = (table as ModelTable & { layout?: { color?: string | null } }).layout;
  return layout?.color || TABLE_DOTS[index % TABLE_DOTS.length]!;
}

function countLabel(count: number, singular: string): string {
  return `${count} ${singular}${count === 1 ? "" : "s"}`;
}

type TableRowProps = {
  table: ModelTable;
  /** Position in the schema, for the fallback colour. */
  index: number;
  rowCount: number | null | undefined;
  /** For a link table: the two tables it connects. */
  links?: [string, string] | null;
};

/**
 * One table in the Data overview: its colour (as on the schema canvas), its
 * name with the description or technical name under it, and how many rows
 * and columns it has.
 */
export function TableRow({ table, index, rowCount, links = null }: TableRowProps) {
  const displayName = table.displayName ?? table.name;
  const subline = links
    ? `${links[0]} ↔ ${links[1]}`
    : table.description || (table.displayName && table.displayName !== table.name ? table.name : null);

  return (
    <Link to={`/data/${table.name}`} className="table-row">
      <span className="table-row__dot" style={{ background: tableColor(table, index) }} aria-hidden="true" />
      <span className="table-row__text">
        <span className="table-row__name">{displayName}</span>
        {subline ? <span className="table-row__subline">{subline}</span> : null}
      </span>
      <span className="table-row__count">
        {rowCount === null || rowCount === undefined ? "—" : countLabel(rowCount, "row")}
      </span>
      <span className="table-row__columns">{countLabel(table.columns.length, "column")}</span>
      <span className="table-row__arrow" aria-hidden="true">
        <Icon name="chevron-right" size={14} />
      </span>
    </Link>
  );
}
