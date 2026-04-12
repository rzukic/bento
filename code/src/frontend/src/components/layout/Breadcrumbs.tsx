import { Link } from "react-router-dom";
import "./Breadcrumbs.css";

export type BreadcrumbItem = {
  label: string;
  to?: string;
};

type BreadcrumbsProps = {
  items: BreadcrumbItem[];
};

export function Breadcrumbs({ items }: BreadcrumbsProps) {
  if (items.length === 0) {
    return null;
  }

  return (
    <nav className="breadcrumbs" aria-label="Breadcrumb">
      <ol className="breadcrumbs__list">
        {items.map((item, index) => {
          const isLast = index === items.length - 1;

          return (
            <li key={`${item.label}-${index}`} className="breadcrumbs__item">
              {item.to && !isLast ? (
                <Link className="breadcrumbs__link" to={item.to}>
                  {item.label}
                </Link>
              ) : (
                <span className="breadcrumbs__current">{item.label}</span>
              )}
            </li>
          );
        })}
      </ol>
    </nav>
  );
}
