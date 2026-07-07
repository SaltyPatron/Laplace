import { cva, type VariantProps } from 'class-variance-authority';
import type { HTMLAttributes } from 'react';
import { cn } from '../../lib/cn';
import chipStyles from './Chip.module.css';

const chipVariants = cva(chipStyles.base, {
  variants: {
    variant: {
      default: chipStyles.default,
      confirm: chipStyles.confirm,
      refute: chipStyles.refute,
      draw: chipStyles.draw,
      engineOk: chipStyles.engineOk,
      engineMissing: chipStyles.engineMissing,
    },
  },
  defaultVariants: {
    variant: 'default',
  },
});

export interface ChipProps extends HTMLAttributes<HTMLSpanElement>, VariantProps<typeof chipVariants> {}

export function Chip({ className, variant, ...props }: ChipProps) {
  return <span className={cn(chipVariants({ variant }), className)} {...props} />;
}

export { chipVariants };
