import { useEffect, useRef, useState } from 'react';
import { apiGet } from '../api/client';

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
  const prev = useRef<Pulse | null>(null);

  useEffect(() => {
    let stopped = false;
    let timer: ReturnType<typeof setTimeout>;

    const tick = async () => {
      try {
        const next = await apiGet<Pulse>('/v1/pulse');
        if (stopped) return;
        setReachable(true);
        const p = prev.current;
        if (p && next.at > p.at) {
          const dAtt = next.attestations - p.attestations;
          const dt = next.at - p.at;
          // Estimate counts can jitter down slightly; a negative delta is noise,
          // not a fold, so it floors at zero.
          setRate(dAtt > 0 ? dAtt / dt : 0);
        }
        prev.current = next;
        setPulse(next);
      } catch {
        if (!stopped) setReachable(false);
      } finally {
        if (!stopped) timer = setTimeout(tick, intervalMs);
      }
    };

    void tick();
    return () => { stopped = true; clearTimeout(timer); };
  }, [intervalMs]);

  return { pulse, ratePerSec, reachable };
}
