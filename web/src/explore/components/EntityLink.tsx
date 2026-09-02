import { Link as RouterLink } from 'react-router-dom';
import styles from './EntityLink.module.css';

export function EntityLink({ idHex, label }: { idHex: string; label: string }) {
  return (
    <RouterLink to={`/explore/entity/${idHex}`} className={styles.link}>
      {label || 'Unrealized entity'}
    </RouterLink>
  );
}
