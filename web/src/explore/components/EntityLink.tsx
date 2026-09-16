import { Link as RouterLink } from 'react-router-dom';
import styles from './EntityLink.module.css';

export function EntityLink({ idHex, label }: { idHex: string; label: string }) {
  const fallbackLabel = idHex ? `Entity · ${idHex.slice(0, 12)}` : 'Entity';

  return (
    <RouterLink to={`/explore/entity/${idHex}`} className={styles.link}>
      {label || fallbackLabel}
    </RouterLink>
  );
}
