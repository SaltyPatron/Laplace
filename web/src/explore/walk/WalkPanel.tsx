import { useState } from 'react';
import { Button, ErrorText, Field, Input, Muted, Panel, Stack } from '@ui';
import { apiPost, type ApiOptions } from '../../api/client';
import { useAppStore } from '../../store';
import { useExploreStore } from '../store';
import { GatePrompt } from '../components/GatePrompt';
import { relationWords } from '../../domain/Strand/StrandList';
import styles from './WalkPanel.module.css';

/** Exact receipt shape emitted by /v1/explain/report's canonical forward pass. */
interface ForwardTraceStep {
  step: number;
  entity_id_hex: string;
  entity: string;
  stride_used: number;
  root_id_hex: string;
  candidate_count: number;
  ordered_context_count: number;
  proposal_channel_count: number;
  exact_channel_count: number;
  sequence_occurrences: number;
  covered_occurrences: number;
  relation_families: number;
  opposed_occurrences: number;
  support_anchor_id_hex?: string;
  support_anchor?: string;
  support_relation_id_hex?: string;
  support_relation?: string;
  support_outbound?: boolean;
  support_rating?: number;
  support_rd?: number;
  support_witnesses?: number;
  support_sources: number;
  support_contexts: number;
  declared_result: boolean;
  event: 'route' | 'emit' | string;
  routing_round: number;
}

export function WalkPanel() {
  const { tenant, quoteId } = useAppStore();
  const exploreQuote = useExploreStore((s) => s.quoteId);
  const setWalkPath = useExploreStore((s) => s.setWalkPath);
  const [prompt, setPrompt] = useState('How does lightning work?');
  const [steps, setSteps] = useState(32);
  const [depth, setDepth] = useState(4);
  const [fanout, setFanout] = useState(8);
  const [topK, setTopK] = useState(10);
  const [trace, setTrace] = useState<ForwardTraceStep[]>([]);
  const [unlocked, setUnlocked] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [ran, setRan] = useState(false);

  const quote = exploreQuote || quoteId;

  async function run() {
    setErr(null);
    setBusy(true);
    const opts: ApiOptions = { tenant, quoteId: quote };
    try {
      const res = await apiPost<{ trace: ForwardTraceStep[] }>(
        '/v1/explain/report',
        {
          prompt,
          depth,
          beam: fanout,
          academic: false,
          steps,
          max_stride: 5,
          spread: 0,
          top_k: topK,
        },
        opts,
      );
      const receipts = res.trace ?? [];
      setTrace(receipts);
      const emitted = receipts.filter((s) => s.event === 'emit');
      setWalkPath(emitted.map((s, i) => ({
        idHex: s.entity_id_hex || `forward-${i}`,
        label: s.entity || `Unrealized selection ${i + 1}`,
      })));
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
      setRan(true);
    }
  }

  const emitted = trace.filter((s) => s.event === 'emit');
  return (
    <Panel title="Reasoning trace">
      {!unlocked ? (
        <GatePrompt
          serviceId="explain.trace"
          label="Step-by-step receipts from the same forward pass the conversation uses."
          onReady={() => setUnlocked(true)}
        />
      ) : (
        <Stack gap={4}>
          <form className={styles.controls} onSubmit={(e) => { e.preventDefault(); void run(); }}>
            <Field label="Prompt" layout="row" htmlFor="walk-prompt">
              <Input id="walk-prompt" value={prompt} onChange={(e) => setPrompt(e.target.value)} />
            </Field>
            <details className={styles.limits}>
              <summary>Limits</summary>
              <div className={styles.controls}>
                <Field label="Most words to choose" layout="row" htmlFor="walk-steps">
                  <Input id="walk-steps" type="number" min={1} max={256} value={steps} onChange={(e) => setSteps(Number(e.target.value))} />
                </Field>
                <Field label="Hops per choice" layout="row" htmlFor="walk-depth">
                  <Input id="walk-depth" type="number" min={1} max={32} value={depth} onChange={(e) => setDepth(Number(e.target.value))} />
                </Field>
                <Field label="Strands followed per hop" layout="row" htmlFor="walk-fanout">
                  <Input id="walk-fanout" type="number" min={1} max={128} value={fanout} onChange={(e) => setFanout(Number(e.target.value))} />
                </Field>
                <Field label="Candidates compared" layout="row" htmlFor="walk-topk">
                  <Input id="walk-topk" type="number" min={1} max={128} value={topK} onChange={(e) => setTopK(Number(e.target.value))} />
                </Field>
              </div>
            </details>
            <Button type="submit" disabled={busy || !prompt.trim()}>{busy ? 'Tracing…' : 'Trace'}</Button>
          </form>
          {ran && trace.length === 0 && !err ? (
            <Muted role="status">The forward pass returned no steps for this prompt: nothing recorded connects to it strongly enough to choose a word.</Muted>
          ) : null}
          {trace.length > 0 ? (
            <>
              <Muted>
                {emitted.length > 0
                  ? `Chose ${emitted.length} ${emitted.length === 1 ? 'word' : 'words'}: ${emitted.map((s) => s.entity || '(unrealised)').join(' ')}. Chosen words are also drawn on the Web and Placement views.`
                  : 'Routed but chose no words.'}
              </Muted>
              <ol className={styles.trace}>
                {trace.map((s, i) => (
                  <li key={`${s.step}-${s.routing_round}-${s.event}-${i}`}>
                    <strong>{s.event === 'emit' ? 'Chose' : 'Considered'} {s.entity || 'an unrealised entity'}</strong>
                    <span className={styles.detail}>
                      {[
                        `step ${s.step}${s.routing_round ? `, round ${s.routing_round + 1}` : ''}`,
                        `${s.candidate_count.toLocaleString()} ${s.candidate_count === 1 ? 'candidate' : 'candidates'}`,
                        s.support_relation
                          ? `${s.support_outbound === false ? 'reached from' : 'via'} ${relationWords(s.support_relation)}${s.support_anchor ? ` (${s.support_anchor})` : ''}`
                          : s.sequence_occurrences > 0 ? `follows recorded text ${s.sequence_occurrences.toLocaleString()} ${s.sequence_occurrences === 1 ? 'time' : 'times'}` : null,
                        s.support_witnesses != null ? `${s.support_witnesses.toLocaleString()} witnesses from ${s.support_sources.toLocaleString()} ${s.support_sources === 1 ? 'source' : 'sources'}` : null,
                        s.support_rating != null && s.support_rd != null ? `standing ${Math.round(s.support_rating)} ± ${Math.round(2 * s.support_rd)}` : null,
                        s.opposed_occurrences > 0 ? `opposed ${s.opposed_occurrences.toLocaleString()} ${s.opposed_occurrences === 1 ? 'time' : 'times'}` : null,
                        s.declared_result ? 'declared result' : null,
                      ].filter(Boolean).join(' · ')}
                    </span>
                  </li>
                ))}
              </ol>
            </>
          ) : null}
        </Stack>
      )}
      {err ? <ErrorText>{err}</ErrorText> : null}
    </Panel>
  );
}
