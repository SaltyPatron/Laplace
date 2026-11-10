import { Link as RouterLink, useLocation } from 'react-router-dom';
import { useEntityLabels } from '../../shell/inspector';
import { entityDestination } from './entityDestination';
import styles from './EntityLink.module.css';

export function EntityLink({ idHex, label }: { idHex: string; label: string }) {
  const location = useLocation();
  const remember = useEntityLabels((s) => s.remember);
  const fallbackLabel = idHex ? `Entity · ${idHex.slice(0, 12)}` : 'Entity';

  return (
    <RouterLink to={entityDestination(idHex, location)} className={styles.link} preventScrollReset
      onClick={() => { if (label && /^[0-9a-f]{32}$/i.test(idHex)) remember(idHex.toLowerCase(), label); }}>
      {label || fallbackLabel}
    </RouterLink>
  );
}
