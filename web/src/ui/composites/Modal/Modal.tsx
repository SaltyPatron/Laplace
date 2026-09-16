import { useEffect, useId, useRef, type ReactNode } from 'react';
import { cn } from '../../lib/cn';
import styles from './Modal.module.css';

export interface ModalProps {
  open: boolean;
  onClose: () => void;
  title?: ReactNode;
  /** Supply a useful name for a dialog without a visible title. */
  label?: string;
  children: ReactNode;
  className?: string;
  actions?: ReactNode;
}

/** Native top-layer modality owns focus trapping, Escape and opener restoration. */
export function Modal({ open, onClose, title, label, children, className, actions }: ModalProps) {
  const panelRef = useRef<HTMLDialogElement>(null);
  const titleId = useId();

  useEffect(() => {
    const panel = panelRef.current;
    if (!panel) return;
    if (open && !panel.open) panel.showModal();
    else if (!open && panel.open) panel.close();
    return () => { if (panel.open) panel.close(); };
  }, [open]);

  return (
    <dialog
      ref={panelRef}
      aria-labelledby={title != null ? titleId : undefined}
      aria-label={title == null ? label ?? 'Dialog' : undefined}
      className={cn(styles.panel, className)}
      onCancel={(event) => { event.preventDefault(); onClose(); }}
      onClose={() => { if (open && !panelRef.current?.open) onClose(); }}
      onClick={(event) => {
        if (event.target !== event.currentTarget) return;
        const box = event.currentTarget.getBoundingClientRect();
        if (event.clientX < box.left || event.clientX > box.right ||
            event.clientY < box.top || event.clientY > box.bottom) onClose();
      }}
    >
      {open && <>
        {title != null && <h2 id={titleId}>{title}</h2>}
        {children}
        {actions && <div className={styles.actions}>{actions}</div>}
      </>}
    </dialog>
  );
}
