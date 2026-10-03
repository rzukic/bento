import type { ButtonHTMLAttributes, ReactNode } from "react";

type ButtonVariant = "default" | "primary" | "accent" | "ghost" | "danger";
type ButtonSize = "default" | "sm" | "lg" | "icon";

type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: ButtonVariant;
  size?: ButtonSize;
  leadingIcon?: ReactNode;
  trailingIcon?: ReactNode;
};

function getVariantClassName(variant: ButtonVariant): string {
  switch (variant) {
    case "primary":
      return "btn--primary";
    case "accent":
      return "btn--accent";
    case "ghost":
      return "btn--ghost";
    case "danger":
      return "btn--danger";
    default:
      return "";
  }
}

function getSizeClassName(size: ButtonSize): string {
  switch (size) {
    case "sm":
      return "btn--sm";
    case "lg":
      return "btn--lg";
    case "icon":
      return "btn--icon";
    default:
      return "";
  }
}

export function Button({
  variant = "default",
  size = "default",
  leadingIcon,
  trailingIcon,
  className = "",
  children,
  type = "button",
  ...props
}: ButtonProps) {
  const classes = ["btn", getVariantClassName(variant), getSizeClassName(size), className]
    .filter(Boolean)
    .join(" ");

  return (
    <button type={type} className={classes} {...props}>
      {leadingIcon}
      {children}
      {trailingIcon}
    </button>
  );
}
