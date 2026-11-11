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
  /** A word to mark wherever it occurs in the label as a whole word (a container shown for that word). */
  highlight?: string;
  className?: string;
}

const escape = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
/** The label with each whole-word, case-insensitive occurrence of `word` in a <mark>. */
function marked(label: string, word: string | undefined) {
  if (!word?.trim()) return label;
  const parts = label.split(new RegExp(`(?<![\\p{L}\\p{N}])(${escape(word.trim())})(?![\\p{L}\\p{N}])`, 'giu'));
  return parts.length === 1 ? label : parts.map((part, index) => index % 2 ? <mark key={index} className={styles.mark}>{part}</mark> : part);
}

/** An entity anywhere in the UI. Click opens it in the inspector drawer without leaving the page. */
export function EntityChip({ id, label, tier, showTier = false, highlight, className }: EntityChipProps) {
  const { open, current } = useInspector();
  const active = current === id.toLowerCase();
  return (
    <ToggleButton className={cn(styles.chip, active && styles.active, className)} data-entity-id={id}
      pressed={active} title={`Open “${label}” in the inspector`} onClick={() => open(id, label)}>
      <span className={styles.label}>{label ? marked(label, highlight) : id.slice(0, 8)}</span>
      {showTier && tier != null && <span className={styles.tier} aria-label={`tier ${tier}`}>T{tier}</span>}
    </ToggleButton>
  );
}
