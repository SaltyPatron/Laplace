import { forwardRef, type HTMLAttributes } from 'react';
import { cn } from '../../lib/cn';
import styles from './Alert.module.css';

export type AlertProps = HTMLAttributes<HTMLDivElement> & {
  variant?: 'error' | 'success';
};

export const Alert = forwardRef<HTMLDivElement, AlertProps>(function Alert(
  { variant = 'error', className, ...props },
  ref,
) {
  return (
    <div
      ref={ref}
      role="alert"
      className={cn(styles.alert, variant === 'success' && styles.success, className)}
      {...props}
    />
  );
});
