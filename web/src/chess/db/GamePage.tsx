import { useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import { WorldLink as Link } from '../../world/WorldLink';
import { ErrorText, LoadingText, Muted, Panel, ReadStatus, Stack, useReadResource } from '@ui';
import { chessGame, playerPath, tagValue } from './api';
import { GameBoard } from './GameBoard';
import { gameOpenings } from '../openings/api';
import { OpeningName } from '../openings/Openings';
import type { ChessGameResponse } from './types';
import styles from './ChessDb.module.css';

/**
 * One recorded game: its source tags, both players linked to their player pages by name,
 * the game itself linked to its entity page, and the replayed ply sequence. One read
 * gives the tags and the plies.
 */
export function GamePage({ selectedId, embedded = false }: { selectedId?: string; embedded?: boolean } = {}) {
  const { idHex: routeId } = useParams();
  const idHex = selectedId ?? routeId;
  const [game, setGame] = useState<ChessGameResponse | null>(null);
  const [err, setErr] = useState<string | null>(null);

  useEffect(() => {
    if (!idHex) return;
    let stale = false;
    setGame(null);
    setErr(null);
    chessGame(idHex)
      .then((g) => { if (!stale) setGame(g); })
      .catch((e) => { if (!stale) setErr(e instanceof Error ? e.message : String(e)); });
    return () => { stale = true; };
  }, [idHex]);

  // One read for every ply's position: which named openings hold each, the deepest one the
  // line reaches, and the first ply out of book.
  const movetext = game?.movetext ?? null;
  const openings = useReadResource({
    key: JSON.stringify(['chess-game-openings', idHex, movetext]),
    enabled: !!movetext,
    read: (signal) => gameOpenings(movetext!, signal),
  });
  const byPly = new Map(openings.data?.plies.map((row) => [row.ply, row.openings]) ?? []);

  if (err) return <ErrorText>{err}</ErrorText>;
  if (!game) return <LoadingText>Reading the game…</LoadingText>;
  const leaves = openings.data?.out_of_book_at ?? null;
  const leavingMove = leaves != null ? game.plies[leaves - 1] : null;

  return (
    <Stack gap={4}>
      <header className={styles.hero}>
        <nav className={styles.crumbs}>
          {!embedded && <><Link to="/chess">Players</Link> <span>/</span> <span>Game</span></>}
        </nav>
        <h2 className={styles.matchup}>
          <Side name={game.white} />
          <span className={styles.versus}>{game.result ?? 'vs'}</span>
          <Side name={game.black} />
        </h2>
        <Muted>
          {[game.date, game.event].filter(Boolean).join(' · ') || 'undated'} ·{' '}
          <Link to={`/explore/entity/${game.id}`}>see this game as a substrate entity ›</Link>
        </Muted>
      </header>

      <Panel title="Headers">
        <dl className={styles.headerGrid}>
          <Header label="Result" value={game.result} />
          <Header label="Date" value={game.date} />
          <Header label="Event" value={game.event} />
          <Header label="ECO" value={game.eco} />
          <Header label="Termination" value={tagValue(game.tags, 'Termination')} />
          <Header label="Time control" value={tagValue(game.tags, 'TimeControl')} />
        </dl>
        <Muted className={styles.note}>
          Every field above is a tag the source wrote, transcribed verbatim. Anything the
          source left out is shown as missing rather than guessed.
        </Muted>
      </Panel>

      <Panel title="Opening">
        {!movetext ? <Muted>This game carries no movetext, so its opening cannot be read.</Muted> : (
          <>
            <ReadStatus label="Openings" resource={openings} />
            {openings.data && (
              openings.data.deepest ? (
                <Stack gap={2}>
                  <p className={styles.note}>
                    <OpeningName opening={openings.data.deepest} />{' '}
                    <Muted>the deepest named line this game follows ({openings.data.deepest.plies} plies)</Muted>
                  </p>
                  <Muted>
                    {leaves == null
                      ? 'Every position of this game lies on a named opening line.'
                      : `Leaves book at ply ${leaves}${leavingMove ? ` (${leavingMove.white_moved ? `${Math.ceil(leaves / 2)}.` : `${Math.ceil(leaves / 2)}…`} ${leavingMove.san})` : ''}.`}
                    {' '}Step through the replay to see the openings of each position, including transpositions.
                  </Muted>
                </Stack>
              ) : <Muted>No position of this game lies on a named opening line.</Muted>
            )}
          </>
        )}
      </Panel>

      <Panel title="Replay">
        <GameBoard game={game} openingsAt={openings.data ? (ply) => byPly.get(ply) : undefined} />
      </Panel>

      <Panel title="Movetext">
        <Muted className={styles.lede}>
          The movetext recorded for this game. Source headers are shown separately.
        </Muted>
        {!game.complete ? (
          <Muted>This game did not replay completely, so the board above may stop before the movetext does.</Muted>
        ) : null}
        {game.movetext ? (
          <pre className={styles.movetext}>{game.movetext}</pre>
        ) : (
          <Muted>This game carries no movetext.</Muted>
        )}
      </Panel>
    </Stack>
  );
}

/** A player as the game's tag writes the name, linked to that name's page. */
function Side({ name }: { name: string | null }) {
  if (!name) return <span>unknown</span>;
  return <Link className={styles.playerLink} to={playerPath(name)}>{name}</Link>;
}

function Header({ label, value }: { label: string; value: string | null }) {
  return (
    <>
      <dt>{label}</dt>
      <dd className={value ? undefined : styles.missing}>{value ?? 'not recorded'}</dd>
    </>
  );
}
