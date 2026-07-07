import { forwardRef, type HTMLAttributes } from 'react';
import { cn } from '../../lib/cn';
import styles from './Text.module.css';

export type TextProps = HTMLAttributes<HTMLParagraphElement>;

export const Text = forwardRef<HTMLParagraphElement, TextProps>(function Text(
  { className, ...props },
  ref,
) {
  return <p ref={ref} className={cn(styles.text, className)} {...props} />;
});

export const Muted = forwardRef<HTMLParagraphElement, TextProps>(function Muted(
  { className, ...props },
  ref,
) {
  return <p ref={ref} className={cn(styles.muted, className)} {...props} />;
});

export const ErrorText = forwardRef<HTMLParagraphElement, TextProps>(function ErrorText(
  { className, ...props },
  ref,
) {
  return <p ref={ref} className={cn(styles.error, className)} {...props} />;
});

export const LoadingText = forwardRef<HTMLParagraphElement, TextProps>(function LoadingText(
  { className, children = 'Loading…', ...props },
  ref,
) {
  return (
    <p ref={ref} className={cn(styles.loading, className)} {...props}>
      {children}
    </p>
  );
});
