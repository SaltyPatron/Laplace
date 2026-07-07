import { type ReactNode } from 'react';
import { cn } from '../../lib/cn';
import styles from './Sidebar.module.css';

export function Sidebar({ children, className }: { children: ReactNode; className?: string }) {
  return <aside className={cn(styles.sidebar, className)}>{children}</aside>;
}
