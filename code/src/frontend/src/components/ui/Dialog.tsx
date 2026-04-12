import { useEffect, type ReactNode } from "react";
import { Button } from "./Button";

type DialogProps = {
  open: boolean;
  title: string;
  description?: string;
  onClose: () => void;
  children?: ReactNode;
  footer?: ReactNode;
};

export function Dialog({ open, title, description, onClose, children, footer }: DialogProps) {
  useEffect(() => {
    if (!open) return undefined;

    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") {
        onClose();
      }
    }

    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [open, onClose]);

  if (!open) return null;

  return (
    <div className="dialog" role="presentation">
      <button
        type="button"
        className="dialog__backdrop"
        aria-label="Close dialog"
        onClick={onClose}
      />
      <div className="dialog__panel" role="dialog" aria-modal="true" aria-labelledby="dialog-title">
        <header className="dialog__header">
          <h2 id="dialog-title" className="dialog__title">
            {title}
          </h2>
          {description ? <p className="dialog__description">{description}</p> : null}
        </header>
        {children ? <div className="dialog__body">{children}</div> : null}
        {footer ? <footer className="dialog__footer">{footer}</footer> : null}
      </div>
    </div>
  );
}

type ConfirmDialogProps = {
  open: boolean;
  title: string;
  description: string;
  confirmLabel?: string;
  cancelLabel?: string;
  confirmVariant?: "primary" | "danger";
  onConfirm: () => void;
  onCancel: () => void;
};

export function ConfirmDialog({
  open,
  title,
  description,
  confirmLabel = "Confirm",
  cancelLabel = "Cancel",
  confirmVariant = "primary",
  onConfirm,
  onCancel
}: ConfirmDialogProps) {
  return (
    <Dialog
      open={open}
      title={title}
      description={description}
      onClose={onCancel}
      footer={
        <>
          <Button variant="ghost" onClick={onCancel}>
            {cancelLabel}
          </Button>
          <Button variant={confirmVariant} onClick={onConfirm}>
            {confirmLabel}
          </Button>
        </>
      }
    />
  );
}
