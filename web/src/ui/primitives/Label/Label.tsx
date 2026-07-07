import { forwardRef, type LabelHTMLAttributes } from 'react';
import { cn } from '../../lib/cn';
import styles from './Label.module.css';

export type LabelProps = LabelHTMLAttributes<HTMLLabelElement> & {
  required?: boolean;
};

export const Label = forwardRef<HTMLLabelElement, LabelProps>(function Label(
  { className, required, ...props },
  ref,
) {
  return (
    <label ref={ref} className={cn(styles.label, required && styles.required, className)} {...props} />
  );
});
