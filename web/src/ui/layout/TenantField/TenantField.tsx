import { Input } from '../../primitives/Input';
import { Label } from '../../primitives/Label';
import styles from './TenantField.module.css';

export interface TenantFieldProps {
  id?: string;
  value: string;
  onChange: (value: string) => void;
}

export function TenantField({ id = 'tenant', value, onChange }: TenantFieldProps) {
  return (
    <div className={styles.root}>
      <Label htmlFor={id} className={styles.label}>
        tenant
      </Label>
      <Input
        id={id}
        className={styles.input}
        value={value}
        onChange={(e) => onChange(e.target.value)}
      />
    </div>
  );
}
