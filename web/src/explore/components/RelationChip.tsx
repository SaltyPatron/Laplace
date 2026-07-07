import { ConsensusBadge } from '@ui';
import styles from './RelationChip.module.css';

export function RelationChip({
  type,
  label,
  mu,
  witnesses,
}: {
  type: string;
  label?: string;
  mu?: number;
  witnesses?: number;
}) {
  return (
    <span className={styles.chip}>
      <strong>{type}</strong>
      {label ? <> → {label}</> : null}
      {(mu !== undefined || witnesses !== undefined) ? (
        <ConsensusBadge mu={mu} witnesses={witnesses} tone="explore" />
      ) : null}
    </span>
  );
}
