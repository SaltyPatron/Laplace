import { type FormEvent, type ReactNode } from 'react';
import { Button } from '../../primitives/Button';
import { Input } from '../../primitives/Input';
import { ErrorText } from '../../primitives/Text';
import styles from './LookupRow.module.css';

export interface LookupRowProps {
  value: string;
  onChange: (value: string) => void;
  onSubmit: () => void;
  placeholder?: string;
  submitLabel?: string;
  error?: string | null;
  disabled?: boolean;
  children?: ReactNode;
}

export function LookupRow({
  value,
  onChange,
  onSubmit,
  placeholder,
  submitLabel = 'Go',
  error,
  disabled,
  children,
}: LookupRowProps) {
  const handleSubmit = (e: FormEvent) => {
    e.preventDefault();
    onSubmit();
  };

  return (
    <form className={styles.row} onSubmit={handleSubmit}>
      <Input
        className={styles.input}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
        disabled={disabled}
        invalid={!!error}
      />
      <Button type="submit" disabled={disabled}>
        {submitLabel}
      </Button>
      {children}
      {error && <ErrorText role="alert">{error}</ErrorText>}
    </form>
  );
}
