import { useEffect, useState } from 'react';
import { Link, Navigate, useParams } from 'react-router-dom';
import { ErrorText, LoadingText, Muted, Panel } from '@ui';
import { queryLeaders, relationBands } from '../../query/api';
import type { BandLeaders, RelationBand } from '../../query/types';
import { SearchBar } from '../components/SearchBar';
import { findLayer } from './layers';
import styles from './Highway.module.css';

/**
 * One division of the highway.
 *
 * Standings are real rated edges from /v1/query/leaders for the band that
 * carries this layer, each side clickable through to the entity it names — the
 * roster, not a picture of one. A layer with no API read renders its gap
 * explicitly instead of an empty table.
 */
export function LayerPage() {
  const { slug } = useParams();
  const layer = findLayer(slug);

  const [leaders, setLeaders] = useState<BandLeaders[] | null>(null);
  const [band, setBand] = useState<RelationBand | null>(null);
  const [error, setError] = useState<string | null>(null);

  const bandNo = layer?.band;

  useEffect(() => {
    if (bandNo == null) return;
    let stale = false;
    setLeaders(null);
    setError(null);
    Promise.all([queryLeaders([bandNo], 15), relationBands()])
      .then(([l, b]) => {
        if (stale) return;
        setLeaders(l.bands ?? []);
        setBand(b.bands?.find((x) => x.band === bandNo) ?? null);
      })
      .catch((e) => { if (!stale) setError(e instanceof Error ? e.message : String(e)); });
    return () => { stale = true; };
  }, [bandNo]);

  if (!layer) return <Navigate to="/explore/highway" replace />;

  const rows = leaders?.[0]?.rows ?? [];

  return (
    <div className={styles.page}>
      <header className={styles.hero}>
        <Link className={styles.back} to="/explore/highway">← Highway</Link>
        <h2 className={styles.title}>
          {layer.name} <span className={styles.tag}>{layer.tag}</span>
        </h2>
        <p className={styles.lede}>{layer.blurb}</p>
      </header>

      <div className={styles.grid}>
        <Panel title="The standardized read">
          <code className={styles.read}>{layer.read}</code>
          {layer.relations.length > 0 && (
            <>
              <div className={styles.subhead}>Relation types in this division</div>
              <ul className={styles.relList}>
                {layer.relations.map((r) => <li key={r}><code>{r}</code></li>)}
              </ul>
            </>
          )}
        </Panel>

        <Panel title="What it contributes">
          <p className={styles.contributes}>{layer.contributes}</p>
          {band && (
            <Muted className={styles.bandNote}>
              Band {band.band} · {band.name} · rank {band.rank} ·{' '}
              {band.consensus_rows.toLocaleString()} consensus edges across{' '}
              {band.relation_types} relation type{band.relation_types === 1 ? '' : 's'}.
            </Muted>
          )}
        </Panel>
      </div>

      {layer.readGap ? (
        <Panel title="Roster">
          <p className={styles.gap}>{layer.readGap}</p>
        </Panel>
      ) : (
        <Panel title="Standings — strongest witnessed edges in this division">
          {/* A layer whose relations are spread across bands has no single
              leaders read; say that rather than spinning on a fetch that the
              effect never starts. */}
          {bandNo == null ? (
            <Muted>
              This division&rsquo;s relations span several salience bands, so there is no single
              band to rank. Enter it at a concrete entity below and read its edges there.
            </Muted>
          ) : error ? <ErrorText>{error}</ErrorText>
            : leaders == null ? <LoadingText>Reading the band…</LoadingText>
            : rows.length === 0 ? (
              <Muted>
                No consensus edges witnessed in this band yet. The division exists; nothing has
                been folded into it.
              </Muted>
            ) : (
              <table className={styles.standings}>
                <thead>
                  <tr>
                    <th scope="col" className={styles.num}>#</th>
                    <th scope="col">Subject</th>
                    <th scope="col">Relation</th>
                    <th scope="col">Object</th>
                    <th scope="col" className={styles.num}>μ</th>
                    <th scope="col" className={styles.num}>Wit</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((r, i) => (
                    <tr key={`${r.subject_id}-${i}`}>
                      <td className={styles.num}>{i + 1}</td>
                      <td>
                        <Link className={styles.entLink} to={`/explore/entity/${r.subject_id}`}>{r.subject}</Link>
                      </td>
                      <td><span className={styles.rel}>{r.relation}</span></td>
                      <td>
                        <Link className={styles.entLink} to={`/explore/entity/${r.object_id}`}>{r.object}</Link>
                      </td>
                      <td className={styles.num}>{Number(r.eff_mu).toFixed(1)}</td>
                      <td className={styles.num}>{r.witnesses}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
        </Panel>
      )}

      <Panel title="Enter the division">
        <SearchBar
          placeholder="a word, sense, frame, or id hex…"
          label={`Open a witnessed entity in ${layer.name}`}
          hint="Unwitnessed terms open the nearest geometric neighborhood."
          shortcut={false}
        />
      </Panel>
    </div>
  );
}
