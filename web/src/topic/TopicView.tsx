import { useEffect, useState } from 'react';
import { Link as RouterLink, useNavigate, useParams } from 'react-router-dom';
import { Button, ConsensusBadge, ErrorText, Input, LoadingText, Muted, Panel } from '@ui';
import { entityRecord, entityTaxonomy, runQuery, type TaxonomyResponse } from '../query/api';
import { exploreMesh, exploreResolve } from '../explore/api';
import type { MeshResponse } from '../explore/mesh/types';
import type { EntityRecord, QueryResult } from '../query/types';
import { TaxonomyTree } from './TaxonomyTree';
import styles from './TopicView.module.css';

/**
 * The topic page: ask once, see EVERYTHING. No shape dropdown, no mode picking —
 * every read the substrate can serve about a topic loads as its own section, in
 * parallel: glosses, the IS_A tree, the strongest facts by band, translations
 * (the ILI hub meshing languages), the mesh position, and the verdict record.
 * The Query console remains the advanced surface for dials; this is the answer.
 */
export function TopicView() {
  const { ref = '' } = useParams();
  const nav = useNavigate();
  const [ask, setAsk] = useState(decodeURIComponent(ref));

  useEffect(() => setAsk(decodeURIComponent(ref)), [ref]);

  return (
    <div className={styles.page}>
      <form
        className={styles.ask}
        onSubmit={(e) => { e.preventDefault(); if (ask.trim()) nav(`/topic/${encodeURIComponent(ask.trim())}`); }}
      >
        <Input aria-label="topic" value={ask} placeholder="any word, concept, or id — see everything at once"
          onChange={(e) => setAsk(e.target.value)} />
        <Button type="submit" disabled={!ask.trim()}>Look up</Button>
      </form>
      {ref ? <TopicBody topicRef={decodeURIComponent(ref)} /> : (
        <Muted className={styles.hint}>One lookup, every read: definition, taxonomy tree, rated facts, translations, mesh position.</Muted>
      )}
    </div>
  );
}

function TopicBody({ topicRef }: { topicRef: string }) {
  const [idHex, setIdHex] = useState<string | null>(null);
  const [label, setLabel] = useState('');
  const [notFound, setNotFound] = useState(false);

  const [define, setDefine] = useState<QueryResult | null>(null);
  const [facts, setFacts] = useState<QueryResult | null>(null);
  const [translations, setTranslations] = useState<QueryResult | null>(null);
  const [taxonomy, setTaxonomy] = useState<TaxonomyResponse | null>(null);
  const [mesh, setMesh] = useState<MeshResponse | null>(null);
  const [record, setRecord] = useState<EntityRecord | null>(null);

  useEffect(() => {
    let stale = false;
    setIdHex(null); setNotFound(false);
    setDefine(null); setFacts(null); setTranslations(null);
    setTaxonomy(null); setMesh(null); setRecord(null);

    exploreResolve(topicRef).then((hit) => {
      if (stale) return;
      if (!hit) { setNotFound(true); return; }
      setIdHex(hit.id_hex); setLabel(hit.label);

      // Everything, in parallel — each section lands when it lands.
      runQuery({ topic: topicRef, shape: 'define' }).then((r) => !stale && setDefine(r)).catch(() => {});
      runQuery({ topic: topicRef, shape: 'band_facts', limit: 30 }).then((r) => !stale && setFacts(r)).catch(() => {});
      runQuery({ topic: topicRef, shape: 'translate' }).then((r) => !stale && setTranslations(r)).catch(() => {});
      entityTaxonomy(hit.id_hex).then((r) => !stale && setTaxonomy(r)).catch(() => {});
      exploreMesh(hit.id_hex).then((r) => !stale && setMesh(r)).catch(() => {});
      entityRecord(hit.id_hex).then((r) => !stale && setRecord(r)).catch(() => {});
    }).catch(() => { if (!stale) setNotFound(true); });

    return () => { stale = true; };
  }, [topicRef]);

  if (notFound) return <ErrorText>Nothing witnessed for “{topicRef}”.</ErrorText>;
  if (!idHex) return <LoadingText>Resolving…</LoadingText>;

  return (
    <>
      <header className={styles.head}>
        <h2 className={styles.title}>{label}</h2>
        {record && (
          <span className={styles.record}>
            {record.confirmed} confirmed · {record.contested} contested · {record.refuted} refuted · {record.thin} thin
          </span>
        )}
        <nav className={styles.jumps}>
          <RouterLink to={`/explore/entity/${idHex}`}>entity</RouterLink>
          <RouterLink to={`/explore/mesh/${idHex}`}>mesh</RouterLink>
          <RouterLink to={`/explore/matchup?x=${encodeURIComponent(label)}`}>head-to-head</RouterLink>
          <RouterLink to="/query">dials</RouterLink>
        </nav>
      </header>

      <div className={styles.grid}>
        <Panel title="Definition">
          {define === null ? <LoadingText /> : define.rows.length === 0 ? <Muted>No witnessed glosses.</Muted> : (
            <ul className={styles.rows}>
              {define.rows.map((r, i) => (
                <li key={i} className={styles.row}>
                  <span className={styles.rowText}>{r.reply}</span>
                  <ConsensusBadge mu={r.eff_mu ?? undefined} witnesses={r.witnesses ?? undefined} />
                </li>
              ))}
            </ul>
          )}
        </Panel>

        <Panel title="Taxonomy — the IS_A tree">
          {taxonomy === null ? <LoadingText /> : <TaxonomyTree tax={taxonomy} />}
        </Panel>

        <Panel title="Translations — the ILI hub across languages">
          {translations === null ? <LoadingText /> : translations.rows.length === 0
            ? <Muted>No cross-lingual surfaces witnessed yet.</Muted>
            : (
              <div className={styles.chips}>
                {translations.rows.slice(0, 40).map((r, i) => (
                  <span key={i} className={styles.chip}>{r.reply}</span>
                ))}
              </div>
            )}
        </Panel>

        <Panel title="Strongest facts, by band">
          {facts === null ? <LoadingText /> : facts.rows.length === 0 ? <Muted>No rated facts yet.</Muted> : (
            <ul className={styles.rows}>
              {facts.rows.map((r, i) => (
                <li key={i} className={styles.row}>
                  <span className={styles.rowText}>{r.reply}</span>
                  <ConsensusBadge mu={r.eff_mu ?? undefined} witnesses={r.witnesses ?? undefined} />
                </li>
              ))}
            </ul>
          )}
        </Panel>

        <Panel title="Mesh position — the hubs it plays for">
          {mesh === null ? <LoadingText /> : mesh.belongs_to.length === 0 ? <Muted>A root — nothing above it.</Muted> : (
            <ul className={styles.rows}>
              {mesh.belongs_to.slice(0, 10).map((l, i) => (
                <li key={i} className={styles.row}>
                  <RouterLink className={styles.meshLink} to={`/explore/mesh/${l.id}`}>
                    <span className={styles.meshRel}>{l.relation.replace(/_/g, ' ').toLowerCase()}</span> {l.label}
                    {l.hub_type && <span className={styles.hubTag}>{l.hub_type.replace(/_/g, ' ')}</span>}
                  </RouterLink>
                </li>
              ))}
            </ul>
          )}
        </Panel>
      </div>
    </>
  );
}
