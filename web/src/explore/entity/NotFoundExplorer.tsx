import { useEffect, useState } from 'react';
import { Link as RouterLink, useSearchParams } from 'react-router-dom';
import { changeWorld } from '../../world/context';
import { useRouteText } from '../../layout/useRouteText';
import { ErrorText, LoadingText, Muted, Panel } from '@ui';
import { exploreNotFound } from '../api';
import { EntityLink } from '../components/EntityLink';
import type { ExploreNotFoundResponse } from '../types';
import styles from './NotFoundExplorer.module.css';

/** Explicit identity/geometry calculation. Geometric neighbors and calculated
 * constituent addresses do not establish admission or semantic relationships. */
export function NotFoundExplorer() {
  const surface = useRouteText('/explore/notfound/:ref', 'ref');
  const [params] = useSearchParams();
  const [data, setData] = useState<ExploreNotFoundResponse | null>(null);
  const [err, setErr] = useState<string | null>(null);

  useEffect(() => {
    let stale = false;
    setData(null);
    setErr(null);
    exploreNotFound(surface)
      .then((result) => { if (!stale) setData(result); })
      .catch((e) => { if (!stale) setErr(e instanceof Error ? e.message : String(e)); });
    return () => { stale = true; };
  }, [surface]);

  if (err) return <ErrorText>{err}</ErrorText>;
  if (!data) return <LoadingText>Calculating the input and reading geometric neighbors…</LoadingText>;

  const geodesic = data.neighbors.filter((n) => n.axis === 'geodesic');
  const shape = data.neighbors.filter((n) => n.axis === 'shape');

  return (
    <div className={styles.wrap}>
      <header className={styles.head}>
        <div className={styles.title}>
          <span className={styles.term}>{surface}</span>
          <span className={styles.badge}>{data.exists ? 'known content · geometric calculation' : 'calculated identity · not reported present'}</span>
        </div>
        <Muted>
          This operation calculates the input’s identity and geometry. A calculated
          address does not establish admission, testimony, or a semantic relationship
          to nearby content.
        </Muted>
        <RouterLink to={`/explore?${changeWorld(params, { q: surface, selected: null, answer: null, offset: null })}`}>Search known content</RouterLink>
      </header>

      {data.suggestions.length > 0 ? (
        <Panel title="Similar spellings">
          <ul className={styles.list}>
            {data.suggestions.map((s) => (
              <li key={s.id_hex} className={styles.row}>
                <EntityLink idHex={s.id_hex} label={s.surface} />
                <span className={styles.dist}>
                  {s.distance} edit{s.distance === 1 ? '' : 's'}
                </span>
              </li>
            ))}
          </ul>
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

      <Panel title="Calculated decomposition">
        <div className={styles.decomp}>
          {data.decomposition
            .filter((d) => d.tier <= 2 && d.label.length > 0)
            .sort((a, b) => a.text_offset - b.text_offset || a.tier - b.tier)
            .map((d) => (
              <RouterLink
                key={`d-${d.ordinal}`}
                to={d.tier === 2
                  ? `/explore/resolve/${encodeURIComponent(d.label)}`
                  : `/explore/entity/${d.id_hex}`}
                className={styles.glyph}
                title={`tier ${d.tier}`}
              >
                {d.label === ' ' ? '␣' : d.label}
              </RouterLink>
            ))}
        </div>
      </Panel>
    </div>
  );
}
