import { useEffect, useState } from 'react';
import { Muted } from '@ui';
import { exploreModalities } from '../api';
import styles from './ModalityMap.module.css';

/**
 * The omni-modal map, told honestly. The substrate's law is one identity across
 * every modality; this shows which are actually resident and which await
 * ingestion — driven by /v1/explore/modalities, which counts each modality from
 * a fast targeted query, never the flaky all-sources aggregate. A modality with
 * nothing seeded reads "awaiting ingest", not an empty scoreboard pretending live.
 */
type Status = 'live' | 'sparse' | 'awaiting';

interface Counts { text: number; chess: number; models: number; multilingual: number }

const CARDS: { name: string; key: keyof Counts; blurb: string; ladder: string; href?: string }[] = [
  {
    name: 'Text', key: 'text', href: '/explore/mesh',
    blurb: 'WordNet, FrameNet, VerbNet, PropBank and the ILI hub — the semantic mesh.',
    ladder: 'surface → sense → concept → frame / class / roleset → roles',
  },
  {
    name: 'Chess', key: 'chess',
    blurb: "Magnus Carlsen's ~9k chess.com games plus the openings book, recorded as PGN testimony with the analysis layer folded over them — the proving domain, where ground truth is objectively checkable.",
    ladder: 'square → piece → move → position → game',
  },
  {
    name: 'Languages', key: 'multilingual',
    blurb: 'OMW multilingual lemmas meshing every language at the ILI hub — the largest source in the substrate — plus the full ISO-639 code catalog.',
    ladder: 'lemma → sense → ILI concept (cross-lingual)',
  },
  {
    name: 'AI models', key: 'models',
    blurb: 'A checkpoint is a witness like any other — its tensors assert token→token couplings. None ingested yet.',
    ladder: 'token → circuit (layer/head) → plane → model',
  },
];

function statusOf(n: number): Status {
  if (n === 0) return 'awaiting';
  return n < 5000 ? 'sparse' : 'live';
}

export function ModalityMap() {
  const [counts, setCounts] = useState<Counts | null>(null);

  useEffect(() => {
    exploreModalities().then(setCounts).catch(() => setCounts(null));
  }, []);

  return (
    <section className={styles.map} aria-label="Modalities">
      <div className={styles.head}>
        <span className={styles.title}>One law, every modality</span>
        <Muted className={styles.sub}>same identity, same rating math — resident where seeded, honest where not</Muted>
      </div>
      <div className={styles.grid}>
        {CARDS.map((m) => {
          const n = counts ? counts[m.key] : null;
          const status = n == null ? 'awaiting' : statusOf(n);
          const inner = (
            <>
              <div className={styles.cardHead}>
                <span className={styles.name}>{m.name}</span>
                <StatusPill status={status} />
              </div>
              <span className={styles.ladder}>{m.ladder}</span>
              <span className={styles.blurb}>{m.blurb}</span>
              <span className={styles.count}>
                {status === 'awaiting' ? 'not yet ingested'
                  : `${(n ?? 0).toLocaleString()} attestations`}
              </span>
            </>
          );
          return m.href && status !== 'awaiting' ? (
            <a key={m.name} className={`${styles.card} ${styles.live}`} href={m.href}>{inner}</a>
          ) : (
            <div key={m.name} className={`${styles.card} ${status === 'awaiting' ? styles.awaitingCard : ''}`}>{inner}</div>
          );
        })}
      </div>
    </section>
  );
}

function StatusPill({ status }: { status: Status }) {
  const label = status === 'live' ? 'live' : status === 'sparse' ? 'sparse' : 'awaiting ingest';
  return <span className={`${styles.pill} ${styles[status]}`}><span className={styles.dot} />{label}</span>;
}
