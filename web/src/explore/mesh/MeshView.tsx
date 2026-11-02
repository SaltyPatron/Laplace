import { useEffect, useState } from 'react';
import { useLocation, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { WorldLink as RouterLink } from '../../world/WorldLink';
import { changeWorld, selectWorldEntity } from '../../world/context';
import { Button, ErrorText, LoadingText, Muted } from '@ui';
import { exploreMesh, explorePreview } from '../api';
import { useExploreStore } from '../store';
import { PlayerCard } from '../entity/PlayerCard';
import { MeshLanding } from './MeshLanding';
import type { ExploreEntityPreviewResponse } from '../types';
import type { MeshLink, MeshResponse } from './types';
import styles from './MeshView.module.css';

/**
 * Master/detail drill over one entity's containment in the web: the entity in the center,
 * the hubs it belongs to (`belongs_to`) on the left, its members (`roster`) on the right.
 * Every name re-centers the view on that entity; the breadcrumb is the drilled path. The
 * relation is a graph, so one entity can belong to many hubs.
 */
export function MeshView({ selectedId, embedded = false }: { selectedId?: string; embedded?: boolean } = {}) {
  const { id: routeId } = useParams();
  const id = selectedId ?? routeId;
  if (!id) return <MeshLanding />;
  return <MeshDrill idHex={id} embedded={embedded} />;
}

function MeshDrill({ idHex, embedded }: { idHex: string; embedded: boolean }) {
  const nav = useNavigate();
  const location = useLocation();
  const [params, setParams] = useSearchParams();
  const recenter = (id: string) => nav(embedded ? selectWorldEntity(id, location, 'mesh') : `/explore/mesh/${id}`, { preventScrollReset: embedded });
  const { meshTrail, pushMeshCrumb } = useExploreStore();
  const [data, setData] = useState<MeshResponse | null>(null);
  const [preview, setPreview] = useState<ExploreEntityPreviewResponse | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let stale = false;
    setData(null);
    setPreview(null);
    setError(null);
    exploreMesh(idHex)
      .then((m) => {
        if (stale) return;
        setData(m);
        pushMeshCrumb({ id: m.id, label: m.label || m.id });
      })
      .catch((e) => { if (!stale) setError(e instanceof Error ? e.message : String(e)); });
    // The entity's stat card comes from a separate preview read so the ladder never waits on it.
    explorePreview(idHex)
      .then((pv) => { if (!stale) setPreview(pv); })
      .catch(() => { /* card falls back to the record line alone */ });
    return () => { stale = true; };
  }, [idHex, pushMeshCrumb]);

  return (
    <div className={`${styles.page} ${embedded ? styles.embedded : ''}`}>
      <nav className={styles.trail} aria-label="Mesh path">
        {!embedded && <RouterLink to="/explore/mesh" className={styles.crumb}>Mesh</RouterLink>}
        {meshTrail.map((c, i) => (
          <span key={c.id} className={styles.crumbWrap}>
            <span className={styles.sep}>›</span>
            {i === meshTrail.length - 1
              ? <span className={styles.crumbCurrent}>{c.label}</span>
              : <RouterLink to={`/explore/mesh/${c.id}`} className={styles.crumb}>{c.label}</RouterLink>}
          </span>
        ))}
      </nav>

      {error ? <ErrorText>{error}</ErrorText>
        : !data ? <LoadingText>Reading the mesh…</LoadingText>
        : (
          <div className={styles.ladder}>
            <Column
              title="Parent relations"
              hint="Relations returned for this entity"
              links={data.belongs_to}
              side="up"
              onPick={(l) => recenter(l.id)}
              empty="No parent relations were returned."
            />

            <div className={styles.node}>
              {data.hub_type && <span className={styles.hubType}>{data.hub_type.replace(/_/g, ' ')}</span>}
              <span className={styles.nodeLabel}>{data.label || data.id}</span>
              {preview ? <PlayerCard preview={preview} /> : <Muted>Standing preview has not been returned.</Muted>}
              <div className={styles.nodeLinks}>
                <RouterLink to={`/explore/entity/${data.id}`} className={styles.openEntity}>
                  full entity →
                </RouterLink>
                {embedded ? <Button variant="ghost" onClick={() => setParams(changeWorld(params, { presentation: 'comparison' }), { preventScrollReset: true })}>Compare →</Button> : <RouterLink
                  to={`/explore/matchup?x=${encodeURIComponent(data.id)}`}
                  className={styles.openEntity}
                >
                  head-to-head →
                </RouterLink>}
              </div>
            </div>

            <Column
              title="Members"
              hint="Members returned by the relation read"
              links={data.roster}
              side="down"
              onPick={(l) => recenter(l.id)}
              empty="No members were returned."
            />
          </div>
        )}
    </div>
  );
}

function Column({ title, hint, links, side, onPick, empty }: {
  title: string; hint: string; links: MeshLink[]; side: 'up' | 'down';
  onPick: (l: MeshLink) => void; empty: string;
}) {
  return (
    <div className={styles.col}>
      <div className={styles.colHead}>
        <span className={styles.colTitle}>{title}</span>
        <Muted className={styles.colHint}>{hint}</Muted>
      </div>
      {links.length === 0 ? (
        <Muted className={styles.colEmpty}>{empty}</Muted>
      ) : (
        <ul className={styles.links}>
          {links.map((l, i) => (
            <li key={`${l.id}-${i}`}>
              <button type="button" data-entity-ref={l.id} className={styles.link} onClick={() => onPick(l)}>
                <span className={styles.arrow}>{side === 'up' ? '↑' : '↓'}</span>
                <span className={styles.linkBody}>
                  <span className={styles.rel}>{l.relation.replace(/_/g, ' ')}</span>
                  <span className={styles.linkLabel}>{l.label || l.id}</span>
                </span>
                {l.hub_type && <span className={styles.linkHub}>{l.hub_type.replace(/_/g, ' ')}</span>}
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
