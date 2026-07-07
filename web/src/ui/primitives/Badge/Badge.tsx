import { cva, type VariantProps } from 'class-variance-authority';
import { forwardRef, type HTMLAttributes } from 'react';
import { cn } from '../../lib/cn';
import styles from './Badge.module.css';

const badgeVariants = cva(styles.badge, {
  variants: {
    variant: {
      default: '',
      mu: styles.mu,
      ord: styles.ord,
    },
  },
  defaultVariants: { variant: 'default' },
});

export type BadgeProps = HTMLAttributes<HTMLSpanElement> & VariantProps<typeof badgeVariants>;

export const Badge = forwardRef<HTMLSpanElement, BadgeProps>(function Badge(
  { className, variant, ...props },
  ref,
) {
  return <span ref={ref} className={cn(badgeVariants({ variant }), className)} {...props} />;
});

export { badgeVariants };
