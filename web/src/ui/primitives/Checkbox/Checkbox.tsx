import { forwardRef, type InputHTMLAttributes, type ReactNode } from 'react';
import { cn } from '../../lib/cn';
import styles from './Checkbox.module.css';

export type CheckboxProps = Omit<InputHTMLAttributes<HTMLInputElement>, 'type'> & {
  label?: ReactNode;
};

export const Checkbox = forwardRef<HTMLInputElement, CheckboxProps>(function Checkbox(
  { className, label, id, ...props },
  ref,
) {
  const input = (
    <input ref={ref} type="checkbox" id={id} className={cn(styles.input, className)} {...props} />
  );
  if (!label) return input;
  return (
    <label className={styles.root} htmlFor={id}>
      {input}
      {label}
    </label>
  );
});
