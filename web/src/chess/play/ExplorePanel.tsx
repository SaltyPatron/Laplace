import { useEffect, useState } from 'react';
import { cn, Input, Muted } from '@ui';
import { apiPost } from '../../api/client';
import { Panel } from './Panel';
import styles from './ExplorePanel.module.css';

interface ExploreMove {
  uci: string;
  san: string;
  effMu: number;
  rd: number;
  witnesses: number;
  playerGames: number;
  playerScore: number | null;
}

interface ExploreResponse {
  fen: string;
  player: string | null;
  moves: ExploreMove[];
}

const fmtDev = (mu: number) => `${mu >= 0 ? '+' : ''}${mu.toFixed(1)}`;

export function ExplorePanel({ fen, onPlayMove }: { fen: string; onPlayMove?: (uci: string) => void }) {
  const [playerInput, setPlayerInput] = useState('');
  const [player, setPlayer] = useState('');
  const [data, setData] = useState<ExploreResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    const timer = setTimeout(() => {
      void apiPost<ExploreResponse>('/chess/explore', { fen, player: player || undefined })
        .then((r) => { if (!cancelled) { setData(r); setErr(null); } })
        .catch((e) => { if (!cancelled) setErr(e instanceof Error ? e.message : String(e)); })
        .finally(() => { if (!cancelled) setLoading(false); });
    }, 200);
    return () => { cancelled = true; clearTimeout(timer); };
  }, [fen, player]);

  const hasPlayer = !!data?.player;
  const moves = data?.moves ?? [];

  return (
    <Panel title={<>Explore {loading && <Muted>…</Muted>}</>}>
      <Muted>Attested continuations for this position — Glicko dev, positive = good for the mover.</Muted>
      <Input
        value={playerInput}
        placeholder="player filter, e.g. magnuscarlsen"
        aria-label="Player filter"
        onChange={(e) => setPlayerInput(e.target.value)}
        onBlur={() => setPlayer(playerInput.trim())}
        onKeyDown={(e) => { if (e.key === 'Enter') setPlayer(playerInput.trim()); }}
      />
      {err && <Muted>explore failed: {err}</Muted>}
      {!err && data && moves.length === 0 && <Muted>No attested continuations here{hasPlayer ? ` for ${data.player}` : ''}.</Muted>}
      {moves.length > 0 && (
        <table className={styles.table}>
          <thead>
            <tr>
              <th>move</th>
              <th className={styles.num}>dev</th>
              <th className={styles.num}>wit</th>
              {hasPlayer && <th className={styles.num}>games</th>}
              {hasPlayer && <th className={styles.num}>score</th>}
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
                {hasPlayer && <td className={styles.num}>{m.playerGames.toLocaleString()}</td>}
                {hasPlayer && <td className={styles.num}>{m.playerScore === null ? '—' : `${Math.round(m.playerScore * 100)}%`}</td>}
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Panel>
  );
}
