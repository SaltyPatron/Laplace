import { useEffect, useMemo, useState } from 'react';
import { ErrorText, LoadingText, Muted } from '@ui';

import { apiPost, PaymentRequiredError, type ApiOptions, type Schemas } from '../../api/client';

import { useAppStore } from '../../store';

import { useExploreStore } from '../store';

import { GatePrompt } from '../components/GatePrompt';
import type { BillingReceipt } from '../types';
import { GlomeCanvas, type GlomeNode } from './GlomeCanvas';
import styles from './ConstellationView.module.css';

type VizResponse = Schemas['VisualizationGraphResponse'];

function nodesFromGraph(graph: VizResponse['graph']): GlomeNode[] {
  return (graph?.nodes ?? [])
    .filter((n) => n.x != null && n.y != null && n.z != null)
    .map((n) => ({
      id: n.idHex ?? '',
      label: n.label ?? n.idHex ?? '',
      x: Number(n.x),
      y: Number(n.y),
      z: Number(n.z),
      m: Number(n.m ?? 0),
      radius: Number(n.radius ?? 1),
      kind: 'primary' as const,
    }));
}

export function ConstellationView() {
  const { tenant, quoteId } = useAppStore();
  const exploreQuote = useExploreStore((s) => s.quoteId);
  const [graph, setGraph] = useState<VizResponse | null>(null);
  const [needsGate, setNeedsGate] = useState(false);
  const [busy, setBusy] = useState(false);
  const [autoTried, setAutoTried] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [receipt, setReceipt] = useState<BillingReceipt | null>(null);

  const quote = exploreQuote || quoteId;

  async function load() {
    setBusy(true);
    setErr(null);
    const opts: ApiOptions = { tenant, quoteId: quote };
    try {
      const res = await apiPost<VizResponse>(
        '/v1/visualizations/substrate',
        { limit: 80, include_geometry: true, include_evidence: false },
        opts,
      );
      setGraph(res);
      setNeedsGate(false);
      if (res.billing) {
        setReceipt({
          quote_id: String(res.billing.quote_id),
          amount_cents: Number(res.billing.amount_cents),
          currency: String(res.billing.currency),
          tenant: String(res.billing.tenant),
          service_id: String(res.billing.service_id),
        });
      }
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
    if (autoTried || graph || needsGate || busy) return;
    setAutoTried(true);
    void load();
  }, [autoTried, graph, needsGate, busy]);

  const nodes = useMemo(() => (graph ? nodesFromGraph(graph.graph) : []), [graph]);

  return (
    <div className={styles.root}>
      <h2>Substrate constellation</h2>
      <Muted className={styles.lead}>
        Hilbert-stratified S³ coverage of stored physicalities — not a top-relations leaderboard.
        {graph ? ` ${nodes.length} occupied strata shown.` : ''}
      </Muted>
      {needsGate ? (
        <GatePrompt
          serviceId="visualization.deep_export"
          label="Load the Hilbert-stratified substrate geometry sample."
          units={80}
          receipt={receipt}
          onReady={() => void load()}
        />
      ) : graph ? (
        <div className={styles.viewer}>
          <GlomeCanvas nodes={nodes} fill />
        </div>
      ) : err ? (
        <ErrorText>{err}</ErrorText>
      ) : (
        <LoadingText>{busy ? 'Loading constellation…' : 'Preparing constellation…'}</LoadingText>
      )}
    </div>
  );
}
