import { Muted, Text } from '@ui';
import styles from './StatCard.module.css';

export function StatCard({ label, value, sub }: { label: string; value: string; sub?: string }) {
  return (
    <div className={styles.card}>
      <Muted className={styles.label}>{label}</Muted>
      <Text className={styles.value}>{value}</Text>
      {sub ? <Muted className={styles.sub}>{sub}</Muted> : null}
    </div>
  );
}
