/** Play view: composes game, board-interaction and training hooks into the board + sidebar layout. */
import { Button, Modal } from '@ui';

import { Board, parseBoard, whiteToMove } from './play/Board';

import { MoveList } from './play/MoveList';

import { MoveNav } from './play/MoveNav';

import { EnginePanel } from './play/EnginePanel';

import { PstGrid } from './play/PstGrid';

import { GameControls } from './play/GameControls';

import { Sidebar } from './play/Sidebar';

import { ExplorePanel } from './play/ExplorePanel';

import { formatPositionEval, whiteCpToBarFraction } from './evalDisplay';

import { sqRC } from './play/playTypes';
import { useBoardInteraction } from './play/useBoardInteraction';
import { usePlayGame } from './play/usePlayGame';
import { useTraining } from './play/useTraining';

import styles from './ChessView.module.css';

export type { SearchInfo } from './play/playTypes';

export function ChessView() {
  const { sel, setSel, marks, setMarks, userArrows, setUserArrows, drag, setDrag, rdrag, boardRef } = useBoardInteraction();
  const {
    viewPly, livePly, reviewing, fen, status, lastMove, liveFen, history,
    legal, inCheck, busy, err,
    autoReply, setAutoReply, flip, setFlip, searchDepth, setSearchDepth,
    useSubstrate, setUseSubstrate, recordToSubstrate, changeRecordToSubstrate, evalMode, setEvalMode,
    promo, setPromo, search, searchPickUci, positionEval, motifs, resumeNote,
    goToPly, botMove, applyUci, playFromTo, newGame,
  } = usePlayGame({ setSel, setDrag, setMarks, setUserArrows });
  const { train, trainBusy, trainStatusError, trainActionError, fetchTrain, knobs, setKnobs, changeTraining } = useTraining();

  const board = parseBoard(fen);
  const pieceAt = (sq: string) => { const { f, r } = sqRC(sq); return board[r]?.[f] ?? ''; };
  const isMine = (sq: string) => {
    const p = pieceAt(sq);
    return !!p && (whiteToMove(fen) ? p === p.toUpperCase() : p === p.toLowerCase());
  };

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

    // The board and its panel lay out by the space the play area has, not the window width.
    <div className={styles.frame}><div className={styles.layout}>

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

            <div className={styles.promotion}>

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

          onSubstrate={setUseSubstrate} onRecordToSubstrate={changeRecordToSubstrate} onEvalMode={setEvalMode}

        />

        <MoveList

          topMoves={topMoves}

          fen={fen}

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

        <EnginePanel

          train={train}
          busy={trainBusy}
          statusError={trainStatusError}
          actionError={trainActionError}
          onRefresh={() => void fetchTrain()}

          knobs={knobs}

          onKnobsChange={setKnobs}

          onStart={() => void changeTraining('start')}

          onStop={() => void changeTraining('stop')}

        />

      </Sidebar>

    </div></div>

  );

}
