import { type ReactNode } from 'react';
import { cn } from '../../lib/cn';
import styles from './Panel.module.css';

export interface PanelProps {
  title?: ReactNode;
  actions?: ReactNode;
  className?: string;
  /** Stretch to fill a flex parent; last child grows into remaining space. */
  fill?: boolean;
  children: ReactNode;
}

export function Panel({ title, actions, className, fill = false, children }: PanelProps) {
  const hasHead = title != null || actions != null;
  return (
    <section className={cn(styles.panel, fill && styles.fill, className)}>
      {hasHead && (
        <header className={styles.head}>
          {title != null && <h3>{title}</h3>}
          {actions != null && <div className={styles.actions}>{actions}</div>}
        </header>
      )}
      {children}
    </section>
  );
}
