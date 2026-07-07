import { useCallback, useEffect, useRef, useState } from 'react';
import { Button, Modal } from '@ui';
import { apiGet, apiPost } from '../api/client';
import { Board, parseBoard, whiteToMove, useBoardRef, type MoveScore } from './play/Board';
import { MoveList } from './play/MoveList';
import { EnginePanel, type TrainKnobs, type TrainStatus } from './play/EnginePanel';
import { PstGrid } from './play/PstGrid';
import { GameControls } from './play/GameControls';
import { Sidebar } from './play/Sidebar';
import styles from './ChessView.module.css';

interface ApplyResult { fen: string; terminal: boolean; status: string; legal: boolean; motifs?: string[]; }
interface BestMove {
  uci: string | null; fen: string; effMu: number; rated: boolean; terminal: boolean; status: string;
  scoreCp: number; depth: number; nodes: number; pv?: string[]; motifs?: string[];
}
export interface SearchInfo { scoreCp: number; depth: number; nodes: number; substrate: boolean; pv: string[]; }

const START = 'rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1';

const FILES = 'abcdefgh';
const sqRC = (sq: string) => ({ f: FILES.indexOf(sq[0]), r: 8 - Number(sq[1]) });
const msg = (e: unknown) => (e instanceof Error ? e.message : String(e));

export function ChessView() {
  const [fen, setFen] = useState(START);
  const [status, setStatus] = useState('ongoing');
  const [legal, setLegal] = useState<MoveScore[]>([]);
  const [sel, setSel] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [autoReply, setAutoReply] = useState(true);
  const [train, setTrain] = useState<TrainStatus | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [knobs, setKnobs] = useState<TrainKnobs>({ games: 200, temp: 120, maxPlies: 200, weight: 0.5 });
  const [searchDepth, setSearchDepth] = useState(4);
  const [useSubstrate, setUseSubstrate] = useState(true);
  const [evalMode, setEvalMode] = useState(false);
  const [flip, setFlip] = useState(false);
  const [history, setHistory] = useState<string[]>([]);
  const [lastMove, setLastMove] = useState<{ from: string; to: string } | null>(null);
  const [promo, setPromo] = useState<{ from: string; to: string } | null>(null);
  const [search, setSearch] = useState<SearchInfo | null>(null);
  const [motifs, setMotifs] = useState<string[]>([]);

  const [marks, setMarks] = useState<Set<string>>(new Set());
  const [userArrows, setUserArrows] = useState<{ from: string; to: string }[]>([]);
  const [drag, setDrag] = useState<{ from: string; piece: string; x: number; y: number } | null>(null);
  const rdrag = useRef<string | null>(null);
  const boardRef = useBoardRef();

  const refreshLegal = useCallback(async (f: string) => {
    try { setLegal(await apiPost<MoveScore[]>('/chess/legal', { fen: f })); setErr(null); }
    catch (e) { setLegal([]); setErr(`scoring failed: ${msg(e)}`); }
  }, []);

  useEffect(() => { void refreshLegal(fen); }, [fen, refreshLegal]);

  const fetchTrain = useCallback(async () => {
    try { setTrain(await apiGet<TrainStatus>('/chess/train/status')); } catch { }
  }, []);
  // One status read on mount, then poll ONLY while a training run is active — idle costs nothing.
  useEffect(() => { void fetchTrain(); }, [fetchTrain]);
  useEffect(() => {
    if (!train?.running) return;
    const h = setInterval(fetchTrain, 1500);
    return () => clearInterval(h);
  }, [train?.running, fetchTrain]);

  useEffect(() => {
    // Track movement on window, not just the board, so the drag ghost follows the cursor
    // everywhere instead of freezing at the board edge when dragging toward the side panel.
    const move = (e: PointerEvent) => setDrag((d) => (d ? { ...d, x: e.clientX, y: e.clientY } : d));
    const up = () => { setDrag(null); rdrag.current = null; };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up);
    return () => { window.removeEventListener('pointermove', move); window.removeEventListener('pointerup', up); };
  }, []);

  const board = parseBoard(fen);
  const pieceAt = (sq: string) => { const { f, r } = sqRC(sq); return board[r]?.[f] ?? ''; };
  const isMine = (sq: string) => {
    const p = pieceAt(sq);
    return !!p && (whiteToMove(fen) ? p === p.toUpperCase() : p === p.toLowerCase());
  };

  const botMove = useCallback(async (f: string) => {
    setBusy(true);
    try {
      const r = await apiPost<BestMove>('/chess/bestmove', { fen: f, depth: searchDepth, substrate: useSubstrate });
      if (r.uci) {
        setLastMove({ from: r.uci.slice(0, 2), to: r.uci.slice(2, 4) });
        setHistory((h) => [...h, r.uci!]);
      }
      setSearch({ scoreCp: r.scoreCp, depth: r.depth, nodes: r.nodes, substrate: useSubstrate, pv: r.pv ?? [] });
      setMotifs(r.motifs ?? []);
      setFen(r.fen); setStatus(r.status); setErr(null);
    } catch (e) { setErr(`bot move failed: ${msg(e)}`); }
    finally { setBusy(false); }
  }, [searchDepth, useSubstrate]);

  const applyUci = useCallback(async (uci: string) => {
    setBusy(true);
    try {
      const r = await apiPost<ApplyResult>('/chess/move', { fen, uci });
      if (!r.legal) { setErr(`illegal move: ${uci}`); return; }
      setLastMove({ from: uci.slice(0, 2), to: uci.slice(2, 4) });
      setHistory((h) => [...h, uci]);
      setMotifs(r.motifs ?? []);
      setFen(r.fen); setStatus(r.status); setErr(null);
      if (!r.terminal && autoReply) await botMove(r.fen);
    } catch (e) { setErr(`move failed: ${msg(e)}`); }
    finally { setBusy(false); }
  }, [fen, autoReply, botMove]);

  const playFromTo = useCallback(async (from: string, to: string) => {
    if (busy || status !== 'ongoing' || from === to) return;
    const cands = legal.filter((m) => m.uci.slice(0, 4) === from + to);
    if (cands.length > 1 && cands.some((m) => m.uci.length === 5)) {
      setPromo({ from, to });
      return;
    }
    const uci = cands[0]?.uci;
    if (!uci) return;
    await applyUci(uci);
  }, [busy, status, legal, applyUci]);

  const onPointerDown = (e: React.PointerEvent, sq: string) => {
    if (e.button === 2) {
      if (sel) { setSel(null); setDrag(null); rdrag.current = null; return; }
      rdrag.current = sq;
      return;
    }
    if (e.button !== 0) return;
    setMarks(new Set()); setUserArrows([]);
    if (busy || status !== 'ongoing') return;
    if (sel && sel !== sq) { void playFromTo(sel, sq); setSel(null); return; }
    if (isMine(sq)) { setSel(sq); setDrag({ from: sq, piece: pieceAt(sq), x: e.clientX, y: e.clientY }); }
    else setSel(null);
  };

  const onPointerUp = (e: React.PointerEvent, sq: string) => {
    if (e.button === 2) {
      const from = rdrag.current; rdrag.current = null;
      if (from === null) return;
      if (from === sq) setMarks((m) => { const n = new Set(m); n.has(sq) ? n.delete(sq) : n.add(sq); return n; });
      else setUserArrows((a) => [...a, { from, to: sq }]);
      return;
    }
    if (e.button !== 0) return;
    if (drag && drag.from !== sq) { void playFromTo(drag.from, sq); setSel(null); }
    setDrag(null);
  };

  const newGame = async () => {
    const r = await apiGet<{ fen: string }>('/chess/new');
    setFen(r.fen); setStatus('ongoing'); setSel(null); setMarks(new Set()); setUserArrows([]);
    setHistory([]); setLastMove(null); setSearch(null); setMotifs([]);
  };
  const startTrain = () => {
    const q = new URLSearchParams({
      games: String(knobs.games), temperature: String(knobs.temp),
      maxPlies: String(knobs.maxPlies), weight: String(knobs.weight),
    }).toString();
    apiPost(`/chess/train/start?${q}`, {}).then(fetchTrain).catch(() => {});
  };
  const stopTrain = () => apiPost('/chess/train/stop', {}).then(fetchTrain).catch(() => {});

  const topMoves = [...legal].sort((a, b) => b.effMu - a.effMu).slice(0, 8);
  const stmEval = topMoves[0]?.effMu ?? 1500;
  const whiteEval = whiteToMove(fen) ? stmEval - 1500 : 1500 - stmEval;
  const evalFrac = 1 / (1 + Math.exp(-whiteEval / 200));
  const winPct = Math.round(evalFrac * 100);
  // whiteEval is a Glicko rating delta (not centipawns) — label it as rating points so it
  // doesn't read as a second, conflicting pawn eval next to the search readout.
  const leadTxt = Math.abs(whiteEval) < 12 ? 'Even'
    : whiteEval > 0 ? `White +${Math.round(whiteEval)}` : `Black +${Math.round(-whiteEval)}`;

  const botBest = topMoves[0];
  const botBestFrom = botBest?.uci.slice(0, 2);
  const botBestTo = botBest?.uci.slice(2, 4);
  const showPick = !!botBestTo && ((!sel && !drag) || sel === botBestFrom);

  const sideToMove = whiteToMove(fen) ? 'White' : 'Black';
  const over = status !== 'ongoing';
  // Which king got mated (loser's king). Draw/ongoing -> no red king.
  const matedKing = status === 'white wins' ? 'k' : status === 'black wins' ? 'K' : null;
  const statusText = busy
    ? 'thinking…'
    : over
      ? (status === 'draw' ? 'Draw'
        : status === 'white wins' ? 'Checkmate — White wins'
        : status === 'black wins' ? 'Checkmate — Black wins'
        : status)
      : `${sideToMove} to move`;

  return (
    <div className={styles.layout}>
      <div className={styles.main}>
        <Board
          fen={fen}
          legal={legal}
          sel={sel}
          drag={drag}
          marks={marks}
          userArrows={userArrows}
          showPick={showPick && !evalMode}
          botBestTo={botBestTo}
          whiteEval={whiteEval}
          evalFrac={evalFrac}
          leadTxt={leadTxt}
          winPct={winPct}
          boardRef={boardRef}
          flip={flip}
          lastMove={lastMove}
          matedKing={matedKing}
          onPointerDown={onPointerDown}
          onPointerUp={onPointerUp}
          onDragMove={(x, y) => setDrag((d) => (d ? { ...d, x, y } : d))}
        />
        {promo && (
          <Modal
            open
            title="Promote pawn"
            onClose={() => setPromo(null)}
            actions={<Button variant="ghost" onClick={() => setPromo(null)}>Cancel</Button>}
          >
            <div style={{ display: 'flex', gap: '0.5rem', justifyContent: 'center' }}>
              {(['q', 'r', 'b', 'n'] as const).map((p) => (
                <Button key={p} onClick={() => { void applyUci(promo.from + promo.to + p); setPromo(null); }}>
                  {p.toUpperCase()}
                </Button>
              ))}
            </div>
          </Modal>
        )}
      </div>

      {/* Modular sidebar: controls first, then the analysis modules. Reorder or add
          custom controls by editing this list — each item is a Panel-based UserControl. */}
      <Sidebar className={styles.side}>
        <GameControls
          statusText={statusText} busy={busy} over={over} status={status}
          motifs={motifs} err={err} fen={fen}
          autoReply={autoReply} flip={flip} searchDepth={searchDepth}
          useSubstrate={useSubstrate} evalMode={evalMode}
          onNewGame={() => void newGame()} onBotMove={() => void botMove(fen)}
          onAutoReply={setAutoReply} onFlip={setFlip} onDepth={setSearchDepth}
          onSubstrate={setUseSubstrate} onEvalMode={setEvalMode}
        />
        <EnginePanel
          train={train}
          knobs={knobs}
          onKnobsChange={setKnobs}
          onStart={startTrain}
          onStop={stopTrain}
        />
        <MoveList topMoves={topMoves} history={history} evalMode={evalMode} search={search} />
        <PstGrid />
      </Sidebar>
    </div>
  );
}
