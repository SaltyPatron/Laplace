/** Play-view game state: position history, legal moves, eval, retained play session, bot replies and moves. */
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';

import { apiGet, apiPost } from '../../api/client';
import { useAppStore } from '../../store';

import type { MoveScore } from './Board';
import { terminalWhiteCp } from '../evalDisplay';
import {
  historyFromPositions,
  sanFromPositions,
  initialPositions,
  snapshotFromMove,
  type PositionSnapshot,
} from '../gameHistory';
import {
  clearSavedPlayGame,
  loadSavedPlayGame,
  savePlayGame,
  savedGameIsResumable,
} from '../playPersist';

import type { BoardInteraction } from './useBoardInteraction';
import {
  msg,
  type BestMove,
  type LegalResponse,
  type PlayMoveResult,
  type PlayStart,
  type PositionEval,
  type RetainedPlay,
  type SearchInfo,
} from './playTypes';

type InteractionSetters = Pick<BoardInteraction, 'setSel' | 'setDrag' | 'setMarks' | 'setUserArrows'>;

export function usePlayGame({ setSel, setDrag, setMarks, setUserArrows }: InteractionSetters) {
  const tenant = useAppStore((state) => state.tenant);

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
  const [busy, setBusy] = useState(false);
  const [autoReply, setAutoReply] = useState(() => savedBoot?.autoReply ?? true);
  const [err, setErr] = useState<string | null>(null);
  const [searchDepth, setSearchDepth] = useState(() => savedBoot?.searchDepth ?? 4);
  const [useSubstrate, setUseSubstrate] = useState(() => savedBoot?.useSubstrate ?? true);
  const [recordToSubstrate, setRecordToSubstrate] = useState(() => savedBoot?.recordToSubstrate ?? true);
  const [playSessionId, setPlaySessionId] = useState<string | null>(savedBoot?.sessionId ?? null);
  const sessionReady = useRef<Promise<string | null> | null>(null);
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

  const livePly = positions.length - 1;
  const reviewing = viewPly < livePly;
  const snap = positions[viewPly];
  const live = positions[livePly];
  const fen = snap.fen;
  const status = snap.status;
  const lastMove = snap.lastMove;
  const liveFen = live.fen;
  const history = useMemo(() => sanFromPositions(positions), [positions]);

  const goToPly = useCallback((ply: number) => {
    setViewPly(Math.max(0, Math.min(livePly, ply)));
    setSel(null);
    setDrag(null);
    setMarks(new Set());
    setUserArrows([]);
  }, [livePly, setSel, setDrag, setMarks, setUserArrows]);

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

  const startPlaySession = useCallback(async (record: boolean, moves?: string[]) => {
    try {
      const r = await apiPost<PlayStart>('/chess/play/start', {
        record,
        moves: moves && moves.length > 0 ? moves : undefined,
        tenant,
      });
      setPlaySessionId(r.sessionId);
      sessionReady.current = Promise.resolve(r.sessionId);
      return r;
    } catch (e) {
      setErr(`play session failed: ${msg(e)}`);
      setPlaySessionId(null);
      return null;
    }
  }, [tenant]);

  const positionsRef = useRef(positions);
  positionsRef.current = positions;

  // A retained operation keeps the same requester and game through browser and API restarts.
  useEffect(() => {
    if (!savedBoot?.sessionId) return;
    let active = true;
    sessionReady.current = (async () => {
      try {
          let retained = await apiGet<RetainedPlay>('/chess/play/' + savedBoot.sessionId);
          if (retained.canRetryRecording) {
            await apiPost('/chess/play/' + retained.sessionId + '/recording/retry', {});
            retained = await apiGet<RetainedPlay>('/chess/play/' + savedBoot.sessionId);
          }
          if (retained.state !== 'ongoing' && retained.state !== 'completed')
            throw new Error(`Saved game recording is ${retained.state}; the game remains retained.`);
          const finalStatus = retained.state === 'ongoing' ? 'ongoing'
            : retained.result === '1-0' ? 'white wins' : retained.result === '0-1' ? 'black wins' : 'draw';
          const restored = retained.positionFens.map((fen, ply) => ply === 0
            ? { fen, status: retained.moves.length === 0 ? finalStatus : 'ongoing', lastMove: null }
            : snapshotFromMove(fen, ply === retained.moves.length ? finalStatus : 'ongoing', retained.moves[ply - 1]));
          if (active) {
            setPositions(restored);
            setViewPly(restored.length - 1);
            setPlaySessionId(retained.sessionId);
            setResumeNote(`${retained.state === 'completed' ? 'Recorded game' : 'Resumed saved game'} · ${retained.moves.length} plies`);
          }
          return retained.sessionId;
      } catch (e) {
        if (active) setErr(`Cannot resume saved game: ${msg(e)}`);
        return null;
      }
    })();
    return () => { active = false; };
  }, [savedBoot]);

  useEffect(() => {
    savePlayGame({
      sessionId: playSessionId,
      positions,
      recordToSubstrate,
      searchDepth,
      useSubstrate,
      autoReply,
      flip,
      evalMode,
    });
  }, [playSessionId, positions, recordToSubstrate, searchDepth, useSubstrate, autoReply, flip, evalMode]);

  const ensurePlaySession = useCallback(async () => {
    if (sessionReady.current) return await sessionReady.current;
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

      if (!r.legal) {
        setErr(inCheck
          ? `illegal while in check: ${uci} does not resolve check`
          : `illegal move: ${uci}`);
        return;
      }

      appendSnapshot(uci, r.fen, r.status);
      setSearchPickUci(null);
      setMotifs(r.motifs ?? []);
      setErr(null);
      if (!r.terminal && autoReply) await botMove(r.fen);
    } catch (e) { setErr(`move failed: ${msg(e)}`); }
    finally { setBusy(false); }
  }, [liveFen, autoReply, botMove, appendSnapshot, reviewing, ensurePlaySession, startPlaySession, recordToSubstrate, inCheck]);

  const playFromTo = useCallback(async (from: string, to: string) => {
    if (reviewing) { setErr('Go to the latest move to play'); return; }
    if (busy || status !== 'ongoing' || from === to) return;
    const moves = legalFen === fen ? legal : [];
    const cands = moves.filter((m) => m.uci.slice(0, 4) === from + to);
    if (cands.length > 1 && cands.some((m) => m.uci.length === 5)) {
      setPromo({ from, to });
      return;
    }
    const uci = cands[0]?.uci;
    if (!uci) {
      if (legalLoading || legalFen !== fen) {
        setErr('Legal moves still loading — try again');
        return;
      }
      setErr(inCheck
        ? `Illegal while in check: ${from}${to} does not resolve check`
        : `Illegal move: ${from}${to}`);
      return;
    }
    await applyUci(uci);
  }, [reviewing, busy, status, legal, legalFen, fen, legalLoading, applyUci, inCheck]);

  const newGame = async () => {
    if (playSessionId) {
      try {
        const retained = await apiGet<RetainedPlay>('/chess/play/' + playSessionId);
        if (retained.canRetryRecording)
          await apiPost('/chess/play/' + playSessionId + '/recording/retry', {});
        else if (retained.state === 'ongoing' && status !== 'ongoing')
          await apiPost('/chess/play/finish', { sessionId: playSessionId, status });
        else if (retained.state !== 'ongoing' && retained.state !== 'completed')
          throw new Error(`Game recording is ${retained.state}; the game remains retained.`);
      } catch (e) {
        setErr(`Cannot finish saved game: ${msg(e)}`);
        return;
      }
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

  const changeRecordToSubstrate = (record: boolean) => {
    setRecordToSubstrate(record);
    setPlaySessionId(null);
    sessionReady.current = null;
  };

  return {
    positions, viewPly, livePly, reviewing, fen, status, lastMove, liveFen, history,
    legal, inCheck, busy, err,
    autoReply, setAutoReply, flip, setFlip, searchDepth, setSearchDepth,
    useSubstrate, setUseSubstrate, recordToSubstrate, changeRecordToSubstrate, evalMode, setEvalMode,
    promo, setPromo, search, searchPickUci, positionEval, motifs, resumeNote,
    goToPly, botMove, applyUci, playFromTo, newGame,
  };
}
