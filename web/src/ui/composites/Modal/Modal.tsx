import { useEffect, useRef, type ReactNode } from 'react';
import { cn } from '../../lib/cn';
import styles from './Modal.module.css';

export interface ModalProps {
  open: boolean;
  onClose: () => void;
  title?: ReactNode;
  children: ReactNode;
  className?: string;
  actions?: ReactNode;
}

export function Modal({ open, onClose, title, children, className, actions }: ModalProps) {
  const panelRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', onKey);
    panelRef.current?.focus();
    return () => document.removeEventListener('keydown', onKey);
  }, [open, onClose]);

  if (!open) return null;

  return (
    <div className={styles.backdrop} role="presentation" onClick={onClose}>
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        tabIndex={-1}
        className={cn(styles.panel, className)}
        onClick={(e) => e.stopPropagation()}
      >
        {title && <h2>{title}</h2>}
        {children}
        {actions && <div className={styles.actions}>{actions}</div>}
      </div>
    </div>
  );
}
