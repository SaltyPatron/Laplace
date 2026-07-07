import { type ReactNode } from 'react';
import { cn } from '../../lib/cn';
import { ErrorText } from '../../primitives/Text';
import { Label } from '../../primitives/Label';
import { IconButton } from '../../primitives/Button';
import { Tooltip, TooltipContent, TooltipTrigger } from '../../primitives/Tooltip';
import styles from './Field.module.css';

export interface FieldProps {
  label: ReactNode;
  help?: string;
  error?: string | null;
  htmlFor?: string;
  layout?: 'column' | 'row';
  /** Shown beside label for slider fields (e.g. current value). */
  valueDisplay?: ReactNode;
  children: ReactNode;
  className?: string;
}

export function Field({
  label,
  help,
  error,
  htmlFor,
  layout = 'column',
  valueDisplay,
  children,
  className,
}: FieldProps) {
  const labelNode = (
    <span className={styles.labelRow}>
      {htmlFor ? <Label htmlFor={htmlFor}>{label}</Label> : <span>{label}</span>}
      {help && (
        <Tooltip>
          <TooltipTrigger asChild>
            <IconButton type="button" aria-label={`Help: ${label}`} className={styles.helpTrigger}>
              ?
            </IconButton>
          </TooltipTrigger>
          <TooltipContent>{help}</TooltipContent>
        </Tooltip>
      )}
    </span>
  );

  return (
    <div className={cn(styles.field, layout === 'row' && styles.row, className)}>
      {valueDisplay != null ? (
        <div className={styles.head}>
          {labelNode}
          <b className={styles.headValue}>{valueDisplay}</b>
        </div>
      ) : (
        labelNode
      )}
      {children}
      {error && <ErrorText role="alert">{error}</ErrorText>}
    </div>
  );
}
