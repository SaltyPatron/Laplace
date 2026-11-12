import { useEffect, useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import { Button, ErrorText, LoadingText, Muted, Panel, Stack, Table, Td, Th } from '@ui';
import { StatCard } from '../../explore/components/StatCard';
import { chessGame, chessPlayer, playerPath } from './api';
import { GameBoard } from './GameBoard';
import { OutcomeChip, RecordCell, recordText, scoreText } from './RecordBar';
import type { ChessGameResponse, ChessPlayerResponse, ChessRecord } from './types';
import { DeepestOpeningCell, useDeepestOpenings } from '../openings/Openings';
import styles from './ChessDb.module.css';

const PAGE = 25;

/**
 * One player, addressed by the name as a PGN White/Black tag writes it: the record and
 * its colour splits, the source's own Elo tags, opponents, and games, each game and
 * opponent linked to its own page. Every count is read from the server; the page derives
 * none of them.
 */
export function PlayerPage() {
  const { name } = useParams();
  const [params, setParams] = useSearchParams();
  const [player, setPlayer] = useState<ChessPlayerResponse | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [latest, setLatest] = useState<ChessGameResponse | null>(null);
  const [latestErr, setLatestErr] = useState<string | null>(null);
  const rawOffset = Number(params.get('offset') ?? 0);
  const offset = Number.isFinite(rawOffset) && rawOffset > 0
    ? Math.floor(rawOffset / PAGE) * PAGE
    : 0;

  // One read gives the record, ratings, opponents and this page of games.
  useEffect(() => {
    if (!name) return;
    let stale = false;
    setErr(null);
    // Paging keeps the record on screen; another player clears it.
    setPlayer((current) => (current?.name === name ? current : null));
    chessPlayer(name, { limit: PAGE, offset })
      .then((p) => { if (!stale) setPlayer(p); })
      .catch((e) => { if (!stale) setErr(e instanceof Error ? e.message : String(e)); });
    return () => { stale = true; };
  }, [name, offset]);

  const games = player && player.offset === offset ? player.games : null;
  const latestGame = offset === 0 && games && games.length > 0 ? games[0] : null;
  // One batched opening read for the page of games.
  const deepest = useDeepestOpenings(games);
  useEffect(() => {
    let stale = false;
    setLatest(null);
    setLatestErr(null);
    if (!latestGame) return () => { stale = true; };
    chessGame(latestGame.id)
      .then((g) => { if (!stale) setLatest(g); })
      .catch((e) => { if (!stale) setLatestErr(e instanceof Error ? e.message : String(e)); });
    return () => { stale = true; };
  }, [latestGame?.id]);

  if (err) return <ErrorText>{err}</ErrorText>;
  if (!player) return <LoadingText>Reading the career…</LoadingText>;

  const movePage = (value: number) => {
    const next = new URLSearchParams(params);
    if (value > 0) next.set('offset', String(value)); else next.delete('offset');
    setParams(next);
  };

  return (
    <Stack gap={4}>
      <header className={styles.hero}>
        <nav className={styles.crumbs}>
          <Link to="/chess">Players</Link> <span>/</span> <span>{player.name}</span>
        </nav>
        <h2>{player.name}</h2>
        <Muted>
          {player.overall.games === 0
            ? 'No recorded games under this name.'
            : `${player.overall.games.toLocaleString()} recorded games under this name`}
        </Muted>
      </header>

      {player.overall.games === 0 ? (
        <Panel title="No recorded games">
          <div className={styles.emptyResult}>
            <strong>Nobody played a recorded game under this name.</strong>
            <Muted>
              A player is the name exactly as a game writes it. <Link to="/chess">Search the players</Link> to
              find the spelling the games use.
            </Muted>
          </div>
        </Panel>
      ) : (
        <>
          <div className={styles.statGrid}>
            <StatCard label="Games" value={player.overall.games.toLocaleString()} />
            <StatCard label="Record (W–L–D)" value={recordText(player.overall)} />
            <StatCard label="Score" value={scoreText(player.overall)} />
            <StatCard
              label="Peak rating"
              value={player.peak_rating != null ? String(player.peak_rating) : '—'}
              sub={player.peak_rating != null ? 'highest Elo a game tagged' : 'no rating tags recorded'}
            />
          </div>

          <Panel title="By colour">
            <Table>
              <thead>
                <tr>
                  <Th>Side</Th>
                  <Th>Games</Th>
                  <Th>W–L–D</Th>
                  <Th>Score</Th>
                </tr>
              </thead>
              <tbody>
                <ColourRow side="As White" record={player.as_white} />
                <ColourRow side="As Black" record={player.as_black} />
                <ColourRow side="Overall" record={player.overall} />
              </tbody>
            </Table>
          </Panel>

          {latestGame ? (
            <Panel title="Latest recorded game">
              <div className={styles.latestGameHeading}>
                <div>
                  <strong>{latestGame.white ?? 'unknown'} {latestGame.result ?? 'vs'} {latestGame.black ?? 'unknown'}</strong>
                  <Muted>
                    {[latestGame.date, latestGame.event, latestGame.eco].filter(Boolean).join(' · ') || 'undated'}
                  </Muted>
                </div>
                <Link className={styles.playerLink} to={`/chess/games/${latestGame.id}`}>open full game ›</Link>
              </div>
              {latestErr ? <ErrorText>{latestErr}</ErrorText> : null}
              {!latestErr && latest === null ? <LoadingText>Replaying latest game…</LoadingText> : null}
              {latest ? <GameBoard game={latest} /> : null}
            </Panel>
          ) : null}

          <Panel title="Games">
            {games === null ? <LoadingText>Loading games…</LoadingText> : null}
            {games && games.length === 0 ? (
              <Muted>No games on this page.</Muted>
            ) : null}
            {games && games.length > 0 ? (
              <>
                <Table>
                  <thead>
                    <tr>
                      <Th>Date</Th>
                      <Th>Colour</Th>
                      <Th>Opponent</Th>
                      <Th>Result</Th>
                      <Th>Event</Th>
                      <Th>ECO</Th>
                      <Th title="The deepest named opening this game follows, read from the substrate">Opening</Th>
                    </tr>
                  </thead>
                  <tbody>
                    {games.map((g) => (
                      <tr key={g.id}>
                        <Td>
                          <Link className={styles.playerLink} to={`/chess/games/${g.id}`}>
                            {g.date ?? 'undated'}
                          </Link>
                        </Td>
                        <Td>{g.as_white ? 'White' : 'Black'}</Td>
                        <Td>
                          {g.opponent ? (
                            <Link className={styles.playerLink} to={playerPath(g.opponent)}>
                              {g.opponent}
                            </Link>
                          ) : '—'}
                        </Td>
                        <Td>
                          <OutcomeChip outcome={g.outcome} />{' '}
                          <span className={styles.resultToken}>{g.result ?? ''}</span>
                        </Td>
                        <Td>{g.event ?? '—'}</Td>
                        <Td>{g.eco ?? '—'}</Td>
                        <Td><DeepestOpeningCell id={g.id} openings={deepest} /></Td>
                      </tr>
                    ))}
                  </tbody>
                </Table>
                <div className={styles.pager}>
                  <Button variant="ghost" disabled={offset <= 0}
                    onClick={() => movePage(Math.max(0, offset - PAGE))}>‹ Newer</Button>
                  <Muted>
                    {offset + 1}–{offset + games.length} of {player.total.toLocaleString()}
                  </Muted>
                  <Button variant="ghost" disabled={offset + games.length >= player.total}
                    onClick={() => movePage(offset + PAGE)}>Older ›</Button>
                </div>
              </>
            ) : null}
          </Panel>

          {player.opponents.length > 0 ? (
            <Panel title="Head to head">
              <Muted className={styles.lede}>
                {player.opponents.length < player.opponents_total
                  ? `The ${player.opponents.length.toLocaleString()} most played of ${player.opponents_total.toLocaleString()} opponents. `
                  : 'Opponents, most games first. '}
                Wins and losses are those of {player.name}.
              </Muted>
              <Table>
                <thead>
                  <tr>
                    <Th>Opponent</Th>
                    <Th>Games</Th>
                    <Th>W–L–D</Th>
                    <Th>Score</Th>
                  </tr>
                </thead>
                <tbody>
                  {player.opponents.map((o) => (
                    <tr key={o.name}>
                      <Td>
                        <Link className={styles.playerLink} to={playerPath(o.name)}>{o.name}</Link>
                      </Td>
                      <Td>{o.games.toLocaleString()}</Td>
                      <Td><RecordCell record={o} /></Td>
                      <Td>{scoreText(o)}</Td>
                    </tr>
                  ))}
                </tbody>
              </Table>
            </Panel>
          ) : null}

          {player.ratings.length > 0 ? (
            <Panel title="Source ratings">
              <Muted className={styles.lede}>
                Elo as the games tagged it for this player: a spread, not a single number.
                These are the sources' own ratings; Laplace assigns a player none.
              </Muted>
              <Table>
                <thead>
                  <tr><Th>Rating</Th><Th>Games</Th></tr>
                </thead>
                <tbody>
                  {player.ratings.slice(0, 25).map((r) => (
                    <tr key={r.rating}>
                      <Td>{r.rating}</Td>
                      <Td>{r.games.toLocaleString()}</Td>
                    </tr>
                  ))}
                </tbody>
              </Table>
            </Panel>
          ) : null}
        </>
      )}
    </Stack>
  );
}

function ColourRow({ side, record }: { side: string; record: ChessRecord }) {
  return (
    <tr>
      <Td>{side}</Td>
      <Td>{record.games.toLocaleString()}</Td>
      <Td><RecordCell record={record} /></Td>
      <Td>{scoreText(record)}</Td>
    </tr>
  );
}
