import { forwardRef } from 'react';
import { cn } from '../../lib/cn';
import styles from './SegmentedControl.module.css';

export interface SegmentedControlProps {
  value: string;
  onValueChange: (value: string) => void;
  options: readonly string[];
  label: string;
  disabled?: boolean;
  className?: string;
}

export const SegmentedControl = forwardRef<HTMLDivElement, SegmentedControlProps>(
  function SegmentedControl({ value, onValueChange, options, label, disabled, className }, ref) {
    return (
      <div
        ref={ref}
        className={cn(styles.group, className)}
        role="radiogroup"
        aria-label={label}
      >
        {options.map((option) => (
          <SegmentedOption
            key={option}
            option={option}
            selected={value === option}
            disabled={disabled}
            onSelect={() => onValueChange(option)}
          />
        ))}
      </div>
    );
  },
);

function SegmentedOption({
  option,
  selected,
  disabled,
  onSelect,
}: {
  option: string;
  selected: boolean;
  disabled?: boolean;
  onSelect: () => void;
}) {
  return (
    <button
      type="button"
      role="radio"
      aria-checked={selected}
      disabled={disabled}
      className={cn(styles.option, selected && styles.selected)}
      onClick={onSelect}
    >
      {option}
    </button>
  );
}
