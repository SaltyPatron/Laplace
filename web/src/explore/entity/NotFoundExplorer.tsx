import { useEffect, useMemo, useState } from 'react';
import { Link as RouterLink, useParams } from 'react-router-dom';
import { ErrorText, LoadingText, Muted, Panel } from '@ui';
import { exploreNotFound } from '../api';
import { EntityLink } from '../components/EntityLink';
import type { ExploreNotFoundResponse } from '../types';
import styles from './NotFoundExplorer.module.css';

// The explorer for a reference that resolves to a valid content id but was never
// witnessed (exists=false). There is no entity page — but the word's geometry is
// fully determined (HashComposer pins it on S³), so we navigate by that computed
// anchor: nearest witnessed content by position (geodesic) and by shape
// (Frechet), plus the deterministic decomposition, every piece a live link.
export function NotFoundExplorer() {
  const { ref = '' } = useParams();
  const surface = useMemo(() => decodeURIComponent(ref), [ref]);
  const [data, setData] = useState<ExploreNotFoundResponse | null>(null);
  const [err, setErr] = useState<string | null>(null);

  useEffect(() => {
    setData(null);
    setErr(null);
    exploreNotFound(surface)
      .then(setData)
      .catch((e) => setErr(e instanceof Error ? e.message : String(e)));
  }, [surface]);

  if (err) return <ErrorText>{err}</ErrorText>;
  if (!data) return <LoadingText>Searching the substrate near “{surface}”…</LoadingText>;

  const geodesic = data.neighbors.filter((n) => n.axis === 'geodesic');
  const shape = data.neighbors.filter((n) => n.axis === 'shape');

  return (
    <div className={styles.wrap}>
      <header className={styles.head}>
        <div className={styles.title}>
          <span className={styles.term}>{surface}</span>
          <span className={styles.badge}>not witnessed</span>
        </div>
        <Muted>
          No source has asserted “{surface}” yet — but its position on the glome is
          determined. Here is what the substrate holds nearest to it.
        </Muted>
        <code className={styles.idhex}>{data.word_id_hex}</code>
      </header>

      {data.did_you_mean ? (
        <Panel title="Did you mean">
          <RouterLink
            className={styles.suggest}
            to={`/explore/resolve/${encodeURIComponent(data.did_you_mean)}`}
          >
            {data.did_you_mean}
          </RouterLink>
        </Panel>
      ) : null}

      <Panel title="Closest by shape — Fréchet over the letter curve">
        {shape.length === 0 ? (
          <Muted>No witnessed word traces a similar curve within range.</Muted>
        ) : (
          <ul className={styles.list}>
            {shape.map((n) => (
              <li key={`s-${n.id_hex}`} className={styles.row}>
                <EntityLink idHex={n.id_hex} label={n.label} />
                <span className={styles.dist}>
                  {n.frechet != null ? n.frechet.toFixed(4) : ''}
                </span>
              </li>
            ))}
          </ul>
        )}
      </Panel>

      <Panel title="Closest by position — geodesic on S³ (any tier)">
        {geodesic.length === 0 ? (
          <Muted>Nothing witnessed sits near this point.</Muted>
        ) : (
          <ul className={styles.list}>
            {geodesic.map((n) => (
              <li key={`g-${n.id_hex}`} className={styles.row}>
                <EntityLink idHex={n.id_hex} label={n.label} />
                <span className={styles.meta}>
                  {n.tier != null ? <span className={styles.tier}>t{n.tier}</span> : null}
                  <span className={styles.dist}>
                    {n.geodesic != null ? n.geodesic.toFixed(4) : ''}
                  </span>
                </span>
              </li>
            ))}
          </ul>
        )}
      </Panel>

      <Panel title="Decomposition">
        <div className={styles.decomp}>
          {data.decomposition
            .filter((d) => d.tier <= 1 && d.label.length > 0)
            .sort((a, b) => a.text_offset - b.text_offset || a.tier - b.tier)
            .map((d) => (
              <RouterLink
                key={`d-${d.ordinal}`}
                to={`/explore/entity/${d.id_hex}`}
                className={styles.glyph}
                title={`tier ${d.tier} · ${d.id_hex}`}
              >
                {d.label === ' ' ? '␣' : d.label}
              </RouterLink>
            ))}
        </div>
      </Panel>
    </div>
  );
}
