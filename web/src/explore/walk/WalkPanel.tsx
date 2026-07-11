import { useState } from 'react';
import { Button, ErrorText, Field, Input, Muted, Panel, Stack } from '@ui';
import { apiPost, type ApiOptions } from '../../api/client';
import { useAppStore } from '../../store';
import { useExploreStore } from '../store';
import { GatePrompt } from '../components/GatePrompt';
import styles from './WalkPanel.module.css';

/** Matches /v1/explain/report trace steps (camelCase JSON). */
interface ExplainStep {
  depth: number;
  entityIdHex?: string;
  entityLabel?: string;
  effectiveMu?: number;
  pathMu?: number;
  witnesses?: number;
}

function formatMu(n: number | undefined): string {
  return typeof n === 'number' && Number.isFinite(n) ? n.toFixed(2) : '—';
}

export function WalkPanel() {
  const { tenant, quoteId } = useAppStore();
  const exploreQuote = useExploreStore((s) => s.quoteId);
  const setWalkPath = useExploreStore((s) => s.setWalkPath);
  const [prompt, setPrompt] = useState('dog');
  const [depth, setDepth] = useState(4);
  const [beam, setBeam] = useState(5);
  const [trace, setTrace] = useState<ExplainStep[]>([]);
  const [unlocked, setUnlocked] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  const quote = exploreQuote || quoteId;

  async function run() {
    setErr(null);
    const opts: ApiOptions = { tenant, quoteId: quote };
    try {
      const res = await apiPost<{ trace: ExplainStep[] }>(
        '/v1/explain/report',
        { prompt, depth, beam, academic: false },
        opts,
      );
      const steps = res.trace ?? [];
      setTrace(steps);
      setWalkPath(steps.map((s, i) => ({
        idHex: s.entityIdHex ?? `walk-${i}`,
        label: s.entityLabel ?? s.entityIdHex ?? `step ${i}`,
      })));
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    }
  }

  return (
    <Panel title="Explain walk">
      {!unlocked ? (
        <GatePrompt serviceId="explain.trace" label="Beam search trace over consensus graph." onReady={() => setUnlocked(true)} />
      ) : (
        <Stack gap={4}>
          <div className={styles.controls}>
            <Field label="prompt" layout="row" htmlFor="walk-prompt">
              <Input id="walk-prompt" value={prompt} onChange={(e) => setPrompt(e.target.value)} aria-label="Prompt" />
            </Field>
            <Field label="depth" layout="row" htmlFor="walk-depth">
              <Input id="walk-depth" type="number" min={1} max={8} value={depth} onChange={(e) => setDepth(Number(e.target.value))} />
            </Field>
            <Field label="beam" layout="row" htmlFor="walk-beam">
              <Input id="walk-beam" type="number" min={1} max={16} value={beam} onChange={(e) => setBeam(Number(e.target.value))} />
            </Field>
            <Button type="button" onClick={() => void run()}>Run walk</Button>
          </div>
          <Muted>Path overlays Graph and Glome tabs on open entity pages.</Muted>
          <ol className={styles.trace}>
            {trace.map((s, i) => (
              <li key={i}>
                d{s.depth} {s.entityLabel ?? s.entityIdHex ?? '—'} path μ {formatMu(s.pathMu)}
                {s.witnesses != null ? ` (${s.witnesses} wit)` : ''}
              </li>
            ))}
          </ol>
        </Stack>
      )}
      {err ? <ErrorText>{err}</ErrorText> : null}
    </Panel>
  );
}
