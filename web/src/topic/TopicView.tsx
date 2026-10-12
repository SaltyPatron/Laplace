import { useEffect, useState } from 'react';
import { Link as RouterLink, useNavigate } from 'react-router-dom';
import { useRouteText } from '../layout/useRouteText';
import { Button, ConsensusBadge, ErrorText, Input, LoadingText, Muted, Panel } from '@ui';
import { describeFailure } from '../api/client';
import { entityRecord, entityTaxonomy, runQuery, type TaxonomyResponse } from '../query/api';
import { exploreMesh, exploreResolve } from '../explore/api';
import type { MeshResponse } from '../explore/mesh/types';
import type { EntityRecord, QueryResult } from '../query/types';
import { TaxonomyTree } from './TaxonomyTree';
import styles from './TopicView.module.css';

/**
 * Topic view: resolves the topic to its entity, then runs every read of that one
 * entity in parallel, each as its own section — definitions, band facts,
 * translations, the IS_A taxonomy, the mesh position and the entity record.
 */
export function TopicView() {
  const ref = useRouteText('/topic/:ref', 'ref');
  const nav = useNavigate();
  const [ask, setAsk] = useState(ref);

  useEffect(() => setAsk(ref), [ref]);

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
      {ref ? <TopicBody key={ref} topicRef={ref} /> : (
        <Muted className={styles.hint}>One lookup, every read: definition, taxonomy tree, rated facts, translations, mesh position.</Muted>
      )}
    </div>
  );
}

function TopicBody({ topicRef }: { topicRef: string }) {
  const [idHex, setIdHex] = useState<string | null>(null);
  const [label, setLabel] = useState('');
  const [notFound, setNotFound] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);

  const [define, setDefine] = useState<QueryResult | null>(null);
  const [facts, setFacts] = useState<QueryResult | null>(null);
  const [translations, setTranslations] = useState<QueryResult | null>(null);
  const [taxonomy, setTaxonomy] = useState<TaxonomyResponse | null>(null);
  const [mesh, setMesh] = useState<MeshResponse | null>(null);
  const [record, setRecord] = useState<EntityRecord | null>(null);
  const [errors, setErrors] = useState<Partial<Record<'define' | 'facts' | 'translations' | 'taxonomy' | 'mesh' | 'record', string>>>({});

  useEffect(() => {
    let stale = false;
    setIdHex(null); setNotFound(false); setFailure(null);
    setDefine(null); setFacts(null); setTranslations(null);
    setTaxonomy(null); setMesh(null); setRecord(null);
    setErrors({});
    const failedRead = (section: keyof typeof errors) => (error: unknown) => {
      if (!stale) setErrors((current) => ({ ...current, [section]: describeFailure(error).message }));
    };

    exploreResolve(topicRef).then((hit) => {
      if (stale) return;
      if (!hit || hit.exists === false) { setNotFound(true); return; }
      setIdHex(hit.id_hex); setLabel(hit.label);

      // Independent reads; each section renders when its read returns.
      runQuery({ topic: topicRef, shape: 'define' }).then((r) => !stale && setDefine(r)).catch(failedRead('define'));
      runQuery({ topic: topicRef, shape: 'band_facts', limit: 30 }).then((r) => !stale && setFacts(r)).catch(failedRead('facts'));
      runQuery({ topic: topicRef, shape: 'translate' }).then((r) => !stale && setTranslations(r)).catch(failedRead('translations'));
      entityTaxonomy(hit.id_hex).then((r) => !stale && setTaxonomy(r)).catch(failedRead('taxonomy'));
      exploreMesh(hit.id_hex).then((r) => !stale && setMesh(r)).catch(failedRead('mesh'));
      entityRecord(hit.id_hex).then((r) => !stale && setRecord(r)).catch(failedRead('record'));
    }).catch((error) => {
      if (stale) return;
      const failed = describeFailure(error);
      if (failed.kind === 'missing') setNotFound(true); else setFailure(failed.message);
    });

    return () => { stale = true; };
  }, [topicRef]);

  if (failure) return <ErrorText role="alert">{failure}</ErrorText>;
  if (notFound) return (
    <Muted>
      Nothing admitted is named “{topicRef}”. <RouterLink to={`/explore?q=${encodeURIComponent(topicRef)}`}>Browse</RouterLink> finds
      the witnessed words and structures it is made of.
    </Muted>
  );
  if (!idHex) return <LoadingText>Resolving…</LoadingText>;

  return (
    <>
      <header className={styles.head}>
        <h2 className={styles.title}>{label}</h2>
        {errors.record && <ErrorText role="alert">Record: {errors.record}</ErrorText>}
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
          {errors.define ? <ErrorText role="alert">{errors.define}</ErrorText> : define === null ? <LoadingText /> : define.rows.length === 0 ? <Muted>No witnessed glosses.</Muted> : (
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
          {errors.taxonomy ? <ErrorText role="alert">{errors.taxonomy}</ErrorText> : taxonomy === null ? <LoadingText /> : <TaxonomyTree tax={taxonomy} />}
        </Panel>

        <Panel title="Translations — the ILI hub across languages">
          {errors.translations ? <ErrorText role="alert">{errors.translations}</ErrorText> : translations === null ? <LoadingText /> : translations.rows.length === 0
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
          {errors.facts ? <ErrorText role="alert">{errors.facts}</ErrorText> : facts === null ? <LoadingText /> : facts.rows.length === 0 ? <Muted>No rated facts yet.</Muted> : (
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
          {errors.mesh ? <ErrorText role="alert">{errors.mesh}</ErrorText> : mesh === null ? <LoadingText /> : mesh.belongs_to.length === 0 ? <Muted>A root — nothing above it.</Muted> : (
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
