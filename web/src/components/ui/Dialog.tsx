import { useEffect, useRef, type ReactNode } from 'react';
import { Button } from './Button';

export function ConfirmDialog({
  open,
  title,
  description,
  confirmLabel = 'Confirm',
  cancelLabel = 'Cancel',
  tone = 'danger',
  busy = false,
  onConfirm,
  onCancel,
}: {
  open: boolean;
  title: ReactNode;
  description: ReactNode;
  confirmLabel?: string;
  cancelLabel?: string;
  tone?: 'primary' | 'danger';
  busy?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const ref = useRef<HTMLDialogElement>(null);

  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;
    if (open && !dialog.open) dialog.showModal();
    if (!open && dialog.open) dialog.close();
  }, [open]);

  return (
    <dialog className="ui-dialog" ref={ref} onCancel={(event) => { event.preventDefault(); onCancel(); }}>
      <div className="ui-dialog__accent" aria-hidden="true" />
      <div className="ui-dialog__content">
        <h2>{title}</h2>
        <p>{description}</p>
        <div className="ui-dialog__actions">
          <Button variant="secondary" onClick={onCancel} disabled={busy}>{cancelLabel}</Button>
          <Button variant={tone} onClick={onConfirm} disabled={busy}>{busy ? 'Working…' : confirmLabel}</Button>
        </div>
      </div>
    </dialog>
  );
}
