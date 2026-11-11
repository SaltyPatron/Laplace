import { forwardRef, type InputHTMLAttributes } from 'react';
import { cn } from '../../lib/cn';
import styles from './Range.module.css';

export type RangeProps = Omit<InputHTMLAttributes<HTMLInputElement>, 'type'>;

/** A native range slider. Use SliderField when the value also needs an editable number box. */
export const Range = forwardRef<HTMLInputElement, RangeProps>(function Range({ className, ...props }, ref) {
  return <input ref={ref} type="range" className={cn(styles.range, className)} {...props} />;
});
