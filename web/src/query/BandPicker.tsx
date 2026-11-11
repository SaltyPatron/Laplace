import { Checkbox, Muted, UnstyledButton } from '@ui';
import type { RelationBand } from './types';
import styles from './BandPicker.module.css';

interface Props { bands: RelationBand[]; selected: number[]; onChange: (next: number[]) => void }
/** Band selectors constrain compatible reads; empty selection leaves the band mask open. */
export function BandPicker({ bands, selected, onChange }: Props) {
  const populated = bands.filter(band => band.consensus_rows > 0);
  return <div className={styles.picker}>
    <Muted>{selected.length ? `${selected.length} selected bands` : 'All bands; no band constraint'}</Muted>
    <div className={styles.grid}>{populated.map(band => <Checkbox key={band.band} checked={selected.includes(band.band)}
      label={`${band.name.replaceAll('_', ' ')} · ${band.consensus_rows.toLocaleString()} consensus rows`}
      onChange={event => onChange(event.target.checked ? [...selected, band.band].sort((a, b) => a - b) : selected.filter(value => value !== band.band))} />)}</div>
    {!!selected.length && <UnstyledButton type="button" className={styles.clear} onClick={() => onChange([])}>Clear band constraints</UnstyledButton>}
  </div>;
}
