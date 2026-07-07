import { useEffect, useMemo, useState } from 'react';
import { apiGet } from '../../api/client';
import {
  Alert,
  Button,
  cn,
  IconButton,
  LoadingText,
  Muted,
  Panel,
  Tooltip,
  TooltipContent,
  TooltipTrigger,
} from '@ui';
import { PIECE_NAME, PIECES } from './pstConstants';
import styles from './PstGrid.module.css';

interface LearnedSquare { piece: string; file: number; rank: number; devPoints: number; witness: number; }

export function PstGrid() {
  const [squares, setSquares] = useState<LearnedSquare[] | null>(null);
  const [piece, setPiece] = useState<string>('P');
  const [err, setErr] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  const load = async () => {
    setLoading(true); setErr(null);
    try { setSquares(await apiGet<LearnedSquare[]>('/chess/learned-pst')); }
    catch (e) { setErr(e instanceof Error ? e.message : String(e)); }
    finally { setLoading(false); }
  };

  useEffect(() => { void load(); }, []);

  const forPiece = useMemo(
    () => (squares ?? []).filter((s) => s.piece === piece),
    [squares, piece]);
  const maxAbs = useMemo(
    () => Math.max(1e-9, ...forPiece.map((s) => Math.abs(s.devPoints))),
    [forPiece]);
  const covered = useMemo(() => forPiece.filter((s) => s.witness > 0).length, [forPiece]);
  const totalWitness = useMemo(() => forPiece.reduce((a, s) => a + s.witness, 0), [forPiece]);

  const at = (file: number, rank: number) => forPiece.find((s) => s.file === file && s.rank === rank);

  return (
    <Panel title="Learned PST (PeSTO + corpus)" actions={
      <div className={styles.pieces}>
        <Button variant="ghost" size="sm" onClick={() => void load()} disabled={loading}>
          {loading ? '…' : 'Refresh'}
        </Button>
        {PIECES.map((p) => (
          <Tooltip key={p}>
            <TooltipTrigger asChild>
              <IconButton
                aria-label={PIECE_NAME[p]}
                className={cn(p === piece && styles.pieceActive)}
                onClick={() => setPiece(p)}
              >
                {p}
              </IconButton>
            </TooltipTrigger>
            <TooltipContent>{PIECE_NAME[p]}</TooltipContent>
          </Tooltip>
        ))}
      </div>
    }>
      {err && <Alert>{err}</Alert>}
      {loading && !squares && <LoadingText>loading…</LoadingText>}
      {squares && (
        <>
          <div className={styles.grid} role="img" aria-label={`Learned piece-square deviations for ${PIECE_NAME[piece]}`}>
            {Array.from({ length: 8 }, (_, r) => 7 - r).flatMap((rank) =>
              Array.from({ length: 8 }, (_, file) => {
                const s = at(file, rank);
                const dev = s?.devPoints ?? 0;
                const wit = s?.witness ?? 0;
                const mag = Math.min(1, Math.abs(dev) / maxAbs);
                const hue = dev >= 0 ? 130 : 0;
                const bg = wit > 0 ? `hsl(${hue} 70% 45% / ${(0.12 + 0.6 * mag).toFixed(3)})` : 'transparent';
                const square = `${String.fromCharCode(97 + file)}${rank + 1}`;
                const tip = `${square} · dev ${dev >= 0 ? '+' : ''}${dev.toFixed(1)} · ${wit.toFixed(0)}w`;
                return (
                  <Tooltip key={`${file}-${rank}`}>
                    <TooltipTrigger asChild>
                      <div className={styles.cell} style={{ background: bg }} tabIndex={wit > 0 ? 0 : -1}>
                        {wit > 0 ? Math.round(dev) : ''}
                      </div>
                    </TooltipTrigger>
                    <TooltipContent>{tip}</TooltipContent>
                  </Tooltip>
                );
              }),
            )}
          </div>
          <Muted className={styles.foot}>
            All 64 squares queried from consensus (384 piece-square edges total). PeSTO floor + learned
            deviation · green = better square, red = worse ·{' '}
            {covered}/64 squares with evidence ({totalWitness.toFixed(0)} witnesses) on {PIECE_NAME[piece]}
          </Muted>
        </>
      )}
    </Panel>
  );
}
