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
    let timer: number | undefined;
    let controller: AbortController | null = null;

    const schedule = () => {
      if (!alive || document.hidden) return;
      timer = window.setTimeout(() => void poll(), 30_000);
    };

    const poll = async () => {
      if (!alive || document.hidden || controller) return;
      controller = new AbortController();
      try {
        const res = await fetch('/health/status', { signal: controller.signal, credentials: 'same-origin' });
        if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
        const data = (await res.json()) as Readiness;
        if (alive) setReport(data.ready ? null : data);
      } catch (error) {
        if (alive && !(error instanceof DOMException && error.name === 'AbortError')) {
          setReport({
            ready: false,
            substrate_reachable: false,
            entities: 0,
            consensus_relations: 0,
            perfcache_ready: false,
            detail: 'Could not reach /health/status.',
          });
        }
      } finally {
        controller = null;
        schedule();
      }
    };

    const visibility = () => {
      if (timer) window.clearTimeout(timer);
      timer = undefined;
      if (document.hidden) controller?.abort();
      else void poll();
    };

    void poll();
    document.addEventListener('visibilitychange', visibility);
    return () => {
      alive = false;
      if (timer) window.clearTimeout(timer);
      controller?.abort();
      document.removeEventListener('visibilitychange', visibility);
    };
  }, []);

  if (!report) return null;
  return <Banner variant="warning"><strong>{statusTitle(report)}</strong> {defaultDetail(report)}</Banner>;
}
