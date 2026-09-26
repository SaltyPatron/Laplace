import { useRef, useState } from 'react';
import { apiGet, describeFailure } from '../api/client';
import { useVisiblePolling } from '@ui';

export interface Pulse {
  at: number;
  entities: number;
  attestations: number;
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
 * The live scoreboard feed. Polls /v1/pulse on an interval and derives the fold
 * rate from consecutive samples — the substrate at rest reads zero, a source at
 * bat reads its throughput. Cheap by construction (estimate counts + a 1.5ms
 * recency query), so a few-second cadence costs nothing.
 */
export function usePulse(intervalMs = 4000): PulseState {
  const [pulse, setPulse] = useState<Pulse | null>(null);
  const [ratePerSec, setRate] = useState(0);
  const [reachable, setReachable] = useState(true);
  const [failure, setFailure] = useState<string | null>(null);
  const prev = useRef<Pulse | null>(null);

  useVisiblePolling(async () => {
    try {
      const next = await apiGet<Pulse>('/v1/pulse');
      setReachable(true);
      setFailure(null);
      const p = prev.current;
      if (p && next.at > p.at) {
        const dAtt = next.attestations - p.attestations;
        const dt = next.at - p.at;
        setRate(dAtt > 0 ? dAtt / dt : 0);
      }
      prev.current = next;
      setPulse(next);
    } catch (error) {
      const failed = describeFailure(error);
      setReachable(failed.kind !== 'network' && failed.kind !== 'server' && failed.kind !== 'unavailable');
      setFailure(failed.message);
    }
  }, { intervalMs });


  return { pulse, ratePerSec, reachable, failure };
}
