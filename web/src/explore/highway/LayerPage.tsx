import { useEffect, useState } from 'react';
import { Link, Navigate, useParams } from 'react-router-dom';
import { ErrorText, LoadingText, Muted, Panel } from '@ui';
import { queryLeaders, relationBands } from '../../query/api';
import type { BandLeaders, RelationBand } from '../../query/types';
import { SearchBar } from '../components/SearchBar';
import { findLayer } from './layers';
import { relationWords } from '../../domain/Strand/StrandList';
import styles from './Highway.module.css';

/**
 * One highway layer. Standings are the rated consensus cells from /v1/query/leaders for the
 * layer's band, each endpoint linking to its entity. A layer with `readGap` renders that gap
 * instead of a table.
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
        <Panel title="Relations in this division">
          {layer.relations.length > 0 ? (
            <ul className={styles.relList}>
              {layer.relations.map((r) => <li key={r}>{relationWords(r)}</li>)}
            </ul>
          ) : <Muted>This division is read through identifiers rather than named relations.</Muted>}
          <details className={styles.subhead}>
            <summary>For developers: the API read</summary>
            <code className={styles.read}>{layer.read}</code>
          </details>
        </Panel>

        <Panel title="What it contributes">
          <p className={styles.contributes}>{layer.contributes}</p>
          {band && (
            <Muted className={styles.bandNote}>
              {band.consensus_rows.toLocaleString()} rated {band.consensus_rows === 1 ? 'strand' : 'strands'} across{' '}
              {band.relation_types} {band.relation_types === 1 ? 'relation' : 'relations'}.
            </Muted>
          )}
        </Panel>
      </div>

      {layer.readGap ? (
        <Panel title="Roster">
          <p className={styles.gap}>{layer.readGap}</p>
        </Panel>
      ) : (
        <Panel title="Strongest strands in this division">
          {/* No band means no leaders read; the effect never fetches for it. */}
          {bandNo == null ? (
            <Muted>
              This division&rsquo;s relations span several salience bands, so there is no single
              band to rank. Enter it at a concrete entity below and read its edges there.
            </Muted>
          ) : error ? <ErrorText>{error}</ErrorText>
            : leaders == null ? <LoadingText>Reading the band…</LoadingText>
            : rows.length === 0 ? (
              <Muted>
                {band && band.consensus_rows > 0
                  ? `None of the ${band.consensus_rows.toLocaleString()} rated strands here has enough standing to rank yet: each needs more sources agreeing before it counts as established.`
                  : 'Nothing has been rated in this division yet.'}
              </Muted>
            ) : (
              <table className={styles.standings}>
                <thead>
                  <tr>
                    <th scope="col" className={styles.num}>#</th>
                    <th scope="col">Subject</th>
                    <th scope="col">Relation</th>
                    <th scope="col">Object</th>
                    <th
                      scope="col"
                      className={styles.num}
                      title="Cautious standing: rating minus twice its uncertainty"
                    >
                      Standing
                    </th>
                    <th scope="col" className={styles.num}>Witnesses</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((r, i) => (
                    <tr key={`${r.subject_id}-${i}`}>
                      <td className={styles.num}>{i + 1}</td>
                      <td>
                        <Link className={styles.entLink} to={`/explore/entity/${r.subject_id}`}>{r.subject}</Link>
                      </td>
                      <td><span className={styles.rel}>{relationWords(r.relation)}</span></td>
                      <td>
                        <Link className={styles.entLink} to={`/explore/entity/${r.object_id}`}>{r.object}</Link>
                      </td>
                      <td className={styles.num}>{Number(r.eff_mu).toFixed(0)}</td>
                      <td className={styles.num}>{r.witnesses.toLocaleString()}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
        </Panel>
      )}

      <Panel title="Enter the division">
        <SearchBar
          placeholder="a word, sense, frame, player, or id hex…"
          label={`Browse a witnessed entity in ${layer.name}`}
          hint="Discovery uses the same canonical Browse result set; select the entity you mean, then inspect this division from that identity."
          shortcut={false}
        />
      </Panel>
    </div>
  );
}
