import { useCallback, useEffect, useRef, useState } from 'react';
import { apiGet, apiPost } from '../api/client';

interface MoveScore { uci: string; effMu: number; rated: boolean; }
interface ApplyResult { fen: string; terminal: boolean; status: string; legal: boolean; }
interface BestMove { uci: string | null; fen: string; effMu: number; rated: boolean; terminal: boolean; status: string; }
interface TrainStatus {
  running: boolean; games: number; white: number; black: number; draws: number;
  adjudicated: number; lastOutcome: string; temperature: number; weight: number; maxPlies: number;
}

const START = 'rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1';

const GLYPH: Record<string, string> = {
  K: '♔', Q: '♕', R: '♖', B: '♗', N: '♘', P: '♙',
  k: '♚', q: '♛', r: '♜', b: '♝', n: '♞', p: '♟',
};

// FEN placement -> 8x8 grid, rank 8 (top) down to rank 1; '' = empty.
function parseBoard(fen: string): string[][] {
  const rows = fen.split(' ')[0].split('/');
  return rows.map((row) => {
    const out: string[] = [];
    for (const c of row) {
      if (c >= '1' && c <= '8') for (let i = 0; i < Number(c); i++) out.push('');
      else out.push(c);
    }
    return out;
  });
}

const FILES = 'abcdefgh';
const sqName = (file: number, rank8: number) => `${FILES[file]}${8 - rank8}`;
const whiteToMove = (fen: string) => fen.split(' ')[1] !== 'b';
const msg = (e: unknown) => (e instanceof Error ? e.message : String(e));
// eff_mu as a signed advantage vs draw (1500), for the on-square metric badge.
const fmtDelta = (mu?: number) => { const d = (mu ?? 1500) - 1500; return `${d >= 0 ? '+' : ''}${Math.round(d)}`; };

// Centre of a UCI square (e.g. "e4") in board-percent coords (0..100), white at the bottom.
function sqCenter(sq: string): { x: number; y: number } {
  const f = FILES.indexOf(sq[0]);
  const rank = Number(sq[1]);
  return { x: (f + 0.5) * 12.5, y: (8 - rank + 0.5) * 12.5 };
}

// f,r grid indices of a UCI square (r = row from top, 0 = rank 8).
function sqRC(sq: string): { f: number; r: number } {
  return { f: FILES.indexOf(sq[0]), r: 8 - Number(sq[1]) };
}

// Right-angle (L) polyline points: longer axis first, then a 90° turn. Straight rank/file
// moves collapse to one segment. Used for the user's contemplation arrows.
function elbow(from: string, to: string): string {
  const a = sqCenter(from), b = sqCenter(to);
  const corner = Math.abs(b.x - a.x) >= Math.abs(b.y - a.y) ? { x: b.x, y: a.y } : { x: a.x, y: b.y };
  return `${a.x},${a.y} ${corner.x},${corner.y} ${b.x},${b.y}`;
}

export function ChessView() {
  const [fen, setFen] = useState(START);
  const [status, setStatus] = useState('ongoing');
  const [legal, setLegal] = useState<MoveScore[]>([]);
  const [sel, setSel] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [autoReply, setAutoReply] = useState(true);
  const [train, setTrain] = useState<TrainStatus | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [knobs, setKnobs] = useState({ games: 200, temp: 120, maxPlies: 200, weight: 0.5 });

  // Annotations + drag.
  const [marks, setMarks] = useState<Set<string>>(new Set());          // right-click square highlights
  const [userArrows, setUserArrows] = useState<{ from: string; to: string }[]>([]);
  const [drag, setDrag] = useState<{ from: string; piece: string; x: number; y: number } | null>(null);
  const rdrag = useRef<string | null>(null);
  const boardRef = useRef<HTMLDivElement>(null);

  const refreshLegal = useCallback(async (f: string) => {
    try { setLegal(await apiPost<MoveScore[]>('/chess/legal', { fen: f })); setErr(null); }
    catch (e) { setLegal([]); setErr(`scoring failed: ${msg(e)}`); }
  }, []);

  useEffect(() => { void refreshLegal(fen); }, [fen, refreshLegal]);

  // Poll training status.
  useEffect(() => {
    let live = true;
    const tick = async () => {
      try { const s = await apiGet<TrainStatus>('/chess/train/status'); if (live) setTrain(s); } catch { /* idle */ }
    };
    void tick();
    const h = setInterval(tick, 1500);
    return () => { live = false; clearInterval(h); };
  }, []);

  // Cancel a dangling drag/right-drag if the pointer is released off the board.
  useEffect(() => {
    const up = () => { setDrag(null); rdrag.current = null; };
    window.addEventListener('pointerup', up);
    return () => window.removeEventListener('pointerup', up);
  }, []);

  const board = parseBoard(fen);
  const pieceAt = (sq: string) => { const { f, r } = sqRC(sq); return board[r]?.[f] ?? ''; };
  const isMine = (sq: string) => {
    const p = pieceAt(sq);
    return !!p && (whiteToMove(fen) ? p === p.toUpperCase() : p === p.toLowerCase());
  };

  // Legal destinations of the selected / picked-up piece, each coloured by the bot's eff_mu for that
  // move (greener = the substrate rates it higher) — the fold's eval painted onto your options as you
  // hold the piece.
  const selMoves = sel ? legal.filter((m) => m.uci.startsWith(sel)) : [];
  const targets = new Set(selMoves.map((m) => m.uci.slice(2, 4)));
  const evLo = Math.min(...selMoves.map((m) => m.effMu), Infinity);
  const evHi = Math.max(...selMoves.map((m) => m.effMu), -Infinity);
  const targetHue = (sq: string) => {
    const m = selMoves.find((x) => x.uci.slice(2, 4) === sq);
    if (!m) return null;
    const t = evHi > evLo ? (m.effMu - evLo) / (evHi - evLo) : 0.5;
    return Math.round(t * 130);   // 0 = red (worst) .. 130 = green (best)
  };
  const targetMu = new Map(selMoves.map((m) => [m.uci.slice(2, 4), m.effMu]));

  // Bot suggestions hue-coded (hue wheel over the top rated moves): the destination square is
  // filled in the hue and the moving (source) piece glyph is tinted the same hue, so you can
  // follow "this coloured piece → its matching-coloured target". Shown only at rest (no piece
  // selected) so the board stays clean while you move.
  const suggMark = new Map<string, { hue: number; role: 'from' | 'to' }>();
  if (!sel && !drag) {
    const sugg = [...legal].sort((a, b) => b.effMu - a.effMu).filter((m) => m.rated).slice(0, 5);
    sugg.forEach((m, i) => {
      const hue = Math.round((i / Math.max(1, sugg.length)) * 320);
      const from = m.uci.slice(0, 2), to = m.uci.slice(2, 4);
      if (!suggMark.has(to)) suggMark.set(to, { hue, role: 'to' });
      if (!suggMark.has(from)) suggMark.set(from, { hue, role: 'from' });
    });
  }

  const botMove = useCallback(async (f: string) => {
    setBusy(true);
    try {
      const r = await apiPost<BestMove>('/chess/bestmove', { fen: f });
      setFen(r.fen); setStatus(r.status); setErr(null);
    } catch (e) { setErr(`bot move failed: ${msg(e)}`); }
    finally { setBusy(false); }
  }, []);

  // Resolve from→to to a legal UCI (prefer queen promotion) and apply it.
  const playFromTo = useCallback(async (from: string, to: string) => {
    if (busy || status !== 'ongoing' || from === to) return;
    const cands = legal.filter((m) => m.uci.slice(0, 4) === from + to);
    const uci = cands.find((m) => m.uci.length === 5 && m.uci[4] === 'q')?.uci ?? cands[0]?.uci;
    if (!uci) return;
    setBusy(true);
    try {
      const r = await apiPost<ApplyResult>('/chess/move', { fen, uci });
      if (!r.legal) { setErr(`illegal move: ${uci}`); return; }
      setFen(r.fen); setStatus(r.status); setErr(null);
      if (!r.terminal && autoReply) await botMove(r.fen);
    } catch (e) { setErr(`move failed: ${msg(e)}`); }
    finally { setBusy(false); }
  }, [busy, status, legal, fen, autoReply, botMove]);

  const onPointerDown = (e: React.PointerEvent, sq: string) => {
    if (e.button === 2) {                                 // right button
      if (sel) { setSel(null); setDrag(null); rdrag.current = null; return; }  // cancel a click-selection
      rdrag.current = sq;                                 // else: start a contemplation gesture
      return;
    }
    if (e.button !== 0) return;
    setMarks(new Set()); setUserArrows([]);               // left click clears annotations
    if (busy || status !== 'ongoing') return;
    if (sel && sel !== sq) { void playFromTo(sel, sq); setSel(null); return; }  // click-click move
    if (isMine(sq)) { setSel(sq); setDrag({ from: sq, piece: pieceAt(sq), x: e.clientX, y: e.clientY }); }
    else setSel(null);
  };

  const onPointerUp = (e: React.PointerEvent, sq: string) => {
    if (e.button === 2) {                                  // right release: highlight or arrow
      const from = rdrag.current; rdrag.current = null;
      if (from === null) return;
      if (from === sq) setMarks((m) => { const n = new Set(m); n.has(sq) ? n.delete(sq) : n.add(sq); return n; });
      else setUserArrows((a) => [...a, { from, to: sq }]);
      return;
    }
    if (e.button !== 0) return;
    if (drag && drag.from !== sq) { void playFromTo(drag.from, sq); setSel(null); }  // drag release = move
    setDrag(null);
  };

  const newGame = async () => {
    const r = await apiGet<{ fen: string }>('/chess/new');
    setFen(r.fen); setStatus('ongoing'); setSel(null); setMarks(new Set()); setUserArrows([]);
  };
  const startTrain = () => {
    const q = new URLSearchParams({
      games: String(knobs.games), temperature: String(knobs.temp),
      maxPlies: String(knobs.maxPlies), weight: String(knobs.weight),
    }).toString();
    apiPost(`/chess/train/start?${q}`, {}).catch(() => {});
  };
  const stopTrain = () => apiPost('/chess/train/stop', {}).catch(() => {});

  const topMoves = [...legal].sort((a, b) => b.effMu - a.effMu).slice(0, 8);
  const stmEval = topMoves[0]?.effMu ?? 1500;
  const whiteEval = whiteToMove(fen) ? stmEval - 1500 : 1500 - stmEval;
  const evalFrac = 1 / (1 + Math.exp(-whiteEval / 200));

  // Human-readable eval read-out for the bar: rough pawns (eff_mu/100) + white win-chance.
  const evalPawns = whiteEval / 100;
  const winPct = Math.round(evalFrac * 100);
  const leadTxt = Math.abs(whiteEval) < 12 ? 'Even'
    : whiteEval > 0 ? `White +${evalPawns.toFixed(1)}` : `Black +${(-evalPawns).toFixed(1)}`;

  // The bot's single recommended move (highest eff_mu): starred on the board + in the list so a
  // beginner instantly sees the pick. Shown at rest, or when the recommended piece is in hand.
  const botBest = topMoves[0];
  const botBestFrom = botBest?.uci.slice(0, 2);
  const botBestTo = botBest?.uci.slice(2, 4);
  const showPick = !!botBestTo && ((!sel && !drag) || sel === botBestFrom);

  // Green→red goodness scale for the analysis list (greener = the bot rates the move higher).
  const muLo = Math.min(...topMoves.map((m) => m.effMu), Infinity);
  const muHi = Math.max(...topMoves.map((m) => m.effMu), -Infinity);
  const goodHue = (mu: number) => Math.round((muHi > muLo ? (mu - muLo) / (muHi - muLo) : 0.5) * 130);

  // Pixel size of one square (for the floating drag ghost, which lives outside the board container).
  const sqPx = (boardRef.current?.clientWidth ?? 480) / 8;

  // Prominent game status: whose move during play, and clearly WHAT HAPPENED at the end.
  const sideToMove = whiteToMove(fen) ? 'White' : 'Black';
  const over = status !== 'ongoing';
  const statusText = busy
    ? 'thinking…'
    : over
      ? (status === 'draw' ? 'Draw'
        : status === 'white wins' ? 'Checkmate — White wins'
        : status === 'black wins' ? 'Checkmate — Black wins'
        : status)
      : `${sideToMove} to move`;

  return (
    <div className="chess">
      <div className="chess-main">
        <div className="chess-controls">
          <div
            className={`status${busy ? ' thinking' : ''}${over ? (status === 'draw' ? ' over draw' : ' over win') : ''}`}
            role="status"
          >{statusText}</div>
          {err && <div className="chess-error" role="alert">{err}</div>}
          <div className="ctl-row">
            <button onClick={newGame}>New game</button>
            <button onClick={() => botMove(fen)} disabled={busy || over}>Bot move</button>
            <label><input type="checkbox" checked={autoReply} onChange={(e) => setAutoReply(e.target.checked)} /> bot auto-replies</label>
          </div>
          <span className="hint">drag/click to move · right-drag = arrow · right-click = mark · left-click clears</span>
          <code className="fen">{fen}</code>
        </div>
        <div className="board-wrap">
          <div className="board-area">
          <div className="eval-col">
            <div className="eval-bar" title={`eval (white): ${whiteEval >= 0 ? '+' : ''}${whiteEval.toFixed(0)}`}>
              <div className="eval-white" style={{ height: `${evalFrac * 100}%` }} />
            </div>
            <div className="eval-readout"><b>{leadTxt}</b><span>{winPct}% white</span></div>
          </div>
          <div
            className="board" role="grid" ref={boardRef}
            onContextMenu={(e) => e.preventDefault()}
            onPointerMove={(e) => { if (drag) setDrag((d) => (d ? { ...d, x: e.clientX, y: e.clientY } : d)); }}
          >
            {board.map((row, r) =>
              row.map((piece, f) => {
                const sq = sqName(f, r);
                const dark = (f + r) % 2 === 1;
                const sm = suggMark.get(sq);
                // A suggestion 'to' colours the destination square; a 'from' tints the moving piece
                // glyph (same hue) so you can visually follow piece → matching destination.
                const suggTo = sm?.role === 'to' ? sm : null;
                const suggFrom = sm?.role === 'from' ? sm : null;
                const cls = ['square', dark ? 'dark' : 'light',
                  sel === sq ? 'sel' : '', targets.has(sq) ? 'target' : '',
                  marks.has(sq) ? 'marked' : '',
                  suggTo ? 'sugg sugg-to' : '',
                  showPick && sq === botBestTo ? 'pick' : '',
                  drag?.from === sq ? 'dragging' : ''].join(' ');
                const th = targets.has(sq) ? targetHue(sq) : null;
                const style = suggTo
                  ? ({ ['--sugg' as string]: `hsl(${suggTo.hue} 90% 50%)` } as React.CSSProperties)
                  : th !== null
                  ? ({ ['--teval' as string]: `hsl(${th} 70% 55%)` } as React.CSSProperties)
                  : undefined;
                const pieceStyle = suggFrom
                  ? ({ color: `hsl(${suggFrom.hue} 85% 45%)` } as React.CSSProperties)
                  : undefined;
                return (
                  <div key={sq} className={cls} style={style}
                       onPointerDown={(e) => onPointerDown(e, sq)}
                       onPointerUp={(e) => onPointerUp(e, sq)}>
                    {piece && <span className={`piece${suggFrom ? ' piece-sugg' : ''}`} style={pieceStyle}>{GLYPH[piece]}</span>}
                    {showPick && sq === botBestTo && <span className="botpick" title="bot's pick">★</span>}
                    {targets.has(sq) && <span className="target-mu">{fmtDelta(targetMu.get(sq))}</span>}
                  </div>
                );
              }),
            )}
            <svg className="board-overlay" viewBox="0 0 100 100" preserveAspectRatio="none">
              <defs>
                <marker id="uarrowhead" markerWidth="4" markerHeight="4" refX="2.4" refY="2" orient="auto">
                  <path d="M0,0 L4,2 L0,4 Z" />
                </marker>
              </defs>
              {userArrows.map((ar, i) => (
                <polyline key={`${ar.from}${ar.to}${i}`} className="uarrow" markerEnd="url(#uarrowhead)"
                          points={elbow(ar.from, ar.to)} />
              ))}
            </svg>
          </div>
          </div>
          <div className="board-legend">
            <span><b className="lg-star">★</b> bot&rsquo;s pick</span>
            <span className="lg-scale"><i /> weaker → stronger move</span>
            <span><b>±</b> = rating points vs an even game (1500 = even)</span>
            <span>eval bar = who&rsquo;s ahead</span>
            <span>right-drag = arrow · right-click = mark</span>
          </div>
        </div>
      </div>

      <div className="chess-side">
        <section className="panel">
          <h3>Training</h3>
          {train ? (
            <ul className="stats">
              <li>state: <b>{train.running ? 'running' : 'idle'}</b></li>
              <li>games: <b>{train.games}</b></li>
              <li>W / B / D: <b>{train.white} / {train.black} / {train.draws}</b></li>
              <li>adjudicated: {train.adjudicated}</li>
              <li>last: {train.lastOutcome || '—'}</li>
            </ul>
          ) : <div className="muted">no status</div>}
          <div className="knobs">
            <label>games<input type="number" min={0} value={knobs.games}
              onChange={(e) => setKnobs((k) => ({ ...k, games: +e.target.value }))} /></label>
            <label>temp<input type="number" min={0} step={10} value={knobs.temp}
              onChange={(e) => setKnobs((k) => ({ ...k, temp: +e.target.value }))} /></label>
            <label>max plies<input type="number" min={2} value={knobs.maxPlies}
              onChange={(e) => setKnobs((k) => ({ ...k, maxPlies: +e.target.value }))} /></label>
            <label>weight<input type="number" min={0} step={0.1} value={knobs.weight}
              onChange={(e) => setKnobs((k) => ({ ...k, weight: +e.target.value }))} /></label>
          </div>
          <div className="row">
            <button onClick={startTrain} disabled={train?.running}>{knobs.games > 0 ? `Run ${knobs.games}` : 'Run ∞'}</button>
            <button onClick={stopTrain} disabled={!train?.running}>Stop</button>
          </div>
          <p className="muted">games 0 = run until Stop · temp = exploration · weight = per-game evidence · folds online.</p>
        </section>

        <section className="panel">
          <h3>Substrate analysis</h3>
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
      </div>

      {drag && (
        <span className="drag-ghost" style={{
          left: drag.x, top: drag.y, fontSize: `${sqPx * 0.82}px`,
        }}>{GLYPH[drag.piece]}</span>
      )}
    </div>
  );
}
