import type { MoveScore } from './Board';

export interface MoveListProps {
  topMoves: MoveScore[];
  history?: string[];
  evalMode?: boolean;
}

export function MoveList({ topMoves, history = [], evalMode = false }: MoveListProps) {
  const muLo = Math.min(...topMoves.map((m) => m.effMu), Infinity);
  const muHi = Math.max(...topMoves.map((m) => m.effMu), -Infinity);
  const goodHue = (mu: number) => Math.round((muHi > muLo ? (mu - muLo) / (muHi - muLo) : 0.5) * 130);

  return (
    <>
      {history.length > 0 && (
        <section className="panel">
          <h3>Move list</h3>
          <p className="move-history">{history.join(' ')}</p>
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
