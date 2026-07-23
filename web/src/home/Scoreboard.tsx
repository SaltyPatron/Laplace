import { useEffect, useRef, useState } from 'react';
import { usePulse } from './usePulse';
import styles from './Scoreboard.module.css';

/**
 * The live scoreboard. Game night for a substrate: the running totals of the
 * witnessed graph, climbing while a source is folded in. The status pill reads
 * the ingest heartbeat — green and rated when a source is at bat, quiet when the
 * league is between games. Nothing here is theatre; every number is the real
 * count, polled, and the rate is measured from consecutive samples.
 */
export function Scoreboard() {
  const { pulse, ratePerSec, reachable } = usePulse();

  return (
    <section className={styles.board} aria-label="Live substrate scoreboard">
      <div className={styles.status}>
        <StatusPill folding={pulse?.folding ?? false} reachable={reachable} rate={ratePerSec}
          lastFlushAt={pulse?.last_flush_at ?? null} />
      </div>
      <div className={styles.counts}>
        <Count value={pulse?.entities ?? null} label="Entities" hint="deduplicated content" />
        <Count value={pulse?.attestations ?? null} label="Attestations" hint="witnessed assertions" live />
        <Count value={pulse?.consensus ?? null} label="Consensus edges" hint="folded ratings" live />
        <Count value={pulse?.physicalities ?? null} label="Geometries" hint="content placed on S³" />
      </div>
    </section>
  );
}

function StatusPill({ folding, reachable, rate, lastFlushAt }: {
  folding: boolean; reachable: boolean; rate: number; lastFlushAt: number | null;
}) {
  if (!reachable) {
    return <span className={`${styles.pill} ${styles.down}`}><span className={styles.dot} /> substrate unreachable</span>;
  }
  if (folding) {
    return (
      <span className={`${styles.pill} ${styles.live}`}>
        <span className={`${styles.dot} ${styles.pulse}`} />
        folding{rate > 0 ? ` · ${formatRate(rate)}/s` : ''}
      </span>
    );
  }
  return (
    <span className={`${styles.pill} ${styles.idle}`}>
      <span className={styles.dot} /> idle{lastFlushAt ? ` · last fold ${ago(lastFlushAt)}` : ''}
    </span>
  );
}

function Count({ value, label, hint, live }: { value: number | null; label: string; hint: string; live?: boolean }) {
  const display = useCountUp(value);
  return (
    <div className={styles.count}>
      <span className={`${styles.value} ${live ? styles.valueLive : ''}`}>
        {value == null ? '—' : display.toLocaleString()}
      </span>
      <span className={styles.label}>{label}</span>
      <span className={styles.hint}>{hint}</span>
    </div>
  );
}

/** Tween the shown number toward its target so updates read as motion, not jumps. */
function useCountUp(target: number | null): number {
  const [shown, setShown] = useState(target ?? 0);
  const from = useRef(target ?? 0);
  const raf = useRef<number>(0);

  useEffect(() => {
    if (target == null) return;
    const start = from.current;
    const delta = target - start;
    if (delta === 0) return;
    // Big first paint (0 → millions) shouldn't crawl; short, eased tween.
    const dur = start === 0 ? 700 : 500;
    let t0 = 0;
    const step = (ts: number) => {
      if (!t0) t0 = ts;
      const k = Math.min(1, (ts - t0) / dur);
      const eased = 1 - Math.pow(1 - k, 3);
      const now = Math.round(start + delta * eased);
      setShown(now);
      if (k < 1) raf.current = requestAnimationFrame(step);
      else from.current = target;
    };
    raf.current = requestAnimationFrame(step);
    return () => cancelAnimationFrame(raf.current);
  }, [target]);

  return shown;
}

function formatRate(r: number): string {
  if (r >= 1000) return `${(r / 1000).toFixed(1)}k`;
  return String(Math.round(r));
}

function ago(unix: number): string {
  const s = Math.max(0, Math.floor(Date.now() / 1000 - unix));
  if (s < 60) return `${s}s ago`;
  if (s < 3600) return `${Math.floor(s / 60)}m ago`;
  if (s < 86400) return `${Math.floor(s / 3600)}h ago`;
  return `${Math.floor(s / 86400)}d ago`;
}
