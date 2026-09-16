import { cloneElement, isValidElement, useId, type AriaAttributes, type ReactNode } from 'react';
import { cn } from '../../lib/cn';
import { ErrorText, Muted } from '../../primitives/Text';
import { Label } from '../../primitives/Label';
import { Input } from '../../primitives/Input';
import { Select } from '../../primitives/Select';
import { TextArea } from '../../primitives/TextArea';
import styles from './Field.module.css';

export interface FieldProps {
  label: ReactNode;
  help?: string;
  error?: string | null;
  /** Supply this for compound/custom controls; single shared controls bind automatically. */
  htmlFor?: string;
  layout?: 'column' | 'row';
  valueDisplay?: ReactNode;
  children: ReactNode;
  className?: string;
}

type ControlBinding = AriaAttributes & { id?: string };
const labelableTags = new Set(['input', 'select', 'textarea', 'output', 'button', 'meter', 'progress']);

export function Field({ label, help, error, htmlFor, layout = 'column', valueDisplay, children, className }: FieldProps) {
  const id = useId();
  const child = isValidElement<ControlBinding>(children) ? children : null;
  const singleControl = child && (typeof child.type === 'string'
    ? labelableTags.has(child.type)
    : [Input, Select, TextArea].some((component) => child.type === component));
  const controlId = htmlFor ?? child?.props.id ?? (singleControl ? `${id}-control` : undefined);
  const describedBy = [child?.props['aria-describedby'], help && `${id}-help`, error && `${id}-error`]
    .filter(Boolean).join(' ') || undefined;
  const control = child && controlId && (!child.props.id || child.props.id === controlId) ? cloneElement(child, {
    id: controlId,
    'aria-describedby': describedBy,
    'aria-invalid': error ? true : child.props['aria-invalid'],
  }) : children;
  const labelNode = <span className={styles.labelRow}>
    {controlId ? <Label htmlFor={controlId}>{label}</Label> : <span>{label}</span>}
  </span>;

  return <div className={cn(styles.field, layout === 'row' && styles.row, className)}>
    {valueDisplay != null ? <div className={styles.head}>{labelNode}<b className={styles.headValue}>{valueDisplay}</b></div> : labelNode}
    {control}
    {help && <Muted id={`${id}-help`}>{help}</Muted>}
    {error && <ErrorText id={`${id}-error`} role="alert">{error}</ErrorText>}
  </div>;
}
