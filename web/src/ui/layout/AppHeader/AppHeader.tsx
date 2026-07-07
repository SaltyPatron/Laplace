import { type ReactNode } from 'react';
import { cn } from '../../lib/cn';
import styles from './AppHeader.module.css';

export interface AppHeaderProps {
  title: ReactNode;
  tagline?: ReactNode;
  nav?: ReactNode;
  tenant?: ReactNode;
  className?: string;
}

export function AppHeader({ title, tagline, nav, tenant, className }: AppHeaderProps) {
  return (
    <header className={cn(styles.header, className)}>
      <h1 className={styles.title}>
        {title}
        {tagline && <span className={styles.tagline}>{tagline}</span>}
      </h1>
      {nav && <nav className={styles.nav}>{nav}</nav>}
      {tenant && <div className={styles.tenantSlot}>{tenant}</div>}
    </header>
  );
}
