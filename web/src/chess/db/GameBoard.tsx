import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { Button, Muted, SegmentedControl } from '@ui';
import { Board } from '../play/Board';
import type { ChessGamePliesResponse, ChessPlyRow } from './types';
import styles from './ChessDb.module.css';

/** Playback speeds. "clock" replays the recorded think times; the rest are fixed intervals. */
type Speed = 'clock' | 'fast' | 'normal' | 'slow';

const FIXED_MS: Record<Exclude<Speed, 'clock'>, number> = { fast: 250, normal: 700, slow: 1500 };

const SPEED_LABELS = ['Real time', 'Slow', 'Normal', 'Fast'] as const;
const LABEL_OF: Record<Speed, string> = { clock: 'Real time', slow: 'Slow', normal: 'Normal', fast: 'Fast' };
const SPEED_OF: Record<string, Speed> = { 'Real time': 'clock', Slow: 'slow', Normal: 'normal', Fast: 'fast' };

/** Real time is capped: a 4-minute think is a fact worth showing, not worth sitting through. */
const MAX_REALTIME_MS = 6000;

function mmss(seconds: number | null | undefined): string {
  if (seconds == null) return '—';
  const s = Math.max(0, Math.round(seconds));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
}

/**
 * How long to wait before the ply at `i` appears — the time its mover actually spent,
 * recovered by diffing that side's own consecutive clock readings. A player's clock only
 * runs on their own turn, so the previous reading for the SAME side is two plies back;
 * diffing against the opponent's reading would attribute their think to this move.
 * Increments make a diff negative, which is real and simply means no wait.
 */
function realtimeGapMs(plies: ChessPlyRow[], i: number): number {
  const here = plies[i]?.clock_seconds;
  const prevSameSide = plies[i - 2]?.clock_seconds;
  if (here == null || prevSameSide == null) return FIXED_MS.normal;
  return Math.min(Math.max(0, (prevSameSide - here) * 1000), MAX_REALTIME_MS);
}

/**
 * The game, played back. The ply sequence is reconstructed server-side by replaying the
 * recorded movetext through the same engine that plays live chess — so what moves on this
 * board is the witnessed game, not a re-rendering of a stored picture of one.
 *
 * Each position carries its own content address, so any ply can be opened as the substrate
 * entity it is: the same board thousands of other games reached, with its rated
 * continuations attached. That link is the point of the whole page.
 */
export function GameBoard({ data, white, black }: { data: ChessGamePliesResponse; white: string; black: string }) {
  const { plies, start_fen: startFen, has_clocks: hasClocks } = data;

  const [ply, setPly] = useState(0);
  const [playing, setPlaying] = useState(false);
  const [speed, setSpeed] = useState<Speed>(hasClocks ? 'clock' : 'normal');
  const [flip, setFlip] = useState(false);
  const boardRef = useRef<HTMLDivElement | null>(null);
  const listRef = useRef<HTMLOListElement | null>(null);

  const atEnd = ply >= plies.length;
  const current = ply > 0 ? plies[ply - 1] : null;
  const fen = current?.fen ?? startFen;

  const lastMove = useMemo(
    () => (current ? { from: current.uci.slice(0, 2), to: current.uci.slice(2, 4) } : null),
    [current],
  );

  useEffect(() => {
    if (!playing || atEnd) { if (atEnd) setPlaying(false); return; }
    const delay = speed === 'clock' ? realtimeGapMs(plies, ply) : FIXED_MS[speed];
    const t = setTimeout(() => setPly((p) => p + 1), delay);
    return () => clearTimeout(t);
  }, [playing, ply, atEnd, speed, plies]);

  // Keep the moving ply in view without yanking the whole page.
  useEffect(() => {
    listRef.current?.querySelector('[data-active="true"]')
      ?.scrollIntoView({ block: 'nearest' });
  }, [ply]);

  const step = useCallback((delta: number) => {
    setPlaying(false);
    setPly((p) => Math.min(plies.length, Math.max(0, p + delta)));
  }, [plies.length]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement) return;
      if (e.key === 'ArrowRight') { e.preventDefault(); step(1); }
      else if (e.key === 'ArrowLeft') { e.preventDefault(); step(-1); }
      else if (e.key === 'Home') { e.preventDefault(); setPlaying(false); setPly(0); }
      else if (e.key === 'End') { e.preventDefault(); setPlaying(false); setPly(plies.length); }
      else if (e.key === ' ') { e.preventDefault(); setPlaying((p) => !p); }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [step, plies.length]);

  if (plies.length === 0) {
    return <Muted>This game carries no playable moves.</Muted>;
  }

  // Clocks are per-side: show each player their own most recent reading.
  const clockFor = (isWhite: boolean): number | null => {
    if (!hasClocks) return null;
    for (let i = ply - 1; i >= 0; i--) if (plies[i].white_moved === isWhite) return plies[i].clock_seconds ?? null;
    return null;
  };

  const topName = flip ? white : black;
  const botName = flip ? black : white;
  const topClock = clockFor(flip);
  const botClock = clockFor(!flip);

  return (
    <div className={styles.replay}>
      <div className={styles.boardCol}>
        <div className={styles.clockRow}>
          <span className={styles.sideName}>{topName}</span>
          {hasClocks ? <span className={styles.clock}>{mmss(topClock)}</span> : null}
        </div>

        <Board
          fen={fen}
          legal={[]}
          sel={null}
          drag={null}
          marks={new Set()}
          userArrows={[]}
          showPick={false}
          whiteEval=""
          evalFrac={0.5}
          evalDetail=""
          boardRef={boardRef}
          flip={flip}
          lastMove={lastMove}
          readOnly
          onPointerDown={() => {}}
          onPointerUp={() => {}}
          onDragMove={() => {}}
        />

        <div className={styles.clockRow}>
          <span className={styles.sideName}>{botName}</span>
          {hasClocks ? <span className={styles.clock}>{mmss(botClock)}</span> : null}
        </div>

        <div className={styles.transport}>
          <Button variant="ghost" onClick={() => { setPlaying(false); setPly(0); }} aria-label="Start" disabled={ply === 0}>⏮</Button>
          <Button variant="ghost" onClick={() => step(-1)} aria-label="Previous move" disabled={ply === 0}>◀</Button>
          <Button onClick={() => (atEnd ? (setPly(0), setPlaying(true)) : setPlaying((p) => !p))} aria-label={playing ? 'Pause' : 'Play'}>
            {playing ? '❚❚' : atEnd ? '↻' : '▶'}
          </Button>
          <Button variant="ghost" onClick={() => step(1)} aria-label="Next move" disabled={atEnd}>▶</Button>
          <Button variant="ghost" onClick={() => { setPlaying(false); setPly(plies.length); }} aria-label="End" disabled={atEnd}>⏭</Button>
          <Button variant="ghost" onClick={() => setFlip((f) => !f)} aria-label="Flip board">⇅</Button>
        </div>

        <input
          className={styles.scrub}
          type="range"
          min={0}
          max={plies.length}
          value={ply}
          onChange={(e) => { setPlaying(false); setPly(Number(e.target.value)); }}
          aria-label="Scrub through the game"
        />

        <div className={styles.speedRow}>
          <SegmentedControl
            label="Playback speed"
            options={hasClocks ? SPEED_LABELS : SPEED_LABELS.slice(1)}
            value={LABEL_OF[speed]}
            onValueChange={(v) => setSpeed(SPEED_OF[v])}
          />
          <Muted className={styles.speedNote}>
            {speed === 'clock'
              ? 'each move waits the time its player actually spent, from the recorded clocks'
              : 'fixed interval between moves'}
          </Muted>
        </div>
      </div>

      <div className={styles.movesCol}>
        <div className={styles.movesHead}>
          <span>{ply === 0 ? 'Starting position' : `${current!.white_moved ? 'White' : 'Black'} played ${current!.san}`}</span>
          {current ? (
            <Link className={styles.playerLink} to={`/explore/entity/${current.position_id}`}>
              open this position ›
            </Link>
          ) : null}
        </div>
        <ol className={styles.moveList} ref={listRef}>
          {plies.map((p, i) => (
            <li key={p.ply}>
              {p.white_moved ? <span className={styles.moveNo}>{Math.floor(i / 2) + 1}.</span> : null}
              <button
                type="button"
                data-active={ply === i + 1}
                className={styles.moveBtn}
                onClick={() => { setPlaying(false); setPly(i + 1); }}
              >
                {p.san}
                {hasClocks ? <span className={styles.moveClock}>{mmss(p.clock_seconds)}</span> : null}
              </button>
            </li>
          ))}
        </ol>
        {data.truncated ? (
          <Muted className={styles.note}>Replay stopped: {data.truncated}</Muted>
        ) : null}
      </div>
    </div>
  );
}
