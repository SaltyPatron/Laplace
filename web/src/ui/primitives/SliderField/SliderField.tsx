import { forwardRef, type ChangeEvent } from 'react';
import { cn } from '../../lib/cn';
import styles from './SliderField.module.css';

export interface SliderFieldProps {
  min: number;
  max: number;
  step?: number;
  value: string;
  onChange: (value: string) => void;
  className?: string;
  disabled?: boolean;
  /** Accessible name for both paired inputs; the surrounding Field label is visual only. */
  label?: string;
}

export const SliderField = forwardRef<HTMLDivElement, SliderFieldProps>(function SliderField(
  { min, max, step = 1, value, onChange, className, disabled, label },
  ref,
) {
  const clamp = (n: number) => Math.min(max, Math.max(min, n));

  // Snap to the control's own step. This was a bare Math.round, which is correct only while
  // every step is 1: a 0.1-step slider accepted 0.3 from the range handle and then rewrote it
  // to the minimum the moment the paired number box was touched.
  const decimals = (String(step).split('.')[1] ?? '').length;
  const snap = (n: number) => (decimals > 0 ? Number(n.toFixed(decimals)) : Math.round(n));

  const set = (raw: string) => {
    if (raw === '') {
      onChange('');
      return;
    }
    const n = Number(raw);
    onChange(Number.isFinite(n) ? String(clamp(snap(n))) : raw);
  };

  const onRange = (e: ChangeEvent<HTMLInputElement>) => onChange(e.target.value);
  const onNumber = (e: ChangeEvent<HTMLInputElement>) => set(e.target.value);
  const onBlur = (e: ChangeEvent<HTMLInputElement>) => set(e.target.value || String(min));

  return (
    <div ref={ref} className={cn(styles.root, className)}>
      <input
        type="range"
        className={styles.range}
        min={min}
        max={max}
        step={step}
        value={value === '' ? min : value}
        disabled={disabled}
        onChange={onRange}
        aria-label={label}
      />
      <input
        type="number"
        className={styles.number}
        min={min}
        max={max}
        step={step}
        value={value}
        disabled={disabled}
        onChange={onNumber}
        onBlur={onBlur}
        aria-label={label ? `${label} value` : undefined}
      />
    </div>
  );
});
