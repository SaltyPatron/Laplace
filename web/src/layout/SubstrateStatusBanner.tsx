import { useEffect, useState } from 'react';
import { Banner } from '@ui';
import type { Schemas } from '../api/client';

type Readiness = Schemas['ReadinessResponse'];

function defaultDetail(report: Readiness): string {
  if (report.detail?.trim()) return report.detail.trim();
  if (!report.substrate_reachable) return 'PostgreSQL is unreachable at localhost:5432.';
  if (!report.perfcache_ready) return 'T0 perfcache is not loaded in Postgres.';
  if (Number(report.entities) === 0) return 'Substrate is empty — run seed-foundation.';
  if (Number(report.consensus_relations) === 0) return 'Substrate has no consensus — finish seeding.';
  return 'Substrate is not ready.';
}

export function SubstrateStatusBanner() {
  const [report, setReport] = useState<Readiness | null>(null);

  useEffect(() => {
    let alive = true;

    const poll = async () => {
      try {
        const res = await fetch('/health/ready');
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
          detail: 'Could not reach /health/ready.',
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
      <strong>Substrate unavailable.</strong> {defaultDetail(report)}
    </Banner>
  );
}
