import { useState } from 'react';
import { apiPost, type ApiOptions } from '../../api/client';
import { useAppStore } from '../../store';
import { useExploreStore } from '../store';
import { GatePrompt } from '../components/GatePrompt';

interface ExplainStep {
  depth: number;
  entity_id_hex?: string;
  entity_label: string;
  eff_mu: number;
  path_mu: number;
  witnesses: number;
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
        idHex: s.entity_id_hex ?? `walk-${i}`,
        label: s.entity_label,
      })));
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    }
  }

  return (
    <div className="walk-panel">
      <h3>Explain walk</h3>
      {!unlocked ? (
        <GatePrompt serviceId="explain.trace" label="Beam search trace over consensus graph." onReady={() => setUnlocked(true)} />
      ) : (
        <>
          <div className="row walk-controls">
            <input value={prompt} onChange={(e) => setPrompt(e.target.value)} aria-label="Prompt" />
            <label>
              depth
              <input type="number" min={1} max={8} value={depth} onChange={(e) => setDepth(Number(e.target.value))} />
            </label>
            <label>
              beam
              <input type="number" min={1} max={16} value={beam} onChange={(e) => setBeam(Number(e.target.value))} />
            </label>
            <button type="button" onClick={() => void run()}>Run walk</button>
          </div>
          <p className="muted">Path overlays Graph and Glome tabs on open entity pages.</p>
          <ol>
            {trace.map((s, i) => (
              <li key={i}>d{s.depth} {s.entity_label} path μ {s.path_mu.toFixed(2)} ({s.witnesses} wit)</li>
            ))}
          </ol>
        </>
      )}
      {err ? <p className="error">{err}</p> : null}
    </div>
  );
}
