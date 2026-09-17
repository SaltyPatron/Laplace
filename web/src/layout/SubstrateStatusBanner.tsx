import { useEffect, useState } from 'react';
import { Banner } from '@ui';
import type { Schemas } from '../api/client';

type Readiness = Schemas['ReadinessResponse'];

function defaultDetail(report: Readiness): string {
  if (report.detail?.trim()) return report.detail.trim();
  if (!report.substrate_reachable) return 'The PostgreSQL substrate cannot be reached.';
  if (!report.perfcache_ready) return 'The T0 perfcache is not loaded.';
  if (Number(report.entities) === 0) return 'The substrate is empty.';
  if (Number(report.consensus_relations) === 0) return 'The substrate has no consensus relations yet.';
  return 'Substrate is not ready.';
}

function statusTitle(report: Readiness): string {
  if (!report.substrate_reachable) return 'Substrate unavailable.';
  if (!report.perfcache_ready) return 'Substrate runtime incomplete.';
  if (Number(report.entities) === 0 || Number(report.consensus_relations) === 0)
    return 'Substrate incomplete.';
  return 'Substrate not ready.';
}

export function SubstrateStatusBanner() {
  const [report, setReport] = useState<Readiness | null>(null);

  useEffect(() => {
    let alive = true;

    const poll = async () => {
      try {
        const res = await fetch('/health/status');
        const data = (await res.json()) as Readiness;
        if (!alive) return;
        setReport(data.ready ? null : data);
      } catch {
        if (!alive) return;
        setReport({
          ready: false,
          substrate_reachable: false,
          entities: 0,
          consensus_relations: 0,
          perfcache_ready: false,
          detail: 'Could not reach /health/status.',
        });
      }
    };

    void poll();
    const id = window.setInterval(poll, 30_000);
    return () => {
      alive = false;
      window.clearInterval(id);
    };
  }, []);

  if (!report) return null;

  return (
    <Banner variant="warning">
      <strong>{statusTitle(report)}</strong> {defaultDetail(report)}
    </Banner>
  );
}
