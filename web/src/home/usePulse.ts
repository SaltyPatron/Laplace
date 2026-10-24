import { useLayoutEffect, useRef, useState } from 'react';
import { apiGet, describeFailure } from '../api/client';
import { useVisiblePolling } from '@ui';
import { useAppStore } from '../store';

export interface Pulse {
  at: number;
  entities: number;
  attestations: number | null;
  consensus: number;
  physicalities: number;
  last_flush_at?: number | null;
  flushes_last_min: number;
  folding: boolean;
}

export interface PulseState {
  pulse: Pulse | null;
  /** Attestations folded per second, from the last two samples (0 when idle). */
  ratePerSec: number;
  reachable: boolean;
  /** Why the last read failed, when it did: a refused read is not an unreachable host. */
  failure: string | null;
}

/**
 * Polls /v1/pulse while the document is visible and derives the attestation fold
 * rate from consecutive samples (zero at rest).
 */
export function usePulse(intervalMs = 4000): PulseState {
  const authReady = useAppStore((state) => state.authReady);
  const scope = useAppStore((state) => JSON.stringify([state.tenant, state.authUser?.id, state.devPrincipal]));
  const [refusedScope, setRefusedScope] = useState<string | null>(null);
  const activeScope = useRef(scope);
  const [pulse, setPulse] = useState<Pulse | null>(null);
  const [ratePerSec, setRate] = useState(0);
  const [reachable, setReachable] = useState(true);
  const [failure, setFailure] = useState<string | null>(null);
  const prev = useRef<Pulse | null>(null);

  useLayoutEffect(() => {
    activeScope.current = scope;
    setRefusedScope(null);
    prev.current = null;
    setPulse(null);
    setRate(0);
    setFailure(null);
    setReachable(true);
  }, [scope]);

  useVisiblePolling(async () => {
    try {
      const next = await apiGet<Pulse>('/v1/pulse');
      if (activeScope.current !== scope) return;
      setReachable(true);
      setFailure(null);
      const p = prev.current;
      if (p && next.at > p.at) {
        const dAtt = next.attestations == null || p.attestations == null ? 0 : next.attestations - p.attestations;
        const dt = next.at - p.at;
        setRate(dAtt > 0 ? dAtt / dt : 0);
      }
      prev.current = next;
      setPulse(next);
    } catch (error) {
      if (activeScope.current !== scope) return;
      const failed = describeFailure(error);
      if (failed.kind === 'auth' || failed.kind === 'forbidden') setRefusedScope(scope);
      setReachable(failed.kind !== 'network' && failed.kind !== 'server' && failed.kind !== 'unavailable');
      setFailure(failed.message);
    }
  }, { intervalMs, enabled: authReady && refusedScope !== scope });


  return { pulse, ratePerSec, reachable, failure };
}
