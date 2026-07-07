import type { MoveScore } from './Board';
import type { SearchInfo } from '../ChessView';
import { formatSubstrateMoveDelta } from '../evalDisplay';
import { cn, Muted, Tooltip, TooltipContent, TooltipTrigger } from '@ui';
import { Panel } from './Panel';
import shared from './playShared.module.css';
import styles from './MoveList.module.css';

export interface MoveListProps {
  topMoves: MoveScore[];
  history?: string[];
  viewPly?: number;
  onGoToPly?: (ply: number) => void;
  evalMode?: boolean;
  search?: SearchInfo | null;
  searchDepth?: number;
  searchPickUci?: string | null;
}

export function MoveList({
  topMoves,
  history = [],
  viewPly = 0,
  onGoToPly,
  evalMode = false,
  search = null,
  searchDepth,
  searchPickUci = null,
}: MoveListProps) {
  const muLo = Math.min(...topMoves.map((m) => m.effMu), Infinity);
  const muHi = Math.max(...topMoves.map((m) => m.effMu), -Infinity);
  const goodHue = (mu: number) => Math.round((muHi > muLo ? (mu - muLo) / (muHi - muLo) : 0.5) * 130);

  return (
    <>
      {search && (
        <Panel title="Last search">
          <ul className={shared.stats}>
            <li>eval <b>{search.scoreCp >= 0 ? '+' : ''}{(search.scoreCp / 100).toFixed(2)}</b> <Muted>pawns, white&rsquo;s view</Muted></li>
            <li>
              depth <b>{search.depth}</b>
              {searchDepth !== undefined && search.depth < searchDepth && (
                <Muted> (requested {searchDepth})</Muted>
              )}
              {' · '}nodes <b>{search.nodes.toLocaleString()}</b>
            </li>
            {search.pv.length > 0 && (
              <li>pv <span className={shared.pv}>{search.pv.join(' ')}</span></li>
            )}
            <li><Muted>{search.substrate ? 'substrate-biased' : 'pure PeSTO'} alpha-beta</Muted></li>
          </ul>
        </Panel>
      )}

      {history.length > 0 && (
        <Panel title={<>Move list <Muted>({history.length} plies)</Muted></>}>
          <ol className={styles.moveHistory}>
            {Array.from({ length: Math.ceil(history.length / 2) }, (_, i) => (
              <li key={i}>
                <span className={styles.movenum}>{i + 1}.</span>
                <button
                  type="button"
                  className={cn(styles.ply, viewPly === i * 2 + 1 && styles.plyActive)}
                  onClick={() => onGoToPly?.(i * 2 + 1)}
                >
                  {history[i * 2]}
                </button>
                {history[i * 2 + 1] ? (
                  <button
                    type="button"
                    className={cn(styles.ply, viewPly === i * 2 + 2 && styles.plyActive)}
                    onClick={() => onGoToPly?.(i * 2 + 2)}
                  >
                    {history[i * 2 + 1]}
                  </button>
                ) : (
                  <span className={styles.ply} />
                )}
              </li>
            ))}
          </ol>
          <Muted>Click a move, or use ◀ ▶ · ← → keys</Muted>
        </Panel>
      )}

      <Panel title={evalMode ? 'Eval mode — legal move scores' : 'Substrate move scores'}>
        <Muted>
          Glicko consensus for each legal move (1-ply, not search depth).
          {searchPickUci ? (
            <> <b className={shared.lgStar}>★</b> = alpha-beta search pick.</>
          ) : (
            <> Greener dot = stronger substrate score.</>
          )}
        </Muted>
        <ul className={styles.moves}>
          {topMoves.map((m) => (
            <li key={m.uci} className={cn(!m.rated && styles.prior)}>
              <span className={styles.dot} style={{ background: `hsl(${goodHue(m.effMu)} 70% 50%)` }} />
              <span className={styles.uci}>{m.uci}</span>
              <span className={styles.mu}>{formatSubstrateMoveDelta(m.effMu)}</span>
              {searchPickUci === m.uci && (
                <Tooltip>
                  <TooltipTrigger asChild>
                    <span className={styles.pickstar}>★</span>
                  </TooltipTrigger>
                  <TooltipContent>search pick (depth {searchDepth ?? search?.depth ?? '?'})</TooltipContent>
                </Tooltip>
              )}
              {!m.rated && <span className={styles.tag}>prior</span>}
            </li>
          ))}
        </ul>
      </Panel>
    </>
  );
}

