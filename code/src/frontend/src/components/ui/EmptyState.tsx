import type { ReactNode } from "react";
import { Icon, type IconName } from "../common/Icon";

type EmptyStateProps = {
  title: string;
  description: string;
  bullets?: string[];
  icon?: IconName;
  actions?: ReactNode;
};

export function EmptyState({
  title,
  description,
  bullets = [],
  icon = "layers",
  actions
}: EmptyStateProps) {
  return (
    <div className="empty-state">
      <div className="empty-state__icon" aria-hidden="true">
        <Icon name={icon} size={18} />
      </div>
      <div className="empty-state__body">
        <div>
          <h2 className="empty-state__title">{title}</h2>
          <p className="empty-state__description">{description}</p>
        </div>
        {bullets.length > 0 ? (
          <ul className="empty-state__list">
            {bullets.map((bullet) => (
              <li key={bullet} className="empty-state__item">
                <Icon name="check" size={13} />
                <span>{bullet}</span>
              </li>
            ))}
          </ul>
        ) : null}
        {actions}
      </div>
    </div>
  );
}
