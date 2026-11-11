import { forwardRef, type ButtonHTMLAttributes } from 'react';
import { cn } from '../../lib/cn';
import styles from './UnstyledButton.module.css';

export type UnstyledButtonProps = ButtonHTMLAttributes<HTMLButtonElement>;

/**
 * A real `<button>` with no visual opinion: the caller's className owns the look.
 * Use it for feature-specific controls (list rows, move plies, rail items) that
 * still need native button semantics. `type` defaults to "button".
 */
export const UnstyledButton = forwardRef<HTMLButtonElement, UnstyledButtonProps>(function UnstyledButton(
  { className, type = 'button', ...props },
  ref,
) {
  return <button ref={ref} type={type} className={cn(styles.button, className)} {...props} />;
});
