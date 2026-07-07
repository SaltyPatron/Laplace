import { forwardRef, type HTMLAttributes } from 'react';
import { cn } from '../../lib/cn';
import styles from './Stack.module.css';

export type StackProps = HTMLAttributes<HTMLDivElement> & {
  gap?: 1 | 2 | 3 | 4 | 5 | 6;
};

const gapClass: Record<NonNullable<StackProps['gap']>, string> = {
  1: styles.gap1,
  2: styles.gap2,
  3: styles.gap3,
  4: styles.gap4,
  5: styles.gap5,
  6: styles.gap6,
};

export const Stack = forwardRef<HTMLDivElement, StackProps>(function Stack(
  { gap = 3, className, ...props },
  ref,
) {
  return <div ref={ref} className={cn(styles.stack, gapClass[gap], className)} {...props} />;
});
