import { useEffect, useState } from 'react';
import { Link as RouterLink, useNavigate, useParams } from 'react-router-dom';
import { ErrorText, LoadingText, Muted } from '@ui';
import { exploreMesh, explorePreview } from '../api';
import { useExploreStore } from '../store';
import { PlayerCard } from '../entity/PlayerCard';
import { MeshLanding } from './MeshLanding';
import type { ExploreEntityPreviewResponse } from '../types';
import type { MeshLink, MeshResponse } from './types';
import styles from './MeshView.module.css';

/**
 * The semantic-mesh drill-down — the tiered master/detail navigation over the
 * factorization of meaning. The current node sits in the middle; the hubs it
 * plays for climb the ladder on the left (belongs_to), its roster fans out on
 * the right. Every name re-centers the view, and the breadcrumb is the path you
 * drilled. This is the league→team→player structure, over a graph rather than a
 * fixed tree: a synset is a team whose roster is its members; a word is a player
 * whose "teams" are its senses, synsets, frames and classes.
 */
export function MeshView() {
  const { id } = useParams();
  if (!id) return <MeshLanding />;
  return <MeshDrill idHex={id} />;
}

function MeshDrill({ idHex }: { idHex: string }) {
  const nav = useNavigate();
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
        pushMeshCrumb({ id: m.id, label: m.label });
      })
      .catch((e) => { if (!stale) setError(e instanceof Error ? e.message : String(e)); });
    // The node's stat row (top rating, games, facts) comes from the preview —
    // a separate cheap read so the ladder itself never waits on it.
    explorePreview(idHex)
      .then((pv) => { if (!stale) setPreview(pv); })
      .catch(() => { /* card falls back to the record line alone */ });
    return () => { stale = true; };
  }, [idHex, pushMeshCrumb]);

  return (
    <div className={styles.page}>
      <nav className={styles.trail} aria-label="Mesh path">
        <RouterLink to="/explore/mesh" className={styles.crumb}>Mesh</RouterLink>
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
              title="Belongs to"
              hint="the hubs this node plays for — up the ladder"
              links={data.belongs_to}
              side="up"
              onPick={(l) => nav(`/explore/mesh/${l.id}`)}
              empty="a root — nothing above it in the mesh"
            />

            <div className={styles.node}>
              {data.hub_type && <span className={styles.hubType}>{data.hub_type.replace(/_/g, ' ')}</span>}
              <span className={styles.nodeLabel}>{data.label}</span>
              <PlayerCard preview={preview ?? {
                id_hex: data.id, label: data.label, tier: null, type: data.hub_type ?? null,
                exists: true, evidence_count: 0, preview_facts: [],
              }} />
              <RouterLink to={`/explore/entity/${data.id}`} className={styles.openEntity}>
                open full entity page →
              </RouterLink>
            </div>

            <Column
              title="Roster"
              hint="the members under this hub — down the ladder"
              links={data.roster}
              side="down"
              onPick={(l) => nav(`/explore/mesh/${l.id}`)}
              empty="a leaf — no members below it"
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
              <button type="button" className={styles.link} onClick={() => onPick(l)}>
                <span className={styles.arrow}>{side === 'up' ? '↑' : '↓'}</span>
                <span className={styles.linkBody}>
                  <span className={styles.rel}>{l.relation.replace(/_/g, ' ')}</span>
                  <span className={styles.linkLabel}>{l.label}</span>
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
