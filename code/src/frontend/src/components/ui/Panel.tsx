import type { ElementType, HTMLAttributes, ReactNode } from "react";

type PanelProps = HTMLAttributes<HTMLElement> & {
  as?: ElementType;
  title?: string;
  description?: string;
  eyebrow?: string;
  actions?: ReactNode;
  bodyClassName?: string;
  muted?: boolean;
};

export function Panel({
  as,
  title,
  description,
  eyebrow,
  actions,
  bodyClassName = "",
  className = "",
  children,
  muted = false,
  ...props
}: PanelProps) {
  const Component = as ?? "section";

  return (
    <Component className={["panel", muted ? "panel--muted" : "", className].filter(Boolean).join(" ")} {...props}>
      {title || description || eyebrow || actions ? (
        <header className="panel__header">
          <div className="panel__heading">
            {eyebrow ? <div className="panel__eyebrow">{eyebrow}</div> : null}
            {title ? <h2 className="panel__title">{title}</h2> : null}
            {description ? <p className="panel__description">{description}</p> : null}
          </div>
          {actions ? <div className="panel__actions">{actions}</div> : null}
        </header>
      ) : null}
      <div className={["panel__body", bodyClassName].filter(Boolean).join(" ")}>{children}</div>
    </Component>
  );
}
