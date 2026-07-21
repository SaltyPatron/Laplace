import { useEffect, useMemo, useState } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import {
  Button,
  ConsensusBadge,
  ErrorText,
  Field,
  Input,
  LoadingText,
  Muted,
  Panel,
  Select,
} from '@ui';

import { useAppStore } from '../store';
import { BandPicker } from './BandPicker';
import { DialPanel } from './DialPanel';
import { queryShapes, relationBands, runQuery } from './api';
import { DIAL_DEFAULTS, type QueryDials, type QueryResult, type QueryShape, type RelationBand } from './types';
import styles from './QueryConsole.module.css';

/**
 * The structural read console.
 *
 * A query here is (topic, lens, shape, dials). None of those are guessed from
 * the phrasing of a question — the substrate is content-addressed and
 * language-agnostic, and this surface asks it in its own terms. Every control
 * is populated from the substrate's own catalogs (`/v1/query/shapes`,
 * `/v1/query/bands`), so the panel shows what is actually there.
 */
export function QueryConsole() {
  const { tenant, quoteId } = useAppStore();

  const [shapes, setShapes] = useState<QueryShape[]>([]);
  const [bands, setBands] = useState<RelationBand[]>([]);
  const [catalogError, setCatalogError] = useState<string | null>(null);

  const [topic, setTopic] = useState('');
  const [topic2, setTopic2] = useState('');
  const [shape, setShape] = useState('describe');
  const [relationType, setRelationType] = useState('');
  const [lang, setLang] = useState('');
  const [selectedBands, setSelectedBands] = useState<number[]>([]);
  const [dials, setDials] = useState<QueryDials>(DIAL_DEFAULTS);

  const [result, setResult] = useState<QueryResult | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const opts = { tenant, quoteId };
    Promise.all([queryShapes(opts), relationBands(opts)])
      .then(([s, b]) => {
        setShapes(s.shapes ?? []);
        setBands(b.bands ?? []);
      })
      .catch((e) => setCatalogError(e instanceof Error ? e.message : String(e)));
  }, [tenant, quoteId]);

  const active = useMemo(() => shapes.find((s) => s.shape === shape), [shapes, shape]);
  const needsTopic2 = active?.needs_topic2 || shape === 'path';
  const needsType = active?.needs_type ?? false;
  const acceptsLang = active?.accepts_lang ?? false;
  const usesBands = shape === 'band_facts' || shape === 'beam';

  async function run() {
    if (!topic.trim() || busy) return;
    setBusy(true);
    setError(null);
    try {
      const seed = dials.seed.trim() === '' ? undefined : Number(dials.seed);
      const res = await runQuery(
        {
          topic: topic.trim(),
          topic2: needsTopic2 && topic2.trim() ? topic2.trim() : undefined,
          shape,
          bands: usesBands && selectedBands.length ? selectedBands : undefined,
          relation_type: needsType && relationType.trim() ? relationType.trim() : undefined,
          lang: acceptsLang && lang.trim() ? lang.trim() : undefined,
          depth: dials.depth,
          breadth: dials.breadth,
          limit: dials.limit,
          steps: dials.steps,
          spread: dials.spread,
          max_stride: dials.max_stride,
          seed: Number.isFinite(seed) ? seed : undefined,
          directed: dials.directed,
          use_geometry: dials.use_geometry,
        },
        { tenant, quoteId },
      );
      setResult(res);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      setResult(null);
    } finally {
      setBusy(false);
    }
  }

  if (catalogError) {
    return (
      <Panel title="Query">
        <ErrorText>{catalogError}</ErrorText>
      </Panel>
    );
  }

  return (
    <div className={styles.layout}>
      <section className={styles.controls}>
        <Panel title="Ask">
          <div className={styles.stack}>
            <Field
              label="topic"
              help="A word in any language, or a 32-character content id. Same content, same id — the substrate resolves either."
              htmlFor="query-topic"
            >
              <Input
                id="query-topic"
                value={topic}
                placeholder="word or entity id"
                onChange={(e) => setTopic(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') void run();
                }}
              />
            </Field>

            {needsTopic2 && (
              <Field label="second topic" help="The other end of the relation or path." htmlFor="query-topic2">
                <Input
                  id="query-topic2"
                  value={topic2}
                  placeholder="word or entity id"
                  onChange={(e) => setTopic2(e.target.value)}
                />
              </Field>
            )}

            <Field
              label="shape"
              help="What kind of read to run. Published by the substrate, not hardcoded here."
              htmlFor="query-shape"
            >
              <Select id="query-shape" value={shape} onChange={(e) => setShape(e.target.value)}>
                {shapes.map((s) => (
                  <option key={s.shape} value={s.shape}>
                    {s.shape}
                  </option>
                ))}
              </Select>
            </Field>
            {active && <Muted className={styles.shapeHelp}>{active.summary}</Muted>}

            {needsType && (
              <Field
                label="relation type"
                help="A canonical relation name, e.g. HAS_PART, CAUSES, IS_ANTONYM_OF."
                htmlFor="query-type"
              >
                <Input
                  id="query-type"
                  value={relationType}
                  placeholder="HAS_PART"
                  onChange={(e) => setRelationType(e.target.value.toUpperCase())}
                />
              </Field>
            )}

            {acceptsLang && (
              <Field
                label="language"
                help="Target language for the surface. Meaning is held at the ILI hub, so any language can be asked for from any other."
                htmlFor="query-lang"
              >
                <Input
                  id="query-lang"
                  value={lang}
                  placeholder="any"
                  onChange={(e) => setLang(e.target.value)}
                />
              </Field>
            )}

            <Button onClick={() => void run()} disabled={busy || !topic.trim()} loading={busy}>
              {busy ? '…' : 'Run query'}
            </Button>
          </div>
        </Panel>

        {usesBands && (
          <Panel title="Lens">
            <BandPicker bands={bands} selected={selectedBands} onChange={setSelectedBands} />
          </Panel>
        )}

        <Panel title="Dials">
          <DialPanel shape={shape} dials={dials} onChange={setDials} />
        </Panel>
      </section>

      <section className={styles.results}>
        <Panel
          title="Result"
          fill
          actions={
            result?.topic_id ? (
              <RouterLink className={styles.entityLink} to={`/explore/entity/${result.topic_id}`}>
                open {result.topic_label} in Explore →
              </RouterLink>
            ) : null
          }
        >
          {error && <ErrorText>{error}</ErrorText>}
          {busy && !result && <LoadingText>Reading consensus…</LoadingText>}

          {!error && !busy && !result && (
            <div className={styles.empty}>
              <h3>Ask the graph directly.</h3>
              <p>
                Name a topic, pick the shape of read you want, and narrow it with the lens. Every
                answer comes back with the consensus rating and the witness count that produced it.
              </p>
              <Muted>
                Nothing here reads your phrasing — the shape and the lens are the question.
              </Muted>
            </div>
          )}

          {result && (
            <>
              <div className={styles.resultHead}>
                <span className={styles.resultTopic}>{result.topic_label}</span>
                {result.topic2_label && <span className={styles.resultTopic}>· {result.topic2_label}</span>}
                <Muted className={styles.resultMeta}>
                  {result.shape} · {result.rows.length} row{result.rows.length === 1 ? '' : 's'}
                </Muted>
              </div>

              {result.rows.length === 0 ? (
                <Muted>Nothing is witnessed for that read yet.</Muted>
              ) : (
                <ol className={styles.rows}>
                  {result.rows.map((row, i) => (
                    <li key={i} className={styles.row}>
                      <span className={styles.rowText}>{row.reply}</span>
                      <ConsensusBadge
                        mu={row.eff_mu ?? undefined}
                        witnesses={row.witnesses ?? undefined}
                      />
                    </li>
                  ))}
                </ol>
              )}
            </>
          )}
        </Panel>
      </section>
    </div>
  );
}
