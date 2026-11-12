import { useEffect, useState } from 'react';
import { cn, Muted, Table } from '@ui';
import { apiPost } from '../../api/client';
import { Panel } from './Panel';
import styles from './ExplorePanel.module.css';

interface ExploreMove {
  uci: string;
  san: string;
  effMu: number;
  rd: number;
  witnesses: number;
}

interface ExploreResponse {
  fen: string;
  moves: ExploreMove[];
}

const fmtDev = (mu: number) => `${mu >= 0 ? '+' : ''}${mu.toFixed(1)}`;

export function ExplorePanel({ fen, onPlayMove }: { fen: string; onPlayMove?: (uci: string) => void }) {
  const [data, setData] = useState<ExploreResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    const timer = setTimeout(() => {
      void apiPost<ExploreResponse>('/chess/explore', { fen })
        .then((r) => { if (!cancelled) { setData(r); setErr(null); } })
        .catch((e) => { if (!cancelled) setErr(e instanceof Error ? e.message : String(e)); })
        .finally(() => { if (!cancelled) setLoading(false); });
    }, 200);
    return () => { cancelled = true; clearTimeout(timer); };
  }, [fen]);

  const moves = data?.moves ?? [];

  return (
    <Panel title={<>Explore {loading && <Muted>…</Muted>}</>}>
      <Muted>Attested continuations for this position — Glicko dev, positive = good for the mover.</Muted>
      {err && <Muted>explore failed: {err}</Muted>}
      {!err && data && moves.length === 0 && <Muted>No attested continuations here.</Muted>}
      {moves.length > 0 && (
        <Table className={styles.table}>
          <thead>
            <tr>
              <th>move</th>
              <th className={styles.num}>dev</th>
              <th className={styles.num}>wit</th>
            </tr>
          </thead>
          <tbody>
            {moves.map((m) => (
              <tr
                key={m.uci}
                className={cn(onPlayMove && styles.clickable)}
                onClick={onPlayMove ? () => onPlayMove(m.uci) : undefined}
                title={onPlayMove ? `Play ${m.san}` : undefined}
              >
                <td className={styles.san}>{m.san}</td>
                <td className={styles.num}>{fmtDev(m.effMu)}</td>
                <td className={styles.num}>{m.witnesses.toLocaleString()}</td>
              </tr>
            ))}
          </tbody>
        </Table>
      )}
    </Panel>
  );
}
