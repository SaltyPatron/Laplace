import { type FormEvent, type ReactNode, type Ref, useId } from 'react';
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
  busy?: boolean;
  submitDisabled?: boolean;
  ariaLabel?: string;
  inputRef?: Ref<HTMLInputElement>;
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
  busy = false,
  submitDisabled = false,
  ariaLabel,
  inputRef,
  children,
}: LookupRowProps) {
  const errorId = useId();
  const handleSubmit = (e: FormEvent) => {
    e.preventDefault();
    if (disabled || busy || submitDisabled) return;
    onSubmit();
  };

  return (
    <form className={styles.row} onSubmit={handleSubmit}>
      <div className={styles.controls}>
        <Input
          ref={inputRef}
          className={styles.input}
          value={value}
          onChange={(e) => onChange(e.target.value)}
          placeholder={placeholder}
          disabled={disabled}
          invalid={!!error}
          aria-label={ariaLabel ?? placeholder}
          aria-describedby={error ? errorId : undefined}
        />
        <Button type="submit" disabled={disabled || submitDisabled} loading={busy}>
          {submitLabel}
        </Button>
        {children}
      </div>
      {error && <ErrorText id={errorId} className={styles.error} role="alert">{error}</ErrorText>}
    </form>
  );
}
