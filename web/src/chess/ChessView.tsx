import { useCallback, useEffect, useMemo, useRef, useState } from 'react';

import { Button, Modal } from '@ui';

import { apiGet, apiPost } from '../api/client';

import { Board, parseBoard, whiteToMove, useBoardRef, type MoveScore } from './play/Board';

import { MoveList } from './play/MoveList';

import { MoveNav } from './play/MoveNav';

import { EnginePanel, type TrainKnobs, type TrainStatus } from './play/EnginePanel';

import { PstGrid } from './play/PstGrid';

import { GameControls } from './play/GameControls';

import { Sidebar } from './play/Sidebar';

import { ExplorePanel } from './play/ExplorePanel';

import { formatPositionEval, terminalWhiteCp, whiteCpToBarFraction } from './evalDisplay';

import {

  historyFromPositions,

  initialPositions,

  snapshotFromMove,

  type PositionSnapshot,

} from './gameHistory';

import {
  clearSavedPlayGame,
  loadSavedPlayGame,
  savePlayGame,
  savedGameIsResumable,
} from './playPersist';

import styles from './ChessView.module.css';



interface PlayStart { sessionId: string; fen: string; status?: string; ply?: number; }

interface PlayMoveResult { fen: string; terminal: boolean; status: string; legal: boolean; ply: number; motifs?: string[]; }

interface LegalResponse { moves: MoveScore[]; inCheck: boolean; status: string; }

interface BestMove {

  uci: string | null; fen: string; effMu: number; rated: boolean; terminal: boolean; status: string;

  scoreCp: number; depth: number; nodes: number; pv?: string[]; motifs?: string[];

}

interface PositionEval { whiteScoreCp: number; depth: number; nodes: number; substrate?: boolean; terminal?: boolean; status?: string; }

export interface SearchInfo { scoreCp: number; depth: number; nodes: number; substrate: boolean; pv: string[]; }



const FILES = 'abcdefgh';

const sqRC = (sq: string) => ({ f: FILES.indexOf(sq[0]), r: 8 - Number(sq[1]) });

const msg = (e: unknown) => (e instanceof Error ? e.message : String(e));



export function ChessView() {

  const savedBoot = useMemo(() => {
    const saved = loadSavedPlayGame();
    return savedGameIsResumable(saved) ? saved : null;
  }, []);

  const [positions, setPositions] = useState<PositionSnapshot[]>(
    () => savedBoot?.positions ?? initialPositions(),
  );

  const [viewPly, setViewPly] = useState(() =>
    savedBoot ? savedBoot.positions.length - 1 : 0,
  );

  const [legal, setLegal] = useState<MoveScore[]>([]);

  const [legalFen, setLegalFen] = useState('');

  const [legalLoading, setLegalLoading] = useState(false);

  const [inCheck, setInCheck] = useState(false);

  const [sel, setSel] = useState<string | null>(null);

  const [busy, setBusy] = useState(false);

  const [autoReply, setAutoReply] = useState(() => savedBoot?.autoReply ?? true);

  const [train, setTrain] = useState<TrainStatus | null>(null);

  const [err, setErr] = useState<string | null>(null);

  const [knobs, setKnobs] = useState<TrainKnobs>({ games: 200, temp: 120, maxPlies: 200, weight: 0.5 });

  const [searchDepth, setSearchDepth] = useState(() => savedBoot?.searchDepth ?? 4);

  const [useSubstrate, setUseSubstrate] = useState(() => savedBoot?.useSubstrate ?? true);
  const [recordToSubstrate, setRecordToSubstrate] = useState(() => savedBoot?.recordToSubstrate ?? true);
  const [playSessionId, setPlaySessionId] = useState<string | null>(null);

  const [evalMode, setEvalMode] = useState(() => savedBoot?.evalMode ?? false);

  const [flip, setFlip] = useState(() => savedBoot?.flip ?? false);

  const [promo, setPromo] = useState<{ from: string; to: string } | null>(null);

  const [search, setSearch] = useState<SearchInfo | null>(null);
  const [searchPickUci, setSearchPickUci] = useState<string | null>(null);

  const [positionEval, setPositionEval] = useState<(PositionEval & { fen: string }) | null>(null);

  const [motifs, setMotifs] = useState<string[]>([]);
  const [resumeNote, setResumeNote] = useState<string | null>(
    () => (savedBoot ? `Resumed · ${savedBoot.positions.length - 1} plies in browser` : null),
  );



  const [marks, setMarks] = useState<Set<string>>(new Set());

  const [userArrows, setUserArrows] = useState<{ from: string; to: string }[]>([]);

  const [drag, setDrag] = useState<{ from: string; piece: string; x: number; y: number } | null>(null);

  const rdrag = useRef<string | null>(null);

  const boardRef = useBoardRef();



  const livePly = positions.length - 1;

  const reviewing = viewPly < livePly;

  const snap = positions[viewPly];

  const live = positions[livePly];

  const fen = snap.fen;

  const status = snap.status;

  const lastMove = snap.lastMove;

  const liveFen = live.fen;

  const history = useMemo(() => historyFromPositions(positions), [positions]);



  const goToPly = useCallback((ply: number) => {

    setViewPly(Math.max(0, Math.min(livePly, ply)));

    setSel(null);

    setDrag(null);

    setMarks(new Set());

    setUserArrows([]);

  }, [livePly]);



  const refreshLegal = useCallback(async (f: string, cancelled?: () => boolean) => {

    setLegalLoading(true);

    try {

      const res = await apiPost<LegalResponse | MoveScore[]>('/chess/legal', { fen: f });

      if (cancelled?.()) return;

      if (Array.isArray(res)) {

        setLegal(res);

        setLegalFen(f);

        setInCheck(false);

      } else {

        setLegal(res.moves);

        setLegalFen(f);

        setInCheck(res.inCheck);

      }

      setErr(null);

    } catch (e) {

      if (cancelled?.()) return;

      setLegal([]);

      setLegalFen('');

      setInCheck(false);

      setErr(`scoring failed: ${msg(e)}`);

    } finally {

      if (!cancelled?.()) setLegalLoading(false);

    }

  }, []);



  useEffect(() => {

    let cancelled = false;

    setLegalLoading(true);

    void refreshLegal(fen, () => cancelled);

    return () => { cancelled = true; };

  }, [fen, refreshLegal]);



  useEffect(() => {
    if (status !== 'ongoing') {
      setPositionEval({
        fen,
        whiteScoreCp: terminalWhiteCp(status),
        depth: 0,
        nodes: 0,
        substrate: useSubstrate,
        terminal: true,
        status,
      });
      return;
    }

    let cancelled = false;
    const fenAtRequest = fen;
    const timer = setTimeout(() => {
      void apiPost<PositionEval>('/chess/eval', { fen: fenAtRequest, depth: searchDepth, substrate: useSubstrate })
        .then((r) => {
          if (!cancelled) setPositionEval({ ...r, fen: fenAtRequest });
        })
        .catch(() => {
          /* keep last good eval — never snap the bar to 0 on a transient failure */
        });
    }, 120);

    return () => { cancelled = true; clearTimeout(timer); };
  }, [fen, searchDepth, useSubstrate, status]);



  useEffect(() => {

    const onKey = (e: KeyboardEvent) => {

      const t = e.target;

      if (t instanceof HTMLInputElement || t instanceof HTMLTextAreaElement || t instanceof HTMLSelectElement) return;

      if (e.key === 'ArrowLeft') { e.preventDefault(); goToPly(viewPly - 1); }

      else if (e.key === 'ArrowRight') { e.preventDefault(); goToPly(viewPly + 1); }

      else if (e.key === 'Home') { e.preventDefault(); goToPly(0); }

      else if (e.key === 'End') { e.preventDefault(); goToPly(livePly); }

    };

    window.addEventListener('keydown', onKey);

    return () => window.removeEventListener('keydown', onKey);

  }, [goToPly, viewPly, livePly]);



  const fetchTrain = useCallback(async () => {

    try { setTrain(await apiGet<TrainStatus>('/chess/train/status')); } catch { }

  }, []);

  useEffect(() => { void fetchTrain(); }, [fetchTrain]);

  useEffect(() => {

    if (!train?.running) return;

    const h = setInterval(fetchTrain, 1500);

    return () => clearInterval(h);

  }, [train?.running, fetchTrain]);



  useEffect(() => {

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



  const startPlaySession = useCallback(async (record: boolean, moves?: string[]) => {
    try {
      const r = await apiPost<PlayStart>('/chess/play/start', {
        record,
        moves: moves && moves.length > 0 ? moves : undefined,
      });
      setPlaySessionId(r.sessionId);
      return r;
    } catch (e) {
      setErr(`play session failed: ${msg(e)}`);
      setPlaySessionId(null);
      return null;
    }
  }, []);

  const positionsRef = useRef(positions);
  positionsRef.current = positions;

  // Always keep a server play session (repetition history lives there). Replay local
  // moves when booting or when the record flag changes.
  useEffect(() => {
    const moves = historyFromPositions(positionsRef.current);
    void startPlaySession(recordToSubstrate, moves.length ? moves : undefined).then((r) => {
      if (r && moves.length > 0) {
        setResumeNote((prev) => prev ?? `Server session rebuilt · ${moves.length} plies`);
      }
    });
  }, [recordToSubstrate, startPlaySession]);

  useEffect(() => {
    savePlayGame({
      positions,
      recordToSubstrate,
      searchDepth,
      useSubstrate,
      autoReply,
      flip,
      evalMode,
    });
  }, [positions, recordToSubstrate, searchDepth, useSubstrate, autoReply, flip, evalMode]);

  const ensurePlaySession = useCallback(async () => {
    if (playSessionId) return playSessionId;
    const moves = historyFromPositions(positionsRef.current);
    const r = await startPlaySession(recordToSubstrate, moves.length ? moves : undefined);
    return r?.sessionId ?? null;
  }, [playSessionId, recordToSubstrate, startPlaySession]);

  const appendSnapshot = useCallback((uci: string, nextFen: string, nextStatus: string) => {
    setPositions((p) => {
      const next = [...p, snapshotFromMove(nextFen, nextStatus, uci)];
      setViewPly(next.length - 1);
      return next;
    });
    setResumeNote(null);
  }, []);



  const botMove = useCallback(async (f: string) => {

    setBusy(true);

    try {
      let sid = await ensurePlaySession();
      if (!sid) throw new Error('no play session');

      let r = await apiPost<BestMove>('/chess/play/bestmove', {
        sessionId: sid, fen: f, depth: searchDepth, substrate: useSubstrate,
      });

      if (r.status === 'session expired') {
        const moves = historyFromPositions(positionsRef.current);
        const restarted = await startPlaySession(recordToSubstrate, moves);
        if (!restarted) throw new Error('session expired');
        sid = restarted.sessionId;
        r = await apiPost<BestMove>('/chess/play/bestmove', {
          sessionId: sid, fen: f, depth: searchDepth, substrate: useSubstrate,
        });
      }

      if (r.uci) {
        appendSnapshot(r.uci, r.fen, r.status);
      } else if (r.terminal && r.status !== 'ongoing') {
        setPositions((p) => {
          if (p.length === 0) return p;
          const next = p.slice();
          const last = next[next.length - 1]!;
          next[next.length - 1] = { ...last, status: r.status, fen: r.fen || last.fen };
          return next;
        });
      }

      setSearch({ scoreCp: r.scoreCp, depth: r.depth, nodes: r.nodes, substrate: useSubstrate, pv: r.pv ?? [] });

      setSearchPickUci(r.uci);

      setMotifs(r.motifs ?? []);

      setErr(null);

    } catch (e) { setErr(`bot move failed: ${msg(e)}`); }

    finally { setBusy(false); }

  }, [searchDepth, useSubstrate, appendSnapshot, ensurePlaySession, startPlaySession, recordToSubstrate]);



  const applyUci = useCallback(async (uci: string) => {

    if (reviewing) {

      setErr('Go to the latest move to play');

      return;

    }

    setBusy(true);

    try {
      let sid = await ensurePlaySession();
      if (!sid) throw new Error('no play session');

      let r = await apiPost<PlayMoveResult>('/chess/play/move', { sessionId: sid, fen: liveFen, uci });

      if (r.status === 'session expired') {
        const moves = historyFromPositions(positionsRef.current);
        const restarted = await startPlaySession(recordToSubstrate, moves);
        if (!restarted) throw new Error('session expired');
        sid = restarted.sessionId;
        r = await apiPost<PlayMoveResult>('/chess/play/move', { sessionId: sid, fen: liveFen, uci });
      }

      if (!r.legal) { setErr(`illegal move: ${uci}`); return; }

      appendSnapshot(uci, r.fen, r.status);

      setSearchPickUci(null);

      setMotifs(r.motifs ?? []);

      setErr(null);

      if (!r.terminal && autoReply) await botMove(r.fen);

    } catch (e) { setErr(`move failed: ${msg(e)}`); }

    finally { setBusy(false); }

  }, [liveFen, autoReply, botMove, appendSnapshot, reviewing, ensurePlaySession, startPlaySession, recordToSubstrate]);



  const playFromTo = useCallback(async (from: string, to: string) => {

    if (reviewing) { setErr('Go to the latest move to play'); return; }

    if (busy || status !== 'ongoing' || from === to) return;

    const moves = legalFen === fen ? legal : [];

    const cands = moves.filter((m) => m.uci.slice(0, 4) === from + to);

    if (cands.length > 1 && cands.some((m) => m.uci.length === 5)) {

      setPromo({ from, to });

      return;

    }

    let uci = cands[0]?.uci;

    if (!uci) {

      if (legalLoading || legalFen !== fen) {

        setErr('Legal moves still loading — try again');

        return;

      }

      uci = from + to;

    }

    await applyUci(uci);

  }, [reviewing, busy, status, legal, legalFen, fen, legalLoading, applyUci]);



  const onPointerDown = (e: React.PointerEvent, sq: string) => {

    if (e.button === 2) {

      if (sel) { setSel(null); setDrag(null); rdrag.current = null; return; }

      rdrag.current = sq;

      return;

    }

    if (e.button !== 0) return;

    setMarks(new Set()); setUserArrows([]);

    if (busy || reviewing || status !== 'ongoing') return;

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
    if (playSessionId && status !== 'ongoing') {
      try {
        await apiPost('/chess/play/finish', { sessionId: playSessionId, status });
      } catch { /* best effort */ }
    }

    clearSavedPlayGame();
    setResumeNote(null);

    const r = await apiGet<{ fen: string }>('/chess/new');

    setPositions([{ fen: r.fen, status: 'ongoing', lastMove: null }]);

    setViewPly(0);

    setSel(null); setMarks(new Set()); setUserArrows([]);

    setSearch(null); setSearchPickUci(null); setMotifs([]); setPositionEval(null); setErr(null);

    await startPlaySession(recordToSubstrate);
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

  const hintUci = searchPickUci ?? search?.pv?.[0] ?? null;

  const hintMove = hintUci && legal.some((m) => m.uci === hintUci) ? hintUci : null;

  const botBestFrom = hintMove?.slice(0, 2);

  const botBestTo = hintMove?.slice(2, 4);

  const showPick = !reviewing && !!botBestTo && ((!sel && !drag) || sel === botBestFrom);

  // Prefer eval bound to the visible fen. While a fresh /chess/eval is in flight, keep the
  // last score so the bar doesn't flash 0.0 / 50% on every ply.
  const evalForFen = positionEval?.fen === fen ? positionEval : null;
  const whiteCp = evalForFen?.whiteScoreCp ?? positionEval?.whiteScoreCp;
  const evalPending = positionEval == null || positionEval.fen !== fen;
  const { lead: evalLead, detail: evalDetail } = formatPositionEval(whiteCp ?? 0);
  const evalFrac = whiteCpToBarFraction(whiteCp ?? 0);

  const sideToMove = whiteToMove(fen) ? 'White' : 'Black';

  const over = status !== 'ongoing';

  const kingPiece = whiteToMove(fen) ? 'K' : 'k';

  const matedKing = status === 'white wins' ? 'k' : status === 'black wins' ? 'K' : null;

  const checkedKing = !over && inCheck ? kingPiece : null;

  const statusText = busy

    ? 'thinking…'

    : reviewing

      ? `Reviewing · ${viewPly === 0 ? 'start' : `after move ${viewPly}`}`

      : over

        ? (status === 'draw' ? 'Draw'

          : status === 'white wins' ? 'Checkmate — White wins'

          : status === 'black wins' ? 'Checkmate — Black wins'

          : status)

        : resumeNote

          ? `${resumeNote} · ${inCheck ? `${sideToMove} in check` : `${sideToMove} to move`}`

          : inCheck

            ? `${sideToMove} in check`

            : `${sideToMove} to move`;



  return (

    <div className={styles.layout}>

      <div className={styles.main}>

        <Board

          fen={fen}

          legal={reviewing ? [] : legal}

          sel={sel}

          drag={drag}

          marks={marks}

          userArrows={userArrows}

          showPick={showPick && !evalMode}

          botBestTo={botBestTo}

          whiteEval={evalPending ? '…' : evalLead}

          evalFrac={evalFrac}

          evalDetail={evalPending ? 'Evaluating…' : evalDetail}

          evalPending={evalPending}

          boardRef={boardRef}

          flip={flip}

          lastMove={lastMove}

          matedKing={matedKing}

          checkedKing={checkedKing}

          readOnly={reviewing}

          onPointerDown={onPointerDown}

          onPointerUp={onPointerUp}

          onDragMove={(x, y) => setDrag((d) => (d ? { ...d, x, y } : d))}

          footer={(
            <MoveNav
              viewPly={viewPly}
              livePly={livePly}
              reviewing={reviewing}
              onFirst={() => goToPly(0)}
              onPrev={() => goToPly(viewPly - 1)}
              onNext={() => goToPly(viewPly + 1)}
              onLast={() => goToPly(livePly)}
            />
          )}

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



      <Sidebar className={styles.side}>

        <GameControls

          statusText={statusText} busy={busy} over={over && !reviewing} status={status}

          motifs={reviewing ? [] : motifs} err={err} fen={fen}

          autoReply={autoReply} flip={flip} searchDepth={searchDepth}

          useSubstrate={useSubstrate} recordToSubstrate={recordToSubstrate} evalMode={evalMode}

          onNewGame={() => void newGame()} onBotMove={() => void botMove(liveFen)}

          onAutoReply={setAutoReply} onFlip={setFlip} onDepth={setSearchDepth}

          onSubstrate={setUseSubstrate} onRecordToSubstrate={setRecordToSubstrate} onEvalMode={setEvalMode}

        />

        <EnginePanel

          train={train}

          knobs={knobs}

          onKnobsChange={setKnobs}

          onStart={startTrain}

          onStop={stopTrain}

        />

        <MoveList

          topMoves={topMoves}

          history={history}

          viewPly={viewPly}

          onGoToPly={goToPly}

          evalMode={evalMode}

          search={search}

          searchDepth={searchDepth}

          searchPickUci={hintMove}

        />

        <ExplorePanel

          fen={fen}

          onPlayMove={!reviewing && !over ? (uci) => void applyUci(uci) : undefined}

        />

        <PstGrid />

      </Sidebar>

    </div>

  );

}


