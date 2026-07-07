import { forwardRef, type InputHTMLAttributes } from 'react';
import { cn } from '../../lib/cn';
import styles from './Input.module.css';

export type InputProps = InputHTMLAttributes<HTMLInputElement> & {
  invalid?: boolean;
};

export const Input = forwardRef<HTMLInputElement, InputProps>(function Input(
  { className, invalid, ...props },
  ref,
) {
  return (
    <input
      ref={ref}
      className={cn(styles.input, invalid && styles.error, className)}
      {...props}
    />
  );
});
