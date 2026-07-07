import { Link as RouterLink } from 'react-router-dom';
import { Tooltip, TooltipContent, TooltipTrigger } from '@ui';
import styles from './EntityLink.module.css';

export function EntityLink({ idHex, label }: { idHex: string; label: string }) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <RouterLink to={`/explore/entity/${idHex}`} className={styles.link}>
          {label}
        </RouterLink>
      </TooltipTrigger>
      <TooltipContent>{idHex}</TooltipContent>
    </Tooltip>
  );
}
