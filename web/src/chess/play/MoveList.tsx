import type { MoveScore } from './Board';
import type { SearchInfo } from '../ChessView';
import { formatSubstrateMoveDelta } from '../evalDisplay';
import { sanLine, toSan } from '../san';
import { cn, Muted, Tooltip, TooltipContent, TooltipTrigger, UnstyledButton } from '@ui';
import { Panel } from './Panel';
import shared from './playShared.module.css';
import styles from './MoveList.module.css';

export interface MoveListProps {
  topMoves: MoveScore[];
  /** Current position, to write moves in standard notation. */
  fen?: string;
  history?: string[];
  viewPly?: number;
  onGoToPly?: (ply: number) => void;
  onPlayMove?: (uci: string) => void;
  evalMode?: boolean;
  search?: SearchInfo | null;
  searchDepth?: number;
  searchPickUci?: string | null;
}

export function MoveList({
  topMoves,
  fen,
  history = [],
  viewPly = 0,
  onGoToPly,
  onPlayMove,
  evalMode = false,
  search = null,
  searchDepth,
  searchPickUci = null,
}: MoveListProps) {
  const muLo = Math.min(...topMoves.map((m) => m.effMu), Infinity);
  const muHi = Math.max(...topMoves.map((m) => m.effMu), -Infinity);
  const san = (uci: string) => (fen ? toSan(fen, uci) : uci);
  const noEvidence = topMoves.length > 0 && topMoves.every((m) => !m.rated);
  const goodHue = (mu: number) => Math.round((muHi > muLo ? (mu - muLo) / (muHi - muLo) : 0.5) * 130);

  return (
    <>
      <Panel title={<>Moves {history.length > 0 && <Muted>({history.length} plies)</Muted>}</>}>
        {history.length === 0 ? (
          <Muted>Drag or click a piece on the board, or click Bot move to start.</Muted>
        ) : (
          <ol className={styles.moveHistory}>
            {Array.from({ length: Math.ceil(history.length / 2) }, (_, i) => (
              <li key={i}>
                <span className={styles.movenum}>{i + 1}.</span>
                <UnstyledButton
                  type="button"
                  className={cn(styles.ply, viewPly === i * 2 + 1 && styles.plyActive)}
                  onClick={() => onGoToPly?.(i * 2 + 1)}
                >
                  {history[i * 2]}
                </UnstyledButton>
                {history[i * 2 + 1] ? (
                  <UnstyledButton
                    type="button"
                    className={cn(styles.ply, viewPly === i * 2 + 2 && styles.plyActive)}
                    onClick={() => onGoToPly?.(i * 2 + 2)}
                  >
                    {history[i * 2 + 1]}
                  </UnstyledButton>
                ) : (
                  <span className={styles.plyEmpty} />
                )}
              </li>
            ))}
          </ol>
        )}
        {search && (
          <div className={styles.searchStrip}>
            <span>
              eval <b>{search.scoreCp >= 0 ? '+' : ''}{(search.scoreCp / 100).toFixed(2)}</b>
              {' · '}depth <b>{search.depth}</b>
              {searchDepth !== undefined && search.depth < searchDepth && <Muted>/{searchDepth}</Muted>}
              {' · '}<b>{search.nodes.toLocaleString()}</b> nodes
            </span>
            {search.pv.length > 0 && (
              <span className={shared.pv}>{(fen ? sanLine(fen, search.pv) : search.pv).join(' ')}</span>
            )}
          </div>
        )}
      </Panel>

      {topMoves.length > 0 && (
        <Panel title={evalMode ? 'Eval mode — candidates' : 'Candidate moves'}>
          {noEvidence ? (
            <Muted role="status">
              No recorded game reached this position yet; moves sit at the 1500 prior.
            </Muted>
          ) : (
            <Muted>
              Recorded standing delta per move.{searchPickUci ? <> <b className={shared.lgStar}>★</b> search pick.</> : null}
            </Muted>
          )}
          <ul className={styles.moves}>
            {topMoves.map((m) => (
              <li key={m.uci} className={cn(!m.rated && styles.prior)}>
                <UnstyledButton
                  type="button"
                  disabled={!onPlayMove}
                  onClick={onPlayMove ? () => onPlayMove(m.uci) : undefined}
                  className={cn(styles.moveRowBtn, onPlayMove && styles.moveRowInteractive)}
                >
                  <span className={styles.dot} style={{ background: `hsl(${goodHue(m.effMu)} 70% 50%)` }} />
                  <span className={styles.uci}>{san(m.uci)}</span>
                  <span className={styles.mu}>{formatSubstrateMoveDelta(m.effMu)}</span>
                  {searchPickUci === m.uci && (
                    <Tooltip>
                      <TooltipTrigger asChild>
                        <span className={styles.pickstar}>★</span>
                      </TooltipTrigger>
                      <TooltipContent>search pick (depth {searchDepth ?? search?.depth ?? '?'})</TooltipContent>
                    </Tooltip>
                  )}
                  {!m.rated && !noEvidence && <span className={styles.tag}>prior</span>}
                </UnstyledButton>
              </li>
            ))}
          </ul>
        </Panel>
      )}
    </>
  );
}

