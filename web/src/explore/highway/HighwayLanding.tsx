import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { Muted } from '@ui';
import { relationBands } from '../../query/api';
import type { RelationBand } from '../../query/types';
import { HIGHWAY_LAYERS } from './layers';
import styles from './Highway.module.css';

/**
 * The highway, as a league table.
 *
 * The divisions are a fixed structural vocabulary — the layers the factorization
 * is built from — but unlike the mesh landing these are not blurb cards you can
 * only read: each is a real destination with a roster, standings and a named
 * read. Volume comes from /v1/query/bands so a division that has not been seeded
 * shows zero rather than looking populated.
 */
export function HighwayLanding() {
  const [bands, setBands] = useState<RelationBand[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    relationBands()
      .then((b) => setBands(b.bands ?? []))
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, []);

  const rowsFor = (band?: number) =>
    band == null ? null : bands?.find((b) => b.band === band)?.consensus_rows ?? null;

  return (
    <div className={styles.page}>
      <header className={styles.hero}>
        <h2 className={styles.title}>The language highway</h2>
        <p className={styles.lede}>
          One division per layer of the ladder. Each has a roster you can walk, standings ranked by
          witnessed consensus, and the exact read that queries it — so you can check that a layer
          carries signal rather than merely existing.
        </p>
      </header>

      {error && <Muted className={styles.err}>Band volumes unavailable — {error}</Muted>}

      <table className={styles.standings}>
        <thead>
          <tr>
            <th scope="col">Division</th>
            <th scope="col">Relations</th>
            <th scope="col" className={styles.num}>Consensus edges</th>
            <th scope="col">Status</th>
          </tr>
        </thead>
        <tbody>
          {HIGHWAY_LAYERS.map((l) => {
            const rows = rowsFor(l.band);
            return (
              <tr key={l.slug}>
                <th scope="row" className={styles.divCell}>
                  <Link className={styles.divLink} to={`/explore/highway/${l.slug}`}>{l.name}</Link>
                  <span className={styles.tag}>{l.tag}</span>
                  <span className={styles.blurb}>{l.blurb}</span>
                </th>
                <td className={styles.relCell}>
                  {l.relations.length ? `${l.relations.length} type${l.relations.length === 1 ? '' : 's'}` : '—'}
                </td>
                <td className={styles.num}>
                  {/* Three distinct states, not one ellipsis: no read at all,
                      relations spread across bands so no single volume applies,
                      and genuinely still loading. */}
                  {l.readGap ? '—'
                    : l.band == null ? <span className={styles.empty}>spans bands</span>
                    : bands == null ? '…'
                    : rows == null ? '—'
                    : rows.toLocaleString()}
                </td>
                <td>
                  {l.readGap ? (
                    <span className={styles.missing}>not readable yet</span>
                  ) : rows === 0 ? (
                    <span className={styles.empty}>no consensus yet</span>
                  ) : (
                    <span className={styles.live}>live</span>
                  )}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>

      <Muted className={styles.foot}>
        Volume is the salience band carrying each layer&rsquo;s edges, read live from
        <code> /v1/query/bands</code>. Divisions marked &ldquo;not readable yet&rdquo; have no API
        read — they say so rather than showing an empty roster.
      </Muted>
    </div>
  );
}
