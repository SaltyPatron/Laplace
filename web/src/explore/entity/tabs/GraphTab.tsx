import { useEffect, useMemo, useState } from 'react';
import { Button, ErrorText, LoadingText, Muted, Panel } from '@ui';
import { PaymentRequiredError } from '../../../api/client';
import { exploreConsensusGraph } from '../../api';
import { GatePrompt } from '../../components/GatePrompt';
import { ConsensusGraph, type WebGraph } from '../../graph/ConsensusGraph';
import { useAppStore } from '../../../store';
import { useExploreStore } from '../../store';
import type { BillingReceipt, ExploreConsensusRow } from '../../types';
import styles from './GraphTab.module.css';

/** Matches server clamps — beam admits ≤fanout new nodes/hop, hard cap 160. */
const HOPS_MAX = 4;
const FANOUT_MAX = 16;

export function GraphTab({
  centerId,
  centerLabel,
  edgesOut,
  edgesIn,
  walkPath,
  onNodeClick,
}: {
  centerId: string;
  centerLabel: string;
  edgesOut: ExploreConsensusRow[];
  edgesIn: ExploreConsensusRow[];
  walkPath: { idHex: string; label: string }[];
  onNodeClick: (id: string) => void;
}) {
  const { tenant, quoteId } = useAppStore();
  const exploreQuote = useExploreStore((s) => s.quoteId);
  const quote = exploreQuote || quoteId;

  const [hops, setHops] = useState(2);
  const [fanout, setFanout] = useState(10);
  const [dim, setDim] = useState<'2d' | '3d'>('3d');
  const [web, setWeb] = useState<WebGraph | null>(null);
  const [truncated, setTruncated] = useState(false);
  const [maxNodes, setMaxNodes] = useState(0);
  const [busy, setBusy] = useState(false);
  const [needsGate, setNeedsGate] = useState(false);
  const [receipt, setReceipt] = useState<BillingReceipt | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [autoTried, setAutoTried] = useState(false);

  const starEdges = useMemo(
    () => [...edgesOut, ...edgesIn],
    [edgesOut, edgesIn],
  );

  async function expand() {
    setBusy(true);
    setErr(null);
    try {
      const res = await exploreConsensusGraph(centerId, hops, fanout, { tenant, quoteId: quote });
      setWeb({
        nodes: res.graph.nodes.map((n) => ({
          id: n.id_hex,
          label: n.label,
          hop: n.hop,
        })),
        edges: res.graph.edges.map((e) => ({
          source: e.source_id_hex,
          target: e.target_id_hex,
          type: e.type,
          mu: e.eff_mu,
          witnesses: e.witnesses,
          hop: e.hop,
        })),
      });
      setTruncated(Boolean(res.graph.truncated));
      setMaxNodes(Number(res.graph.max_nodes ?? 0));
      setReceipt(res.billing ?? null);
      setNeedsGate(false);
    } catch (e) {
      if (e instanceof PaymentRequiredError) {
        setNeedsGate(true);
        return;
      }
      setErr(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  useEffect(() => {
    setWeb(null);
    setTruncated(false);
    setNeedsGate(false);
    setErr(null);
    setAutoTried(false);
  }, [centerId]);

  useEffect(() => {
    if (autoTried || needsGate || web || busy) return;
    setAutoTried(true);
    void expand();
  }, [autoTried, centerId]);

  return (
    <Panel title="Consensus web" fill>
      {needsGate ? (
        <GatePrompt
          serviceId="visualization.deep_export"
          label="Expand multi-hop consensus fanout (beam crawl)."
          units={Math.max(1, hops * fanout)}
          receipt={receipt}
          onReady={() => void expand()}
        />
      ) : null}
      <ConsensusGraph
        centerId={centerId}
        centerLabel={centerLabel}
        edges={starEdges}
        web={web}
        walkPath={walkPath}
        onNodeClick={onNodeClick}
        fill
        hops={hops}
        fanout={fanout}
        hopsMax={HOPS_MAX}
        fanoutMax={FANOUT_MAX}
        onHopsChange={setHops}
        onFanoutChange={setFanout}
        dim={dim}
        onDimChange={setDim}
        toolbar={
          <div className={styles.actions}>
            <Button type="button" size="sm" onClick={() => void expand()} disabled={busy}>
              {busy ? 'Crawling…' : web ? 'Recrawl web' : 'Expand web'}
            </Button>
            {web ? (
              <Button type="button" size="sm" variant="ghost" onClick={() => { setWeb(null); setTruncated(false); }} disabled={busy}>
                Reset to 1-hop
              </Button>
            ) : null}
          </div>
        }
      />
      {truncated ? (
        <Muted className={styles.note}>
          Beam capped at {maxNodes || 160} nodes (≤{fanout} new / hop by eff_μ) — lower hops/fanout is denser per hop, not a full BFS.
        </Muted>
      ) : (
        <Muted className={styles.note}>
          Beam crawl: ≤{fanout} strongest new nodes per hop · ≤{HOPS_MAX} hops · pool-safe concurrency.
        </Muted>
      )}
      {busy && !web ? <LoadingText>Crawling consensus neighborhood…</LoadingText> : null}
      {err ? <ErrorText>{err}</ErrorText> : null}
    </Panel>
  );
}
