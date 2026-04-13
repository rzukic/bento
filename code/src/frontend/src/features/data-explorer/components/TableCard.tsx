import { Link } from "react-router-dom";
import { Icon } from "../../../components/common/Icon";
import type { ModelTable } from "../../../types/api";

type TableCardProps = {
  table: ModelTable;
};

export function TableCard({ table }: TableCardProps) {
  const columnCount = table.columns.length;
  const pkColumns = table.columns.filter((c) => c.primaryKey);
  const fkColumns = table.columns.filter(
    (c) => c.type === "relation" || c.references,
  );
  const displayName = table.displayName ?? table.name;

  return (
    <Link to={`/data/${table.name}`} className="table-card">
      <div className="table-card__header">
        <div className="table-card__icon">
          <Icon name="table" size={15} />
        </div>
        <span className="table-card__name">{displayName}</span>
        <span className="table-card__arrow">
          <Icon name="chevron-right" size={14} />
        </span>
      </div>

      {table.description ? (
        <p className="table-card__description">{table.description}</p>
      ) : null}

      <div className="table-card__meta">
        <span className="table-card__meta-item">
          <Icon name="columns" size={12} />
          {columnCount} col{columnCount !== 1 ? "s" : ""}
        </span>
        {pkColumns.length > 0 ? (
          <span className="table-card__meta-item">
            PK: {pkColumns.map((c) => c.name).join(", ")}
          </span>
        ) : null}
        {fkColumns.length > 0 ? (
          <span className="table-card__meta-item">
            <Icon name="link" size={12} />
            {fkColumns.length} ref{fkColumns.length !== 1 ? "s" : ""}
          </span>
        ) : null}
      </div>
    </Link>
  );
}
