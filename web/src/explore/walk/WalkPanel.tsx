import { useState } from 'react';
import { Button, ErrorText, Field, Input, Muted, Panel, Stack } from '@ui';
import { apiPost, type ApiOptions } from '../../api/client';
import { useAppStore } from '../../store';
import { useExploreStore } from '../store';
import { GatePrompt } from '../components/GatePrompt';
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

  const quote = exploreQuote || quoteId;

  async function run() {
    setErr(null);
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
    }
  }

  return (
    <Panel title="Forward execution">
      {!unlocked ? (
        <GatePrompt
          serviceId="explain.trace"
          label="Receipts from the same query-relative forward execution used by conversation."
          onReady={() => setUnlocked(true)}
        />
      ) : (
        <Stack gap={4}>
          <div className={styles.controls}>
            <Field label="prompt" layout="row" htmlFor="walk-prompt">
              <Input id="walk-prompt" value={prompt} onChange={(e) => setPrompt(e.target.value)} aria-label="Prompt" />
            </Field>
            <Field label="max selections" layout="row" htmlFor="walk-steps">
              <Input id="walk-steps" type="number" min={1} max={256} value={steps} onChange={(e) => setSteps(Number(e.target.value))} />
            </Field>
            <Field label="semantic hops" layout="row" htmlFor="walk-depth">
              <Input id="walk-depth" type="number" min={1} max={32} value={depth} onChange={(e) => setDepth(Number(e.target.value))} />
            </Field>
            <Field label="typed fan-out" layout="row" htmlFor="walk-fanout">
              <Input id="walk-fanout" type="number" min={1} max={128} value={fanout} onChange={(e) => setFanout(Number(e.target.value))} />
            </Field>
            <Field label="election top-k" layout="row" htmlFor="walk-topk">
              <Input id="walk-topk" type="number" min={1} max={128} value={topK} onChange={(e) => setTopK(Number(e.target.value))} />
            </Field>
            <Button type="button" onClick={() => void run()}>Run forward trace</Button>
          </div>
          <Muted>
            Route and emit rows are receipts from the live election. Emitted selections overlay Graph and Glome tabs.
          </Muted>
          <ol className={styles.trace}>
            {trace.map((s, i) => {
              const support = s.support_relation
                ? ` · ${s.support_outbound === false ? '←' : '→'} ${s.support_relation}`
                : s.sequence_occurrences > 0
                  ? ' · trajectory'
                  : '';
              const standing = s.support_rating != null && s.support_rd != null
                ? ` μ ${s.support_rating} RD ${s.support_rd}`
                : '';
              const witnesses = s.support_witnesses != null
                ? ` · ${s.support_witnesses} witnesses / ${s.support_sources} sources`
                : '';
              return (
                <li key={`${s.step}-${s.routing_round}-${s.event}-${i}`}>
                  #{s.step} {s.event} r{s.routing_round} {s.entity || 'Unrealized entity'}
                  {' · '}candidates {s.candidate_count}
                  {' · '}Q→K {s.proposal_channel_count}
                  {' · '}exact {s.exact_channel_count}
                  {' · '}context {s.ordered_context_count}
                  {support}{standing}{witnesses}
                  {s.opposed_occurrences > 0 ? ` · opposed ${s.opposed_occurrences}` : ''}
                  {s.declared_result ? ' · declared result' : ''}
                </li>
              );
            })}
          </ol>
        </Stack>
      )}
      {err ? <ErrorText>{err}</ErrorText> : null}
    </Panel>
  );
}
