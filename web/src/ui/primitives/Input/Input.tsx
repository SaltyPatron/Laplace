import { forwardRef, type InputHTMLAttributes } from 'react';
import { cn } from '../../lib/cn';
import styles from './Input.module.css';

export type InputProps = InputHTMLAttributes<HTMLInputElement> & {
  invalid?: boolean;
  /**
   * `default` is the bordered form field. `bare` drops the field chrome for inputs
   * embedded in a surface that draws its own frame (e.g. the command palette).
   */
  variant?: 'default' | 'bare';
};

export const Input = forwardRef<HTMLInputElement, InputProps>(function Input(
  { className, invalid, variant = 'default', ...props },
  ref,
) {
  return (
    <input
      ref={ref}
      className={cn(variant === 'bare' ? styles.bare : styles.input, invalid && styles.error, className)}
      {...props}
    />
  );
});
