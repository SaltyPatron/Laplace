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

const REQUEST_SUFFIX = /\s*\(request ([^)]+)\)\s*$/;

export interface ErrorTextProps extends HTMLAttributes<HTMLDivElement> {
  /** Correlation id of the failed request, shown under Details for support. */
  requestId?: string;
}

/**
 * The one error presentation: a plain message, with technical detail (the request id) tucked
 * into a disclosure. A message string ending in "(request <id>)" is split automatically.
 */
export const ErrorText = forwardRef<HTMLDivElement, ErrorTextProps>(function ErrorText(
  { className, children, requestId, ...props },
  ref,
) {
  let message = children;
  let id = requestId;
  if (typeof children === 'string') {
    const match = REQUEST_SUFFIX.exec(children);
    if (match) { message = children.slice(0, match.index); id = id ?? match[1]; }
  }
  return (
    <div ref={ref} role="alert" data-state="error" className={cn(styles.error, className)} {...props}>
      {message}
      {id ? (
        <details className={styles.errorDetails}>
          <summary>Details</summary>
          Request <code>{id}</code>. Quote this if you report the problem.
        </details>
      ) : null}
    </div>
  );
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
