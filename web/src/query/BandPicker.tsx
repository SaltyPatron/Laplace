import { Muted, Tooltip, TooltipContent, TooltipTrigger } from '@ui';
import type { RelationBand } from './types';
import styles from './BandPicker.module.css';

interface Props {
  bands: RelationBand[];
  selected: number[];
  onChange: (next: number[]) => void;
}

/**
 * The lens. A band is a set of relation types sharing a read-time salience
 * rank, addressed by one precomputed highway mask — so selecting bands narrows
 * the scan rather than filtering after it. Counts are live: a band holding no
 * consensus is shown as empty rather than offered as a choice that returns
 * nothing.
 */
export function BandPicker({ bands, selected, onChange }: Props) {
  const toggle = (band: number) =>
    onChange(selected.includes(band) ? selected.filter((b) => b !== band) : [...selected, band].sort((a, b) => a - b));

  const populated = bands.filter((b) => b.consensus_rows > 0);
  const total = populated.reduce((sum, b) => sum + b.consensus_rows, 0);
  const covered = populated
    .filter((b) => selected.includes(b.band))
    .reduce((sum, b) => sum + b.consensus_rows, 0);

  return (
    <div className={styles.picker}>
      <div className={styles.head}>
        <span className={styles.title}>Lens</span>
        <Muted className={styles.summary}>
          {selected.length === 0
            ? `all bands · ${total.toLocaleString()} edges`
            : `${covered.toLocaleString()} of ${total.toLocaleString()} edges`}
        </Muted>
        {selected.length > 0 && (
          <button type="button" className={styles.clear} onClick={() => onChange([])}>
            clear
          </button>
        )}
      </div>

      <div className={styles.grid}>
        {populated.map((b) => {
          const on = selected.includes(b.band);
          return (
            <Tooltip key={b.band}>
              <TooltipTrigger asChild>
                <button
                  type="button"
                  role="switch"
                  aria-checked={on}
                  aria-label={`${b.name} band`}
                  className={`${styles.band} ${on ? styles.on : ''}`}
                  onClick={() => toggle(b.band)}
                  style={{ '--band-weight': b.rank } as React.CSSProperties}
                >
                  <span className={styles.bandName}>{b.name.replace(/_/g, ' ')}</span>
                  <span className={styles.bandCount}>{compact(b.consensus_rows)}</span>
                </button>
              </TooltipTrigger>
              <TooltipContent>
                band {b.band} · rank {b.rank.toFixed(2)} · {b.relation_types} relation
                {b.relation_types === 1 ? '' : 's'} · {b.consensus_rows.toLocaleString()} consensus rows
              </TooltipContent>
            </Tooltip>
          );
        })}
      </div>
    </div>
  );
}

function compact(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000) return `${Math.round(n / 1_000)}k`;
  return String(n);
}
