import type { MoveScore } from './Board';
import type { SearchInfo } from '../ChessView';

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
        <section className="panel search-info">
          <h3>Last search</h3>
          <ul className="stats">
            <li>eval <b>{search.scoreCp >= 0 ? '+' : ''}{(search.scoreCp / 100).toFixed(2)}</b> <span className="muted">pawns, mover&rsquo;s view</span></li>
            <li>depth <b>{search.depth}</b> · nodes <b>{search.nodes.toLocaleString()}</b></li>
            {search.pv.length > 0 && (
              <li>pv <span className="pv">{search.pv.join(' ')}</span></li>
            )}
            <li className="muted">{search.substrate ? 'substrate-biased' : 'pure'} alpha-beta</li>
          </ul>
        </section>
      )}
      {history.length > 0 && (
        <section className="panel">
          <h3>Move list <span className="muted">({history.length} plies)</span></h3>
          <ol className="move-history">
            {Array.from({ length: Math.ceil(history.length / 2) }, (_, i) => (
              <li key={i}>
                <span className="movenum">{i + 1}.</span>
                <span className="ply">{history[i * 2]}</span>
                <span className="ply">{history[i * 2 + 1] ?? ''}</span>
              </li>
            ))}
          </ol>
        </section>
      )}
    <section className="panel">
      <h3>{evalMode ? 'Eval mode — legal move scores' : 'Substrate analysis'}</h3>
      <p className="muted">The bot&rsquo;s rating for each legal move. <b className="lg-star">★</b> = its pick · greener dot = stronger.</p>
      <ul className="moves">
        {topMoves.map((m, i) => (
          <li key={m.uci} className={m.rated ? 'rated' : 'prior'}>
            <span className="dot" style={{ background: `hsl(${goodHue(m.effMu)} 70% 50%)` }} />
            <span className="uci">{m.uci}</span>
            <span className="mu">{m.effMu.toFixed(0)}</span>
            {i === 0 && <span className="pickstar" title="bot's pick">★</span>}
            {!m.rated && <span className="tag">prior</span>}
          </li>
        ))}
      </ul>
    </section>
    </>
  );
}
