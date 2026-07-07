import { forwardRef, type AnchorHTMLAttributes } from 'react';
import { cn } from '../../lib/cn';
import styles from './Link.module.css';

export type LinkProps = AnchorHTMLAttributes<HTMLAnchorElement> & {
  variant?: 'default' | 'brand';
};

export const Link = forwardRef<HTMLAnchorElement, LinkProps>(function Link(
  { className, variant = 'default', ...props },
  ref,
) {
  return (
    <a
      ref={ref}
      className={cn(styles.link, variant === 'brand' && styles.brand, className)}
      {...props}
    />
  );
});
