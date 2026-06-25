import { useCallback, useEffect, useState } from 'react';
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

const sqName = (file: number, rank8: number) => `${'abcdefgh'[file]}${8 - rank8}`;
const whiteToMove = (fen: string) => fen.split(' ')[1] !== 'b';

export function ChessView() {
  const [fen, setFen] = useState(START);
  const [status, setStatus] = useState('ongoing');
  const [legal, setLegal] = useState<MoveScore[]>([]);
  const [sel, setSel] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [autoReply, setAutoReply] = useState(true);
  const [train, setTrain] = useState<TrainStatus | null>(null);

  const refreshLegal = useCallback(async (f: string) => {
    try { setLegal(await apiPost<MoveScore[]>('/chess/legal', { fen: f })); }
    catch { setLegal([]); }
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

  const board = parseBoard(fen);
  const targets = new Set(sel ? legal.filter((m) => m.uci.startsWith(sel)).map((m) => m.uci.slice(2, 4)) : []);

  const botMove = useCallback(async (f: string) => {
    setBusy(true);
    try {
      const r = await apiPost<BestMove>('/chess/bestmove', { fen: f });
      setFen(r.fen); setStatus(r.status);
    } finally { setBusy(false); }
  }, []);

  const onSquare = useCallback(async (file: number, rank8: number) => {
    if (busy || status !== 'ongoing') return;
    const sq = sqName(file, rank8);
    const piece = board[rank8][file];
    if (!sel) {
      const mine = piece && (whiteToMove(fen) ? piece === piece.toUpperCase() : piece === piece.toLowerCase());
      if (mine) setSel(sq);
      return;
    }
    if (sq === sel) { setSel(null); return; }
    // Resolve uci (prefer queen promotion when several promos share from+to).
    const cands = legal.filter((m) => m.uci.slice(0, 4) === sel + sq);
    const uci = cands.find((m) => m.uci.length === 5 && m.uci[4] === 'q')?.uci ?? cands[0]?.uci;
    setSel(null);
    if (!uci) return;
    setBusy(true);
    try {
      const r = await apiPost<ApplyResult>('/chess/move', { fen, uci });
      if (!r.legal) return;
      setFen(r.fen); setStatus(r.status);
      if (!r.terminal && autoReply) await botMove(r.fen);
    } finally { setBusy(false); }
  }, [board, busy, fen, legal, sel, status, autoReply, botMove]);

  const newGame = async () => {
    const r = await apiGet<{ fen: string }>('/chess/new');
    setFen(r.fen); setStatus('ongoing'); setSel(null);
  };
  const startTrain = () => apiPost('/chess/train/start', {}).catch(() => {});
  const stopTrain = () => apiPost('/chess/train/stop', {}).catch(() => {});

  const topMoves = [...legal].sort((a, b) => b.effMu - a.effMu).slice(0, 8);

  return (
    <div className="chess">
      <div className="chess-main">
        <div className="board" role="grid">
          {board.map((row, r) =>
            row.map((piece, f) => {
              const sq = sqName(f, r);
              const dark = (f + r) % 2 === 1;
              const cls = ['square', dark ? 'dark' : 'light',
                sel === sq ? 'sel' : '', targets.has(sq) ? 'target' : ''].join(' ');
              return (
                <div key={sq} className={cls} onClick={() => onSquare(f, r)}>
                  {piece && <span className="piece">{GLYPH[piece]}</span>}
                </div>
              );
            }),
          )}
        </div>
        <div className="chess-controls">
          <div className="status">{busy ? 'thinking…' : status}</div>
          <button onClick={newGame}>New game</button>
          <button onClick={() => botMove(fen)} disabled={busy || status !== 'ongoing'}>Bot move</button>
          <label><input type="checkbox" checked={autoReply} onChange={(e) => setAutoReply(e.target.checked)} /> bot auto-replies</label>
          <code className="fen">{fen}</code>
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
          <div className="row">
            <button onClick={startTrain} disabled={train?.running}>Start</button>
            <button onClick={stopTrain} disabled={!train?.running}>Stop</button>
          </div>
          <p className="muted">Self-play folds each game online; ratings update live.</p>
        </section>

        <section className="panel">
          <h3>Substrate analysis</h3>
          <p className="muted">eff_mu of legal moves in this position (the bot's view).</p>
          <ul className="moves">
            {topMoves.map((m) => (
              <li key={m.uci} className={m.rated ? 'rated' : 'prior'}>
                <span className="uci">{m.uci}</span>
                <span className="mu">{m.effMu.toFixed(1)}</span>
                {!m.rated && <span className="tag">prior</span>}
              </li>
            ))}
          </ul>
        </section>
      </div>
    </div>
  );
}
