import { useEffect, useMemo, useRef, useState } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import { Button, ConsensusBadge, Field, Input, Muted, Panel, ReadStatus, Select, useReadResource } from '@ui';
import { useAppStore } from '../store';
import { BandPicker } from './BandPicker';
import { DialPanel } from './DialPanel';
import { queryShapes, relationBands, runQuery } from './api';
import { DIAL_DEFAULTS, type QueryDials } from './types';
import styles from './QueryConsole.module.css';

/** Catalog-driven reads. Catalogs, execution and result inspection fail independently. */
export function QueryConsole() {
  const { tenant, quoteId } = useAppStore();
  const [topic, setTopic] = useState('');
  const [topic2, setTopic2] = useState('');
  const [shape, setShape] = useState('describe');
  const [relationType, setRelationType] = useState('');
  const [lang, setLang] = useState('');
  const [selectedBands, setSelectedBands] = useState<number[]>([]);
  const [dials, setDials] = useState<QueryDials>(DIAL_DEFAULTS);
  const autoRun = useRef(false);
  const usesBands = shape === 'band_facts' || shape === 'beam';

  const shapesRead = useReadResource({
    key: JSON.stringify(['query-shapes', tenant, quoteId]),
    read: (signal) => queryShapes({ tenant, quoteId, signal }),
  });
  const bandsRead = useReadResource({
    key: JSON.stringify(['query-bands', tenant, quoteId]),
    read: (signal) => relationBands({ tenant, quoteId, signal }),
    enabled: usesBands,
  });
  const shapes = shapesRead.data?.shapes;
  const active = useMemo(() => shapes?.find((item) => item.shape === shape), [shapes, shape]);
  const needsTopic2 = active?.needs_topic2 || shape === 'path';
  const needsType = active?.needs_type ?? false;
  const acceptsLang = active?.accepts_lang ?? false;
  const canRun = !!active && topic.length > 0 && (!needsTopic2 || topic2.length > 0) &&
    (!needsType || relationType.length > 0);

  const resultRead = useReadResource({
    key: JSON.stringify(['query-result', tenant, quoteId]),
    enabled: false,
    read: (signal) => {
      if (!canRun) throw new Error('Choose an available shape and supply its required topics and relation.');
      const seed = dials.seed.trim() === '' ? undefined : Number(dials.seed);
      if (seed !== undefined && !Number.isSafeInteger(seed)) throw new Error('Seed must be an exactly representable integer.');
      return runQuery({
        topic, topic2: needsTopic2 ? topic2 : undefined, shape,
        bands: usesBands && selectedBands.length ? selectedBands : undefined,
        relation_type: needsType ? relationType : undefined,
        lang: acceptsLang && lang.length > 0 ? lang : undefined,
        depth: dials.depth, breadth: dials.breadth, limit: dials.limit,
        steps: dials.steps, spread: dials.spread, max_stride: dials.max_stride,
        seed, directed: dials.directed, use_geometry: dials.use_geometry,
      }, { tenant, quoteId, signal });
    },
  });
  const result = resultRead.data;

  const querySeed = useAppStore((state) => state.querySeed);
  const setQuerySeed = useAppStore((state) => state.setQuerySeed);
  useEffect(() => {
    if (!querySeed) return;
    setTopic(querySeed.topic);
    setTopic2(querySeed.topic2 ?? '');
    setShape(querySeed.shape ?? 'describe');
    setRelationType(querySeed.relationType ?? '');
    setSelectedBands(querySeed.bands ?? []);
    setQuerySeed(null);
    autoRun.current = true;
  }, [querySeed, setQuerySeed]);
  useEffect(() => {
    // Do not execute a Home example against guessed, not-yet-loaded shape metadata.
    if (!autoRun.current || !canRun || querySeed != null) return;
    autoRun.current = false;
    void resultRead.reload();
  }, [canRun, querySeed, resultRead.reload, topic, topic2, shape, relationType, selectedBands]);

  return (
    <div className={styles.layout}>
      <section className={styles.controls} aria-label="Query controls">
        <Panel title="Ask">
          <form className={styles.stack} onSubmit={(event) => {
            event.preventDefault();
            if (canRun && !resultRead.busy) void resultRead.reload();
          }}>
            <Field label="topic" help="A word in any language, or a 32-character content id. Exact input is preserved." htmlFor="query-topic">
              <Input id="query-topic" required value={topic} placeholder="word or entity id" onChange={(event) => setTopic(event.target.value)} />
            </Field>
            {needsTopic2 && <Field label="second topic" help="The other end of the relation or path." htmlFor="query-topic2">
              <Input id="query-topic2" required value={topic2} placeholder="word or entity id" onChange={(event) => setTopic2(event.target.value)} />
            </Field>}
            <Field label="shape" help="Available read operations, supplied by the substrate catalog." htmlFor="query-shape">
              <Select id="query-shape" value={shape} onChange={(event) => setShape(event.target.value)}>
                {!active && <option value={shape} disabled>{shapesRead.busy ? 'Loading shapes…' : 'Choose an available shape'}</option>}
                {shapes?.map((item) => <option key={item.shape} value={item.shape}>{item.shape}</option>)}
              </Select>
            </Field>
            <ReadStatus label="Query shapes" resource={shapesRead} />
            {shapes && shapes.length === 0 && <Muted>No query shapes were returned by the catalog.</Muted>}
            {active && <Muted className={styles.shapeHelp}>{active.summary}</Muted>}
            {needsType && <Field label="relation type" help="The exact canonical relation name, for example HAS_PART." htmlFor="query-type">
              <Input id="query-type" required value={relationType} placeholder="HAS_PART" onChange={(event) => setRelationType(event.target.value)} />
            </Field>}
            {acceptsLang && <Field label="language" help="Optional target language for the returned surface." htmlFor="query-lang">
              <Input id="query-lang" value={lang} placeholder="any" onChange={(event) => setLang(event.target.value)} />
            </Field>}
            <Button type="submit" disabled={!canRun} loading={resultRead.busy}>Run query</Button>
          </form>
        </Panel>
        {usesBands && <Panel title="Lens">
          <ReadStatus label="Relation bands" resource={bandsRead} />
          {bandsRead.data && <BandPicker bands={bandsRead.data.bands ?? []} selected={selectedBands} onChange={setSelectedBands} />}
        </Panel>}
        <Panel title="Dials"><DialPanel shape={shape} dials={dials} onChange={setDials} /></Panel>
      </section>
      <section className={styles.results} aria-label="Query results">
        <Panel title="Result" fill actions={result?.topic_id ? (
          <RouterLink className={styles.entityLink} to={`/explore/entity/${result.topic_id}`}>
            open {result.topic_label} in Explore →
          </RouterLink>
        ) : null}>
          <ReadStatus label="Query result" resource={resultRead} />
          {resultRead.status === 'idle' && <div className={styles.empty}>
            <h3>Ask the graph directly.</h3>
            <p>Name a topic, choose an available read, and narrow it with the lens.</p>
            <Muted>The shape and lens specify the read; this is not conversational interpretation.</Muted>
          </div>}
          {result && <>
            <div className={styles.resultHead}>
              <span className={styles.resultTopic}>{result.topic_label}</span>
              {result.topic2_label && <span className={styles.resultTopic}>· {result.topic2_label}</span>}
              <Muted className={styles.resultMeta}>{result.shape} · {result.rows.length} row{result.rows.length === 1 ? '' : 's'}</Muted>
            </div>
            {result.rows.length === 0 ? <Muted>This read returned no rows.</Muted> : <ol className={styles.rows}>
              {result.rows.map((row, index) => <li key={index} className={styles.row}>
                <span className={styles.rowText}>{row.reply}</span>
                <ConsensusBadge mu={row.eff_mu ?? undefined} witnesses={row.witnesses ?? undefined} />
              </li>)}
            </ol>}
          </>}
        </Panel>
      </section>
    </div>
  );
}
