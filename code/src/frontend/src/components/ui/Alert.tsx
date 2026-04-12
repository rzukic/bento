import type { ReactNode } from "react";
import { Icon, type IconName } from "../common/Icon";

type AlertTone = "info" | "success" | "warning" | "error";

type AlertProps = {
  title?: string;
  message: ReactNode;
  tone?: AlertTone;
};

function iconForTone(tone: AlertTone): IconName {
  switch (tone) {
    case "success":
      return "check";
    case "warning":
      return "activity";
    case "error":
      return "activity";
    default:
      return "activity";
  }
}

export function Alert({ title, message, tone = "info" }: AlertProps) {
  return (
    <div className={`alert alert--${tone}`} role={tone === "error" ? "alert" : "status"}>
      <Icon name={iconForTone(tone)} size={14} />
      <div className="alert__body">
        {title ? <div className="alert__title">{title}</div> : null}
        <div className="alert__message">{message}</div>
      </div>
    </div>
  );
}
