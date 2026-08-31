import { useEffect, useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import { Button, ErrorText, LoadingText, Muted, Panel, Stack, Table, Td, Th } from '@ui';
import { StatCard } from '../../explore/components/StatCard';
import { chessPlayer, chessPlayerGames } from './api';
import { OutcomeChip, RecordCell, recordText, scoreText } from './RecordBar';
import type { ChessGameRow, ChessPlayerResponse, ChessRecord } from './types';
import styles from './ChessDb.module.css';

const PAGE = 25;

/**
 * A career. The headline record, the colour splits, the Elo the sources tagged,
 * the rivals, and the games themselves — each one a drill into the game page,
 * each opponent a drill into his own career.
 *
 * The splits are not computed here: they arrive already reconciled with the
 * total from one pass over the same evidence, so what this page shows is what
 * the substrate counted, not what a client re-derived.
 */
export function PlayerPage() {
  const { idHex } = useParams();
  const [params, setParams] = useSearchParams();
  const [player, setPlayer] = useState<ChessPlayerResponse | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [games, setGames] = useState<ChessGameRow[] | null>(null);
  const [gamesErr, setGamesErr] = useState<string | null>(null);
  const rawOffset = Number(params.get('offset') ?? 0);
  const offset = Number.isFinite(rawOffset) && rawOffset > 0
    ? Math.floor(rawOffset / PAGE) * PAGE
    : 0;

  useEffect(() => {
    if (!idHex) return;
    let stale = false;
    setPlayer(null);
    setErr(null);
    chessPlayer(idHex)
      .then((p) => { if (!stale) setPlayer(p); })
      .catch((e) => { if (!stale) setErr(e instanceof Error ? e.message : String(e)); });
    return () => { stale = true; };
  }, [idHex]);

  useEffect(() => {
    if (!idHex) return;
    let stale = false;
    setGames(null);
    setGamesErr(null);
    chessPlayerGames(idHex, { limit: PAGE, offset })
      .then((g) => { if (!stale) setGames(g.games); })
      .catch((e) => { if (!stale) setGamesErr(e instanceof Error ? e.message : String(e)); });
    return () => { stale = true; };
  }, [idHex, offset]);

  if (err) return <ErrorText>{err}</ErrorText>;
  if (!player) return <LoadingText>Reading the career…</LoadingText>;

  return (
    <Stack gap={4}>
      <header className={styles.hero}>
        <nav className={styles.crumbs}>
          <Link to="/chess">Players</Link> <span>/</span> <span>{player.name}</span>
        </nav>
        <h2>{player.name}</h2>
        <Muted>
          {player.overall.games.toLocaleString()} witnessed games ·{' '}
          <Link to={`/explore/entity/${player.id}`}>see this player as a substrate entity ›</Link>
        </Muted>
      </header>

      {player.profiles.length > 0 ? (
        <Panel title="Profiles & identities">
          <div className={styles.profileGrid}>
            {player.profiles.map((profile) => (
              <article className={styles.profileCard} key={`${profile.provider}:${profile.provider_id}`}>
                {profile.avatar_url ? (
                  <img className={styles.profileAvatar} src={profile.avatar_url} alt="" />
                ) : null}
                <div className={styles.profileBody}>
                  <div className={styles.profileHeading}>
                    <strong>{profile.display_name}</strong>
                    <span>{profile.provider}</span>
                  </div>
                  <Muted>
                    {profile.title ? `${profile.title} · ` : ''}
                    {profile.federation ? `${profile.federation} · ` : ''}
                    {profile.provider_id}
                  </Muted>
                  {profile.biography ? <p>{profile.biography}</p> : null}
                  {Object.keys(profile.ratings).length > 0 ? (
                    <p className={styles.profileFacts}>
                      {Object.entries(profile.ratings).map(([kind, rating]) => `${kind} ${rating}`).join(' · ')}
                    </p>
                  ) : null}
                  {profile.links.length > 0 ? (
                    <div className={styles.profileLinks}>
                      {profile.links.map((link, i) => (
                        <a href={link} key={link} target="_blank" rel="noreferrer">
                          {i === 0 ? 'Official profile' : `Link ${i + 1}`} ↗
                        </a>
                      ))}
                    </div>
                  ) : null}
                </div>
              </article>
            ))}
          </div>
        </Panel>
      ) : null}

      <div className={styles.statGrid}>
        <StatCard label="Games" value={player.overall.games.toLocaleString()} />
        <StatCard label="Record (W–L–D)" value={recordText(player.overall)} />
        <StatCard label="Score" value={scoreText(player.overall)} />
        <StatCard
          label="Peak rating"
          value={player.peak_rating != null ? String(player.peak_rating) : '—'}
          sub={player.peak_rating != null ? 'highest Elo any source tagged' : 'no rating tags recorded'}
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

      <Panel title="Games">
        {gamesErr ? <ErrorText>{gamesErr}</ErrorText> : null}
        {!gamesErr && games === null ? <LoadingText>Loading games…</LoadingText> : null}
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
                </tr>
              </thead>
              <tbody>
                {games.map((g) => (
                  <tr key={g.id}>
                    <Td>
                      <Link className={styles.playerLink} to={`/chess/games/${g.id}`}>
                        {g.played_on ?? 'undated'}
                      </Link>
                    </Td>
                    <Td>{g.as_white ? 'White' : 'Black'}</Td>
                    <Td>
                      {g.opponent_id ? (
                        <Link className={styles.playerLink} to={`/chess/players/${g.opponent_id}`}>
                          {g.opponent}
                        </Link>
                      ) : (
                        g.opponent || '—'
                      )}
                    </Td>
                    <Td>
                      <OutcomeChip outcome={g.outcome} />{' '}
                      <span className={styles.resultToken}>{g.result ?? ''}</span>
                    </Td>
                    <Td>{g.event ?? '—'}</Td>
                    <Td>{g.eco ?? '—'}</Td>
                  </tr>
                ))}
              </tbody>
            </Table>
            <div className={styles.pager}>
              <Button variant="ghost" disabled={offset <= 0}
                onClick={() => {
                  const next = new URLSearchParams(params);
                  const value = Math.max(0, offset - PAGE);
                  if (value) next.set('offset', String(value)); else next.delete('offset');
                  setParams(next);
                }}>‹ Newer</Button>
              <Muted>
                {offset + 1}–{offset + games.length} of {player.overall.games.toLocaleString()}
              </Muted>
              <Button variant="ghost" disabled={offset + games.length >= player.overall.games}
                onClick={() => {
                  const next = new URLSearchParams(params);
                  next.set('offset', String(offset + PAGE));
                  setParams(next);
                }}>Older ›</Button>
            </div>
          </>
        ) : null}
      </Panel>

      {player.opponents.length > 0 ? (
        <Panel title="Head to head">
          <Muted style={{ marginBottom: '0.5rem' }}>
            One folded cell per pairing — every meeting between the two lands on the same
            cell, so the games count IS its witness count. Ranked by eff_mu, so a long even
            series against a strong rival outranks a short lopsided one.
          </Muted>
          <Table>
            <thead>
              <tr>
                <Th>Opponent</Th>
                <Th>Games</Th>
                <Th>Rating</Th>
                <Th>±RD</Th>
              </tr>
            </thead>
            <tbody>
              {player.opponents.map((o) => (
                <tr key={o.id}>
                  <Td>
                    <Link className={styles.playerLink} to={`/chess/players/${o.id}`}>{o.name}</Link>
                  </Td>
                  <Td>{o.games.toLocaleString()}</Td>
                  <Td title={`eff_mu ${o.eff_mu.toFixed(0)} = rating − 2·RD`}>
                    {o.eff_mu.toFixed(0)}
                  </Td>
                  <Td>±{o.rd.toFixed(0)}</Td>
                </tr>
              ))}
            </tbody>
          </Table>
        </Panel>
      ) : null}

      {player.ratings.length > 0 ? (
        <Panel title="Ratings witnessed">
          <Muted style={{ marginBottom: '0.5rem' }}>
            Elo as the sources tagged it, per game — a distribution, not a single number.
            This is someone else's rating of him, kept apart from the substrate's own.
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
