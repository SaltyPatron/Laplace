import { Link as RouterLink, useLocation } from 'react-router-dom';
import { entityDestination } from './entityDestination';
import styles from './EntityLink.module.css';

export function EntityLink({ idHex, label }: { idHex: string; label: string }) {
  const location = useLocation();
  const fallbackLabel = idHex ? `Entity · ${idHex.slice(0, 12)}` : 'Entity';

  return (
    <RouterLink to={entityDestination(idHex, location)} className={styles.link} preventScrollReset>
      {label || fallbackLabel}
    </RouterLink>
  );
}
