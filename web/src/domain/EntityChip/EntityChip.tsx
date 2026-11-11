import { useInspector } from '../../shell/inspector';
import { cn } from '../../ui/lib/cn';
import { ToggleButton } from '../../ui/primitives/ToggleButton';
import styles from './EntityChip.module.css';

export interface EntityChipProps {
  id: string;
  label: string;
  /** Merkle height; shown only when asked, as a number, never as a role. */
  tier?: number;
  showTier?: boolean;
  className?: string;
}

/** An entity anywhere in the UI. Click opens it in the inspector drawer without leaving the page. */
export function EntityChip({ id, label, tier, showTier = false, className }: EntityChipProps) {
  const { open, current } = useInspector();
  const active = current === id.toLowerCase();
  return (
    <ToggleButton className={cn(styles.chip, active && styles.active, className)} data-entity-id={id}
      pressed={active} title={`Open “${label}” in the inspector`} onClick={() => open(id, label)}>
      <span className={styles.label}>{label || id.slice(0, 8)}</span>
      {showTier && tier != null && <span className={styles.tier} aria-label={`tier ${tier}`}>T{tier}</span>}
    </ToggleButton>
  );
}
