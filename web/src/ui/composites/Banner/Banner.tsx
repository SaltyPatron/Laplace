import { type HTMLAttributes, type ReactNode } from 'react';
import { cn } from '../../lib/cn';
import styles from './Banner.module.css';

export interface BannerProps extends HTMLAttributes<HTMLDivElement> {
  variant?: 'info' | 'warning';
  children: ReactNode;
}

export function Banner({ variant = 'info', className, children, ...props }: BannerProps) {
  return (
    <div
      className={cn(styles.banner, variant === 'info' ? styles.info : styles.warning, className)}
      role="status"
      {...props}
    >
      {children}
    </div>
  );
}
