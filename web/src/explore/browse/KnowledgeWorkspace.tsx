import { lazy, Suspense, useEffect, useMemo, useState } from 'react';
import { Link, useLocation, useSearchParams } from 'react-router-dom';
import { Button, Checkbox, ErrorText, Field, Input, LoadingText, LookupRow, Muted, Panel, ReadStatus, Stack, Table, Td, Th, useReadResource } from '@ui';
import { useAppStore } from '../../store';
import { queryHomeLeaders, queryShapes, relationBands, relationTypes, runQuery } from '../../query/api';
import { DIAL_DEFAULTS } from '../../query/types';
import { BandPicker } from '../../query/BandPicker';
import { DialPanel } from '../../query/DialPanel';
import { changeWorld, readWorld, selectWorldEntity } from '../../world/context';
import { RetainedContext } from '../../world/RetainedContext';
import { WorldProjection } from '../../world/WorldLink';
import { useEntityLabels } from '../../shell/inspector';
import { EntityLink } from '../components/EntityLink';
import { MasterDetail } from '../../ui/composites/MasterDetail/MasterDetail';
import { FacetCheckboxes } from '../../ui/composites/FacetCheckboxes/FacetCheckboxes';
import { browseSubstrate } from './api';
import type { BrowseResponse } from './types';
import { collectKnowledge, keyedAnswers, type KnowledgeAnswers, type KnowledgeResult } from './knowledge';
import styles from './BrowseHome.module.css';

const SubjectView = lazy(() => import('../../lenses/SubjectView').then(module => ({ default: module.SubjectView })));
/** Saved ?presentation= values keep opening the matching lens. */
const PRESENTATION_LENS: Record<string, string> = { knowledge: 'means' };

/** The selected result, seen through the same lenses as the entity page and the inspector. */
function SelectedSubject({ id, label, lens, onLensChange }: { id: string; label?: string; lens: string; onLensChange: (lens: string) => void }) {
  const known = useEntityLabels((s) => s.labels[id.toLowerCase()]);
  return <Suspense fallback={<LoadingText>Loading knowledge…</LoadingText>}>
    <header className={styles.subjectHeader}>
      <h2>{label ?? known ?? id}</h2>
      <Link to={`/explore/entity/${id}${lens !== 'means' ? `?lens=${lens}` : ''}`}>Open as a page</Link>
    </header>
    {/* Inside the collection, subject links select within the search instead of leaving it. */}
    <WorldProjection><SubjectView id={id} lens={lens} onLensChange={onLensChange} /></WorldProjection>
  </Suspense>;
}
const PAGE = 50, MAX_CAPACITY = 32768;
type Read = { method: string; status: 'loading' | 'ready' | 'error'; error?: string; result?: KnowledgeAnswers; browse?: BrowseResponse };
/** How a result was found, in words a reader uses. Internal shape ids never reach the page. */
const METHOD_LABELS: Record<string, string> = {
  entities: 'Content match', surface: 'Content match', define: 'Definition', fallback: 'Definition', what_is: 'Classification',
  describe: 'Description', synonyms: 'Synonym', translate: 'Translation', languages: 'Language', examples: 'Example',
  walk: 'Path', path: 'Path', complete: 'Completion', band_facts: 'Related fact', beam: 'Related fact',
  constituent: 'query constituent', contains_all: 'contains all query words', name: 'name / alias evidence',
};
const label = (method: string) => METHOD_LABELS[method] ?? method.replaceAll('_', ' ');
/** Shape similarity is not meaning: geometric neighbors belong to shape views, never to knowledge answers. */
const NOT_KNOWLEDGE = new Set(['generate', 'neighbors']);
/** Release-local packaging and format metadata that sources carry but that is not knowledge about the subject. */
const PACKAGING = /type of pointer that lemma has|^upos:\s|\bHAS[ _]SENSE\b|variety of language:|^I hold "/i;
/** A read that resolved no stored content returned nothing; that is an empty result, not a failure. */
const isNoMatch = (message: string) => /No content is witnessed|topic_not_witnessed/.test(message);
const round = (value: number | string | null | undefined, digits = 2) => {
  const n = typeof value === 'string' ? Number(value) : value;
  return n == null || !Number.isFinite(n) ? '—' : n.toFixed(digits);
};
const integer = (value: string | null, fallback: number) => value !== null && /^\d+$/.test(value) ? Number(value) : fallback;

/** One search collection. Retrieval methods contribute results; facets constrain them. */
export function KnowledgeWorkspace() {
  const [params, setParams] = useSearchParams();
  const location = useLocation();
  const world = readWorld(params);
  const controlsKey = JSON.stringify([world.dials, world.bands, world.issues]);
  const [dialsDraft, setDialsDraft] = useState(world.dials), [bandsDraft, setBandsDraft] = useState(world.bands);
  const { tenant, quoteId, querySeed, setQuerySeed } = useAppStore();
  const query = params.get('q') ?? '', topic2 = params.get('other') ?? '', relation = params.get('relation') ?? '', lang = params.get('lang') ?? '';
  const selected = params.get('selected'), selectedAnswer = params.get('answer');
  const offset = integer(params.get('offset'), 0), capacity = Math.min(MAX_CAPACITY, integer(params.get('capacity'), 2048));
  const [draft, setDraft] = useState(query), [otherDraft, setOtherDraft] = useState(topic2), [relationDraft, setRelationDraft] = useState(relation), [languageDraft, setLanguageDraft] = useState(lang);
  const methodKey = JSON.stringify(params.getAll('method')), hasMethods = params.has('method');
  const [methodsDraft, setMethodsDraft] = useState<string[] | null>(hasMethods ? params.getAll('method') : null);
  const [methodReads, setMethodReads] = useState<{ scope: string; reads: Read[] }>({ scope: '', reads: [] });
  const [attempt, setAttempt] = useState(0);
  const types = params.getAll('type'), tiers = params.getAll('tier'), matchedBy = params.getAll('matched');
  const shapes = useReadResource({ key: JSON.stringify(['knowledge-methods', tenant, quoteId]), read: signal => queryShapes({ tenant, quoteId, signal }) });
  const relations = useReadResource({ key: JSON.stringify(['knowledge-relations', tenant, quoteId]), read: signal => relationTypes({ tenant, quoteId, signal }) });
  const bands = useReadResource({ key: JSON.stringify(['knowledge-bands', tenant, quoteId]), read: signal => relationBands({ tenant, quoteId, signal }) });
  const leaders = useReadResource({ key: JSON.stringify(['knowledge-overview', tenant, quoteId]), enabled: !query, read: signal => queryHomeLeaders({ tenant, quoteId, signal }) });
  const searchMethods = shapes.data?.shapes.filter(shape => !NOT_KNOWLEDGE.has(shape.shape)) ?? [];
  const catalogKey = JSON.stringify(searchMethods);
  const scope = JSON.stringify([query, topic2, relation, lang, controlsKey, methodKey, hasMethods, tenant, quoteId, offset, capacity, attempt]);
  const entitiesEnabled = !!query && (!hasMethods || params.getAll('method').includes('entities'));
  const entities = useReadResource({
    key: JSON.stringify(['knowledge-entities', scope]), enabled: entitiesEnabled,
    read: signal => browseSubstrate({ query, offset, limit: PAGE, capacity }, { tenant, quoteId, signal }),
  });
  const reads: Read[] = [
    ...(entitiesEnabled ? [{ method: 'entities', status: entities.error ? 'error' as const : entities.data ? 'ready' as const : 'loading' as const,
      browse: entities.data, error: entities.error?.message }] : []),
    ...(methodReads.scope === scope ? methodReads.reads : []),
  ];
  useEffect(() => { setDraft(query); setOtherDraft(topic2); setRelationDraft(relation); setLanguageDraft(lang); }, [query, topic2, relation, lang]);
  useEffect(() => { setMethodsDraft(hasMethods ? JSON.parse(methodKey) : null); }, [methodKey, hasMethods]);
  useEffect(() => { const [dials, selectedBands] = JSON.parse(controlsKey); setDialsDraft(dials); setBandsDraft(selectedBands); }, [controlsKey]);
  useEffect(() => {
    if (!querySeed) return;
    const next = changeWorld(params, { q: querySeed.topic, other: querySeed.topic2 ?? null,
      relation: querySeed.relationType ?? null, band: (querySeed.bands ?? []).map(String),
      selected: null, answer: null, offset: null, games_offset: null, presentation: null });
    setParams(next); setQuerySeed(null);
  }, [querySeed, setQuerySeed, setParams]);
  useEffect(() => {
    if (!query) return;
    if (shapes.status === 'loading' || shapes.status === 'idle') return;
    const [dials, selectedBands, issues] = JSON.parse(controlsKey) as [typeof world.dials, number[], string[]];
    if (issues.length) { setMethodReads({ scope, reads: [] }); return; }
    const controller = new AbortController(), options = { tenant, quoteId, signal: controller.signal };
    const enabled: string[] | null = hasMethods ? JSON.parse(methodKey) : null;
    const available = JSON.parse(catalogKey) as typeof searchMethods;
    const tasks: { method: string; read: () => Promise<Pick<Read, 'result'>> }[] = [];
    for (const shape of available) {
      if (enabled !== null && !enabled.includes(shape.shape)) continue;
      if ((shape.needs_topic2 && !topic2) || (shape.needs_type && !relation)) continue;
      tasks.push({ method: shape.shape, read: async () => ({ result: await keyedAnswers(await runQuery({ ...dials, seed: dials.seed ? Number(dials.seed) : undefined, topic: query, shape: shape.shape,
        topic2: shape.needs_topic2 ? topic2 : undefined, relation_type: shape.needs_type ? relation : undefined,
        lang: shape.accepts_lang && lang ? lang : undefined,
        bands: ['band_facts', 'beam'].includes(shape.shape) && selectedBands.length ? selectedBands : undefined }, options)) }) });
    }
    setMethodReads({ scope, reads: tasks.map(task => ({ method: task.method, status: 'loading' })) });
    // Bound concurrent database reads. Failures do not conceal successful
    // results; navigation cancels this collection's transports.
    let cursor = 0;
    async function worker() {
      while (cursor < tasks.length && !controller.signal.aborted) {
        const task = tasks[cursor++];
        try {
          const value = await task.read();
          if (!controller.signal.aborted) setMethodReads(current => current.scope === scope ? { scope, reads: current.reads.map(read => read.method === task.method ? { ...read, ...value, status: 'ready' } : read) } : current);
        } catch (error) {
          const message = error instanceof Error ? error.message : String(error);
          const noMatch = isNoMatch(message);
          if (!controller.signal.aborted) setMethodReads(current => current.scope === scope ? { scope, reads: current.reads.map(read => read.method === task.method
            ? noMatch ? { ...read, status: 'ready' } : { ...read, status: 'error', error: message } : read) } : current);
        }
      }
    }
    void Promise.all(Array.from({ length: Math.min(4, tasks.length) }, worker));
    return () => controller.abort();
  }, [scope, query, topic2, relation, lang, controlsKey, methodKey, hasMethods, catalogKey, shapes.status, tenant, quoteId]);

  const browse = reads.find(read => read.browse)?.browse;
  const all = useMemo(() => {
    if (query) return collectKnowledge(browse?.hits ?? [], reads.flatMap(read => read.result ? [read.result] : []))
      .filter(row => !PACKAGING.test(row.text));
    const result = new Map<string, KnowledgeResult>();
    for (const band of leaders.data?.bands ?? []) {
      for (const claim of band.claims ?? []) {
        const key = 'entity:' + claim.claim_id;
        result.set(key, { key, entityId: claim.claim_id, text: claim.parts.map(part => `${part.label || part.id}${part.run > 1 ? ` × ${part.run}` : ''}`).join(' · '), kind: 'assertion', methods: [band.name], observations: [{ method: band.name, witnesses: claim.witnesses }], claim });
      }
      for (const edge of band.rows) {
        const key = JSON.stringify([edge.subject_id, edge.relation, edge.object_id]);
        result.set(key, { key, text: `${edge.subject} ${label(edge.relation)} ${edge.object}`, topicId: edge.subject_id,
          kind: 'relation', methods: [band.name], observations: [{ method: band.name, standing: edge.eff_mu, witnesses: edge.witnesses }] });
      }
    }
    return [...result.values()];
  }, [query, browse, reads, leaders.data]);
  const rows = all.filter(row => (!types.length || types.includes(row.kind)) && (!tiers.length || tiers.includes(String(row.tier ?? ''))) && (!matchedBy.length || row.methods.some(method => matchedBy.includes(label(method)))));
  const answer = all.find(row => row.key === selectedAnswer);
  const loading = reads.some(read => read.status === 'loading'), errors = reads.filter(read => read.status === 'error');
  function apply(patch: Record<string, string | string[] | null>) {
    setParams(changeWorld(params, patch), { preventScrollReset: true });
  }
  function submit() {
    if (!draft.trim()) return;
    const next = changeWorld(params, { q: draft, other: otherDraft || null, relation: relationDraft || null, lang: languageDraft || null,
      selected: null, answer: null, offset: null, capacity: null, detail: null, presentation: null,
      method: methodsDraft === null ? null : methodsDraft.length ? methodsDraft : ['none'], band: bandsDraft.map(String) });
    for (const key of Object.keys(DIAL_DEFAULTS) as (keyof typeof DIAL_DEFAULTS)[]) {
      next.delete(key);
      if (dialsDraft[key] !== DIAL_DEFAULTS[key]) next.set(key, String(dialsDraft[key]));
    }
    setParams(next); setAttempt(value => value + 1);
  }
  function destination(row: KnowledgeResult) {
    if (row.entityId) return selectWorldEntity(row.entityId, location);
    const next = new URLSearchParams(params); next.delete('selected'); next.delete('answer');
    next.set(row.entityId ? 'selected' : 'answer', row.entityId ?? row.key);
    return '?' + next;
  }
  return <Stack gap={4} className={selected || selectedAnswer ? styles.hasSelection : undefined}>
    <header className={styles.hero}>
      <h2>Knowledge</h2><Muted>Search what Laplace knows. Inspect meanings, assertions and relations beside the results.</Muted>
      <LookupRow value={draft} onChange={setDraft} onSubmit={submit} placeholder="A subject, content or canonical ID…" ariaLabel="Search knowledge" submitLabel="Search" submitDisabled={!draft.trim()}>
        {query && <Button variant="ghost" onClick={() => apply({ q: null, selected: null, answer: null, offset: null })}>Clear</Button>}
      </LookupRow>
      <details className={styles.criteria}><summary>Search criteria</summary>
        <div className={styles.criteriaFields}>
          <Field label="Second subject" htmlFor="search-other" help="Adds comparisons and paths to the same search."><Input id="search-other" value={otherDraft} onChange={event => setOtherDraft(event.target.value)} /></Field>
          <Field label="Relation" htmlFor="search-relation" help="Adds incoming and outgoing relation searches."><Input id="search-relation" list="search-relations" value={relationDraft} onChange={event => setRelationDraft(event.target.value)} /><datalist id="search-relations">{relations.data?.relations.map(item => <option key={item.id_hex} value={item.name} />)}</datalist></Field>
          <Field label="Language" htmlFor="search-language"><Input id="search-language" value={languageDraft} onChange={event => setLanguageDraft(event.target.value)} placeholder="Any" /></Field>
        </div>
        <fieldset className={styles.methodChoices}><legend>Search methods — all applicable methods run together</legend>
          {[{ shape: 'entities', summary: 'Names, aliases, exact content and containment', needs_topic2: false, needs_type: false }, ...searchMethods].map(method => {
            const missing = method.needs_topic2 && !otherDraft.trim() ? 'requires a second subject' : method.needs_type && !relationDraft ? 'requires a relation' : '';
            return <Checkbox key={method.shape} label={<span>{label(method.shape)} <small>— {method.summary}{missing ? ` (${missing})` : ''}</small></span>} checked={methodsDraft === null || methodsDraft.includes(method.shape)} onChange={event => {
              const current = methodsDraft ?? ['entities', ...searchMethods.map(item => item.shape)];
              setMethodsDraft(event.target.checked ? [...current, method.shape] : current.filter(item => item !== method.shape));
            }} />;
          })}
        </fieldset><ReadStatus label="Search method catalogue" resource={shapes} /><ReadStatus label="Relation catalogue" resource={relations} />
        <fieldset className={styles.methodChoices}><legend>Relation bands</legend><ReadStatus label="Relation bands" resource={bands} />{bands.data && <BandPicker bands={bands.data.bands} selected={bandsDraft} onChange={setBandsDraft} />}</fieldset>
        <fieldset className={styles.methodChoices}><legend>Execution bounds</legend><DialPanel shapes={searchMethods.filter(method => methodsDraft === null || methodsDraft.includes(method.shape)).map(method => method.shape)} idPrefix="search-dial" dials={dialsDraft} onChange={setDialsDraft} /></fieldset>
        <Button onClick={submit} disabled={!draft.trim()}>Apply search criteria</Button>
      </details>
    </header>
    {!query && <><p>Recorded assertions and relations, sampled from the current consensus. Search to investigate a subject.</p><ReadStatus label="Recorded knowledge" resource={leaders} /></>}
    {query && loading && <div role="status" className={styles.toolbar}><Muted>Searching…</Muted></div>}
    {shapes.error && query && <ErrorText>Part of search is unavailable right now; content matches are still shown.</ErrorText>}
    {world.issues.map(issue => <ErrorText key={issue}>{issue}</ErrorText>)}
    <RetainedContext world={world} />
    {errors.length > 0 && <details className={styles.readErrors}><summary>Some results could not be read; everything that was read is shown below</summary>{errors.map(read => <ErrorText key={read.method}>{label(read.method)}: {read.error}</ErrorText>)}</details>}
    <div className={styles.facets} role="group" aria-label="Search result filters">
      <FacetCheckboxes label="Type" values={all.map(row => row.kind)} selected={types} onChange={values => apply({ type: values })} />
      <FacetCheckboxes label="Tier" values={all.map(row => row.tier === undefined ? null : String(row.tier))} selected={tiers} onChange={values => apply({ tier: values })} />
      <FacetCheckboxes label="Found as" values={all.flatMap(row => [...new Set(row.methods.map(label))])} selected={matchedBy} onChange={values => apply({ matched: values })} />
      {(types.length + tiers.length + matchedBy.length > 0) && <Button variant="ghost" onClick={() => apply({ type: null, tier: null, matched: null })}>Clear result filters</Button>}
    </div>
    <MasterDetail label="Knowledge results and detail" selected={!!selected || !!selectedAnswer} onBack={() => apply({ selected: null, answer: null })} master={<Panel title={`${rows.length} results${query ? ` for “${query}”` : ''}`}>
      {!rows.length ? <Muted>{loading ? 'Reading knowledge…' : query ? `Nothing recorded matches “${query}”. Try a single word or a name.` : 'Nothing to show yet.'}</Muted> : <Table className={styles.resultsTable}><thead><tr><Th>Knowledge</Th><Th>Type</Th><Th>Found as</Th></tr></thead><tbody>{rows.map(row => <tr key={row.key} data-claim-id={row.claim?.claim_id} className={row.entityId === selected || row.key === selectedAnswer ? styles.selected : undefined}><Td><Link className={styles.entityLink} to={destination(row)} preventScrollReset aria-current={row.entityId === selected || row.key === selectedAnswer ? 'true' : undefined}>{row.text}</Link>{row.claim && <><Muted>{row.claim.witnesses} {row.claim.witnesses === 1 ? 'witness' : 'witnesses'} · confidence {round(row.claim.confidence)}</Muted><details><summary>Ordered assertion members</summary><ol>{row.claim.parts.map((part, ordinal) => <li key={ordinal}><EntityLink idHex={part.id} label={part.label || part.id} />{part.run > 1 ? ` × ${part.run}` : ''}</li>)}</ol><p>Recorded trajectory</p><pre className={styles.claimTrajectory}><code>{row.claim.trajectory}</code></pre></details></>}</Td><Td data-label="Type">{row.kind}</Td><Td data-label="Found as">{[...new Set(row.methods.map(label))].join(', ')}</Td></tr>)}</tbody></Table>}
      {browse && <div className={styles.pager}><Button variant="ghost" disabled={!offset} onClick={() => apply({ offset: String(Math.max(0, offset - PAGE)) })}>‹ Previous entities</Button><Muted>{browse.receipt.matched_entities} entities reached</Muted><Button variant="ghost" disabled={offset + browse.hits.length >= browse.receipt.matched_entities} onClick={() => apply({ offset: String(offset + PAGE) })}>Next entities ›</Button>{browse.receipt.candidate_truncated && capacity < MAX_CAPACITY && <Button onClick={() => apply({ capacity: String(Math.min(MAX_CAPACITY, capacity * 2)), offset: null })}>Expand entity frontier</Button>}</div>}
    </Panel>}>
      {(selected || selectedAnswer) && !rows.some(row => row.entityId === selected || row.key === selectedAnswer) && all.length > 0 && <p role="status">{all.some(row => row.entityId === selected || row.key === selectedAnswer) ? 'The selected result is outside the current filters.' : 'The selected subject is outside this returned collection. The originating search remains available.'}</p>}
      {answer ? <><h2>{answer.text}</h2><dl className={styles.receipt}>{answer.observations.map((observation, index) => <div key={index}><dt>{label(observation.method)}</dt><dd>{observation.standing == null ? 'Standing not returned' : `Conservative standing ${round(observation.standing, 1)}`} · {observation.witnesses == null ? 'Witness count not returned' : `${observation.witnesses} witnesses`}</dd></div>)}</dl>{answer.topicId && <Button onClick={() => apply({ selected: answer.topicId!, answer: null })}>Inspect related subject</Button>}</> : selected && /^[0-9a-f]{32}$/i.test(selected) ? <SelectedSubject key={selected} id={selected} label={all.find(row => row.entityId === selected)?.text}
            lens={PRESENTATION_LENS[params.get('presentation') ?? ''] ?? params.get('presentation') ?? 'means'}
            onLensChange={(lens) => apply({ presentation: lens === 'means' ? null : lens })} /> : selectedAnswer ? <p role="status">{loading ? 'Reading the selected answer…' : 'The selected answer was not returned in this read. Its reference has been retained.'}</p> : <><h2>Select a result</h2><p>Read its content and relations here. Your search and filters remain in place.</p></>}
    </MasterDetail>
    {query && <details className={styles.receiptDetails}><summary>Search read scope</summary><p>Methods combine by union. Result checkboxes use OR within a field and AND between fields; they filter the returned collection. Entity paging and frontier limits apply to the entity method. Other methods return their own bounded reads. Equal reply text is combined without merging method measurements.</p>{reads.map(read => <p key={read.method}>{label(read.method)}: {read.status}{read.result ? ` · ${read.result.rows.length} rows` : ''}</p>)}{browse && <pre>{JSON.stringify(browse.receipt, null, 2)}</pre>}</details>}
  </Stack>;
}
