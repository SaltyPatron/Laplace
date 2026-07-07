import { cva, type VariantProps } from 'class-variance-authority';
import { forwardRef, type HTMLAttributes } from 'react';
import { cn } from '../../lib/cn';
import styles from './Chip.module.css';

const chipVariants = cva(styles.chip, {
  variants: {
    variant: {
      default: '',
      confirm: styles.confirm,
      refute: styles.refute,
      draw: styles.draw,
      engineOk: styles.engineOk,
      engineMissing: styles.engineMissing,
      source: styles.source,
    },
  },
  defaultVariants: { variant: 'default' },
});

export type ChipProps = HTMLAttributes<HTMLSpanElement> & VariantProps<typeof chipVariants>;

export const Chip = forwardRef<HTMLSpanElement, ChipProps>(function Chip(
  { className, variant, ...props },
  ref,
) {
  return <span ref={ref} className={cn(chipVariants({ variant }), className)} {...props} />;
});

export { chipVariants };
