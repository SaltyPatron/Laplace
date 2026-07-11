import { useEffect, useMemo, useState } from 'react';
import { ErrorText, LoadingText, Muted } from '@ui';

import { apiPost, type ApiOptions, type Schemas } from '../../api/client';

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
      radius: Number(n.radius ?? 1),
      kind: 'primary' as const,
    }));
}

export function ConstellationView() {
  const { tenant, quoteId } = useAppStore();
  const exploreQuote = useExploreStore((s) => s.quoteId);
  const [graph, setGraph] = useState<VizResponse | null>(null);
  const [unlocked, setUnlocked] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [receipt, setReceipt] = useState<BillingReceipt | null>(null);

  const quote = exploreQuote || quoteId;

  async function load() {
    setErr(null);
    const opts: ApiOptions = { tenant, quoteId: quote };
    try {
      const res = await apiPost<VizResponse>(
        '/v1/visualizations/substrate',
        { limit: 80, include_geometry: true, include_evidence: false },
        opts,
      );
      setGraph(res);
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
      setErr(e instanceof Error ? e.message : String(e));
    }
  }

  useEffect(() => {
    if (unlocked) void load();
  }, [unlocked]);

  const nodes = useMemo(() => (graph ? nodesFromGraph(graph.graph) : []), [graph]);

  return (
    <div className={styles.root}>
      <h2>Substrate constellation</h2>
      <Muted className={styles.lead}>Warehouse-scale S³ sample via visualization.deep_export.</Muted>
      {!unlocked ? (
        <GatePrompt
          serviceId="visualization.deep_export"
          label="Load a gated substrate graph with geometry for the glome viewer."
          units={80}
          receipt={receipt}
          onReady={() => setUnlocked(true)}
        />
      ) : graph ? (
        <div className={styles.viewer}>
          <GlomeCanvas nodes={nodes} fill />
        </div>
      ) : err ? (
        <ErrorText>{err}</ErrorText>
      ) : (
        <LoadingText>Loading constellation…</LoadingText>
      )}
    </div>
  );
}
