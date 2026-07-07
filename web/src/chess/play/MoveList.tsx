import type { MoveScore } from './Board';

import type { SearchInfo } from '../ChessView';

import { cn, Muted, Tooltip, TooltipContent, TooltipTrigger } from '@ui';

import { Panel } from './Panel';

import shared from './playShared.module.css';

import styles from './MoveList.module.css';



export interface MoveListProps {

  topMoves: MoveScore[];

  history?: string[];

  evalMode?: boolean;

  search?: SearchInfo | null;

}



export function MoveList({ topMoves, history = [], evalMode = false, search = null }: MoveListProps) {

  const muLo = Math.min(...topMoves.map((m) => m.effMu), Infinity);

  const muHi = Math.max(...topMoves.map((m) => m.effMu), -Infinity);

  const goodHue = (mu: number) => Math.round((muHi > muLo ? (mu - muLo) / (muHi - muLo) : 0.5) * 130);



  return (

    <>

      {search && (

        <Panel title="Last search">

          <ul className={shared.stats}>

            <li>eval <b>{search.scoreCp >= 0 ? '+' : ''}{(search.scoreCp / 100).toFixed(2)}</b> <Muted>pawns, mover&rsquo;s view</Muted></li>

            <li>depth <b>{search.depth}</b> · nodes <b>{search.nodes.toLocaleString()}</b></li>

            {search.pv.length > 0 && (

              <li>pv <span className={shared.pv}>{search.pv.join(' ')}</span></li>

            )}

            <li><Muted>{search.substrate ? 'substrate-biased' : 'pure'} alpha-beta</Muted></li>

          </ul>

        </Panel>

      )}

      {history.length > 0 && (

        <Panel title={<>Move list <Muted>({history.length} plies)</Muted></>}>

          <ol className={styles.moveHistory}>

            {Array.from({ length: Math.ceil(history.length / 2) }, (_, i) => (

              <li key={i}>

                <span className={styles.movenum}>{i + 1}.</span>

                <span className={styles.ply}>{history[i * 2]}</span>

                <span className={styles.ply}>{history[i * 2 + 1] ?? ''}</span>

              </li>

            ))}

          </ol>

        </Panel>

      )}

      <Panel title={evalMode ? 'Eval mode — legal move scores' : 'Substrate analysis'}>

        <Muted>The bot&rsquo;s rating for each legal move. <b className={shared.lgStar}>★</b> = its pick · greener dot = stronger.</Muted>

        <ul className={styles.moves}>

          {topMoves.map((m, i) => (

            <li key={m.uci} className={cn(!m.rated && styles.prior)}>

              <span className={styles.dot} style={{ background: `hsl(${goodHue(m.effMu)} 70% 50%)` }} />

              <span className={styles.uci}>{m.uci}</span>

              <span className={styles.mu}>{m.effMu.toFixed(0)}</span>

              {i === 0 && (

                <Tooltip>

                  <TooltipTrigger asChild>

                    <span className={styles.pickstar}>★</span>

                  </TooltipTrigger>

                  <TooltipContent>bot&apos;s pick</TooltipContent>

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

