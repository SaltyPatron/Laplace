import { useMemo, useState } from 'react';
import type { Strand, StrandState } from '../../data/contract';
import { relationOf } from '../../data/contract';
import { EntityChip } from '../EntityChip/EntityChip';
import styles from './Strand.module.css';
import { UnstyledButton } from '@ui';

const STATE_LABEL: Record<StrandState, string> = {
  confirmed: 'Confirmed', contested: 'Contested', refuted: 'Refuted', thin: 'Thin evidence', prior: 'Unconfirmed', structural: '',
};
const KIND_LABEL: Record<string, string> = {
  contains: 'Made of', 'contained-in': 'Appears in', precedes: 'Comes before', follows: 'Comes after',
  'co-occurs': 'Occurs with', 'shape-similar': 'Shaped like',
};
/** IsA → "is a", AtLocation → "at location", HAS_PART → "has part". */
export const relationWords = (relation: string) => relation.replace(/([a-z])([A-Z])/g, '$1 $2').replaceAll('_', ' ').toLowerCase();
export const kindLabel = (kind: string) => KIND_LABEL[kind] ?? relationWords(relationOf(kind as Strand['kind']) ?? kind);

/** Consensus as a bar: 1500 is neutral; the bar shows how far the conservative standing sits from it. */
export function StandingMeter({ strength, state }: { strength: number | null; state: StrandState }) {
  if (strength == null || state === 'structural') return null;
  const ratio = Math.max(0, Math.min(1, (strength - 800) / (2200 - 800)));
  return <span className={styles.meter} data-state={state} title={`Conservative standing ${strength.toFixed(0)} (neutral 1500)`}>
    <span className={styles.meterFill} style={{ inlineSize: `${(ratio * 100).toFixed(1)}%` }} />
  </span>;
}

function StrandRow({ strand, side }: { strand: Strand; side: 'to' | 'from' }) {
  const other = side === 'to' ? strand.to : strand.from;
  const [open, setOpen] = useState(false);
  const games = strand.spiders.reduce((sum, s) => sum + s.count, 0);
  return <li className={styles.row}>
    <div className={styles.main}>
      <EntityChip id={other.id} label={other.label} />
      {strand.run && strand.run > 1 && <span className={styles.muted}>× {strand.run}</span>}
      {strand.via && <span className={styles.muted}>in <EntityChip id={strand.via.id} label={strand.via.label} /></span>}
    </div>
    <div className={styles.side}>
      {STATE_LABEL[strand.state] && <span className={styles.state} data-state={strand.state}>{STATE_LABEL[strand.state]}</span>}
      <StandingMeter strength={strand.strength} state={strand.state} />
      {strand.spiders.length > 0 && <UnstyledButton type="button" className={styles.witnessButton} aria-expanded={open} onClick={() => setOpen((v) => !v)}>
        {strand.spiders.length} {strand.spiders.length === 1 ? 'source' : 'sources'}{games ? ` · ${games} ${games === 1 ? 'game' : 'games'}` : ''}
      </UnstyledButton>}
    </div>
    {open && <ul className={styles.spiders}>{strand.spiders.map((s) => <li key={s.witness.id}>
      <EntityChip id={s.witness.id} label={s.witness.label} />
      <span className={styles.muted}>trust {s.trust} · {s.count} {s.count === 1 ? 'game' : 'games'}{s.score != null ? ` · score ${s.score.toFixed(2)}` : ''}</span>
    </li>)}</ul>}
  </li>;
}

/** Strands grouped by kind and direction ("dog is a …" vs "… is a dog"), strongest first. One component for every facet. */
export function StrandList({ subjectId, subjectLabel, strands, groupByKind = true, empty }: {
  subjectId: string; subjectLabel?: string; strands: Strand[]; groupByKind?: boolean; empty: string;
}) {
  const subject = subjectId.toLowerCase();
  const groups = useMemo(() => {
    const map = new Map<string, { title: string; items: Strand[] }>();
    for (const strand of strands) {
      const outgoing = strand.from.id === subject;
      const key = groupByKind ? `${strand.kind}|${outgoing ? 'out' : 'in'}` : 'all';
      const words = kindLabel(strand.kind);
      const name = subjectLabel || 'this';
      const title = !groupByKind ? '' : strand.kind.startsWith('attested:') ? (outgoing ? `${name} ${words} …` : `… ${words} ${name}`) : words;
      const group = map.get(key) ?? { title, items: [] };
      group.items.push(strand);
      map.set(key, group);
    }
    return [...map.entries()];
  }, [strands, groupByKind, subject, subjectLabel]);
  if (!strands.length) return <p className={styles.empty}>{empty}</p>;
  return <div className={styles.groups}>{groups.map(([key, { title, items }]) => <section key={key} className={styles.group} data-relation-group={key}>
    {groupByKind && <h4 className={styles.groupTitle}>{title} <span className={styles.muted}>{items.length}</span></h4>}
    <ul className={styles.list}>{items.map((strand, index) => <StrandRow key={`${strand.from.id}-${strand.to.id}-${strand.kind}-${index}`} strand={strand}
      side={strand.from.id === subjectId.toLowerCase() ? 'to' : 'from'} />)}</ul>
  </section>)}</div>;
}
