import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { ErrorText, LoadingText, Muted, Panel, Stack } from '@ui';
import { chessGame, chessGamePlies } from './api';
import { GameBoard } from './GameBoard';
import type { ChessGamePliesResponse, ChessGameResponse } from './types';
import styles from './ChessDb.module.css';

/**
 * One game entity: its source headers, both players linked to their player pages, the
 * game itself linked to its entity page, and the replayed ply trajectory.
 *
 * Headers are source testimony. Movetext is reconstructed from the stored move trajectory,
 * so its SAN is not presented as a byte copy of the source PGN.
 */
export function GamePage() {
  const { idHex } = useParams();
  const [game, setGame] = useState<ChessGameResponse | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [plies, setPlies] = useState<ChessGamePliesResponse | null>(null);
  const [pliesErr, setPliesErr] = useState<string | null>(null);

  useEffect(() => {
    if (!idHex) return;
    let stale = false;
    setGame(null);
    setErr(null);
    setPlies(null);
    setPliesErr(null);
    chessGame(idHex)
      .then((g) => { if (!stale) setGame(g); })
      .catch((e) => { if (!stale) setErr(e instanceof Error ? e.message : String(e)); });
    // Plies are a separate read so the headers render without waiting for the replay.
    chessGamePlies(idHex)
      .then((p) => { if (!stale) setPlies(p); })
      .catch((e) => { if (!stale) setPliesErr(e instanceof Error ? e.message : String(e)); });
    return () => { stale = true; };
  }, [idHex]);

  if (err) return <ErrorText>{err}</ErrorText>;
  if (!game) return <LoadingText>Reading the game…</LoadingText>;

  return (
    <Stack gap={4}>
      <header className={styles.hero}>
        <nav className={styles.crumbs}>
          <Link to="/chess">Players</Link> <span>/</span> <span>Game</span>
        </nav>
        <h2 className={styles.matchup}>
          <Side id={game.white_id} name={game.white} />
          <span className={styles.versus}>{game.result ?? 'vs'}</span>
          <Side id={game.black_id} name={game.black} />
        </h2>
        <Muted>
          {[game.played_on, game.event].filter(Boolean).join(' · ') || 'undated'} ·{' '}
          <Link to={`/explore/entity/${game.id}`}>see this game as a substrate entity ›</Link>
        </Muted>
      </header>

      <Panel title="Headers">
        <dl className={styles.headerGrid}>
          <Header label="Result" value={game.result} />
          <Header label="Date" value={game.played_on} />
          <Header label="Event" value={game.event} />
          <Header label="ECO" value={game.eco} />
          <Header label="Termination" value={game.termination} />
          <Header label="Time control" value={game.time_control} />
          <Header label="Class" value={game.tc_class} />
        </dl>
        <Muted className={styles.note}>
          Every field above is a tag the source wrote, transcribed verbatim. Anything the
          source left out is shown as missing rather than guessed.
        </Muted>
      </Panel>

      <Panel title="Replay">
        {pliesErr ? <ErrorText>{pliesErr}</ErrorText> : null}
        {!pliesErr && plies === null ? <LoadingText>Replaying the game…</LoadingText> : null}
        {plies ? <GameBoard data={plies} white={game.white} black={game.black} /> : null}
      </Panel>

      <Panel title="Reconstructed movetext">
        <Muted style={{ marginBottom: '0.5rem' }}>
          Generated from the recorded typed move trajectory used by the replay above.
          Source headers are shown separately and are not inferred from this rendering.
        </Muted>
        {plies?.truncated ? (
          <Muted>Movetext unavailable: {plies.truncated}</Muted>
        ) : game.movetext ? (
          <pre className={styles.movetext}>{game.movetext}</pre>
        ) : (
          <Muted>This game carries no movetext.</Muted>
        )}
      </Panel>
    </Stack>
  );
}

function Side({ id, name }: { id: string | null; name: string }) {
  if (!id) return <span>{name || 'unknown'}</span>;
  return <Link className={styles.playerLink} to={`/chess/players/${id}`}>{name}</Link>;
}

function Header({ label, value }: { label: string; value: string | null }) {
  return (
    <>
      <dt>{label}</dt>
      <dd className={value ? undefined : styles.missing}>{value ?? 'not recorded'}</dd>
    </>
  );
}
