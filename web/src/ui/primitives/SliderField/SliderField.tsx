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
}

export const SliderField = forwardRef<HTMLDivElement, SliderFieldProps>(function SliderField(
  { min, max, step = 1, value, onChange, className, disabled },
  ref,
) {
  const clamp = (n: number) => Math.min(max, Math.max(min, n));

  const set = (raw: string) => {
    if (raw === '') {
      onChange('');
      return;
    }
    const n = Number(raw);
    onChange(Number.isFinite(n) ? String(clamp(Math.round(n))) : raw);
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
      />
    </div>
  );
});
