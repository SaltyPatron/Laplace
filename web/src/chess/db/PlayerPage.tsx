import { useEffect, useState } from 'react';
import { useParams, useSearchParams } from 'react-router-dom';
import { WorldLink as Link } from '../../world/WorldLink';
import { Button, ErrorText, LoadingText, Muted, Panel, Stack, Table, Td, Th } from '@ui';
import { StatCard } from '../../explore/components/StatCard';
import { chessGamePlies, chessPlayer, chessPlayerGames } from './api';
import { GameBoard } from './GameBoard';
import { OutcomeChip, RecordCell, recordText, scoreText } from './RecordBar';
import type { ChessGamePliesResponse, ChessGameRow, ChessPlayerResponse, ChessRecord } from './types';
import styles from './ChessDb.module.css';

const PAGE = 25;

/**
 * One player entity: witnessed record and colour splits, source-tagged ratings, opponents,
 * and games, each game and opponent linked to its own page. Every count is read from the
 * server as folded; the page derives none of them.
 */
export function PlayerPage({ selectedId, embedded = false }: { selectedId?: string; embedded?: boolean } = {}) {
  const { idHex: routeId } = useParams();
  const idHex = selectedId ?? routeId;
  const [params, setParams] = useSearchParams();
  const [player, setPlayer] = useState<ChessPlayerResponse | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [games, setGames] = useState<ChessGameRow[] | null>(null);
  const [gamesErr, setGamesErr] = useState<string | null>(null);
  const [latestPlies, setLatestPlies] = useState<ChessGamePliesResponse | null>(null);
  const [latestErr, setLatestErr] = useState<string | null>(null);
  const pageParameter = embedded ? 'games_offset' : 'offset';
  const rawOffset = Number(params.get(pageParameter) ?? 0);
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

  const latestGame = offset === 0 && games && games.length > 0 ? games[0] : null;
  useEffect(() => {
    let stale = false;
    setLatestPlies(null);
    setLatestErr(null);
    if (!latestGame) return () => { stale = true; };
    chessGamePlies(latestGame.id)
      .then((p) => { if (!stale) setLatestPlies(p); })
      .catch((e) => { if (!stale) setLatestErr(e instanceof Error ? e.message : String(e)); });
    return () => { stale = true; };
  }, [latestGame?.id]);

  if (err) return <ErrorText>{err}</ErrorText>;
  if (!player) return <LoadingText>Reading the career…</LoadingText>;

  const latestWhite = latestGame?.as_white ? player.name : latestGame?.opponent ?? '';
  const latestBlack = latestGame?.as_white ? latestGame?.opponent ?? '' : player.name;

  return (
    <Stack gap={4}>
      <header className={styles.hero}>
        <nav className={styles.crumbs}>
          {!embedded && <><Link to="/chess">Players</Link> <span>/</span> <span>{player.name}</span></>}
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
                    <span>{providerName(profile.provider)}</span>
                  </div>
                  {profile.title || profile.federation ? (
                    <Muted>{[profile.title, profile.federation].filter(Boolean).join(' · ')}</Muted>
                  ) : null}
                  {profile.biography ? <p>{profile.biography}</p> : null}
                  {profile.aliases.filter((alias) =>
                    alias.localeCompare(profile.display_name, undefined, { sensitivity: 'accent' }) !== 0
                    && alias.localeCompare(profile.provider_id, undefined, { sensitivity: 'accent' }) !== 0
                  ).length > 0 ? (
                    <p className={styles.profileFacts}>
                      Aliases: {profile.aliases.filter((alias) =>
                        alias.localeCompare(profile.display_name, undefined, { sensitivity: 'accent' }) !== 0
                        && alias.localeCompare(profile.provider_id, undefined, { sensitivity: 'accent' }) !== 0
                      ).join(' · ')}
                    </p>
                  ) : null}
                  {Object.keys(profile.ratings).length > 0 ? (
                    <p className={styles.profileFacts}>
                      {Object.entries(profile.ratings).map(([kind, rating]) => `${words(kind)} ${rating}`).join(' · ')}
                    </p>
                  ) : null}
                  {profileFacts(profile.facts).length > 0 ? (
                    <dl className={styles.profileFacts}>
                      {profileFacts(profile.facts).map(([kind, value]) => (
                        <div key={kind}><dt>{kind}</dt><dd>{value}</dd></div>
                      ))}
                    </dl>
                  ) : null}
                  {pageLinks(profile.links).length > 0 ? (
                    <div className={styles.profileLinks}>
                      {pageLinks(profile.links).map((link) => (
                        <a href={link} key={link} target="_blank" rel="noreferrer">
                          {linkLabel(link)} ↗
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

      {latestGame ? (
        <Panel title="Latest witnessed game">
          <div className={styles.latestGameHeading}>
            <div>
              <strong>{latestWhite} {latestGame.result ?? 'vs'} {latestBlack}</strong>
              <Muted>
                {[latestGame.played_on, latestGame.event, latestGame.eco].filter(Boolean).join(' · ') || 'undated'}
              </Muted>
            </div>
            <Link className={styles.playerLink} to={`/chess/games/${latestGame.id}`}>open full game ›</Link>
          </div>
          {latestErr ? <ErrorText>{latestErr}</ErrorText> : null}
          {!latestErr && latestPlies === null ? <LoadingText>Replaying latest game…</LoadingText> : null}
          {latestPlies ? <GameBoard data={latestPlies} white={latestWhite} black={latestBlack} /> : null}
        </Panel>
      ) : null}

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
                  if (value) next.set(pageParameter, String(value)); else next.delete(pageParameter);
                  setParams(next);
                }}>‹ Newer</Button>
              <Muted>
                {offset + 1}–{offset + games.length} of {player.overall.games.toLocaleString()}
              </Muted>
              <Button variant="ghost" disabled={offset + games.length >= player.overall.games}
                onClick={() => {
                  const next = new URLSearchParams(params);
                  next.set(pageParameter, String(offset + PAGE));
                  setParams(next);
                }}>Older ›</Button>
            </div>
          </>
        ) : null}
      </Panel>

      {player.opponents.length > 0 ? (
        <Panel title="Head to head">
          <Muted className={styles.lede}>
            Every meeting between the two players adds to one shared record. Opponents are ranked
            by the cautious standing of that record (rating minus twice its uncertainty), so a long
            even series against a strong rival ranks above a short lopsided one.
          </Muted>
          <Table>
            <thead>
              <tr>
                <Th>Opponent</Th>
                <Th>Games</Th>
                <Th>Standing</Th>
                <Th>Uncertainty</Th>
              </tr>
            </thead>
            <tbody>
              {player.opponents.map((o) => (
                <tr key={o.id}>
                  <Td>
                    <Link className={styles.playerLink} to={`/chess/players/${o.id}`}>{o.name}</Link>
                  </Td>
                  <Td>{o.games.toLocaleString()}</Td>
                  <Td title="rating minus twice its uncertainty">
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
          <Muted className={styles.lede}>
            Elo as the sources recorded it on each game: a spread, not a single number.
            These are other organisations' ratings of this player, kept apart from Laplace's own standing.
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

const PROVIDERS: Record<string, string> = { chesscom: 'Chess.com', 'chess.com': 'Chess.com', lichess: 'Lichess', fide: 'FIDE' };
const providerName = (provider: string) => PROVIDERS[provider.toLowerCase()] ?? provider;
const words = (key: string) => {
  const text = key.replaceAll('_', ' ').replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase();
  return text.charAt(0).toUpperCase() + text.slice(1);
};
/** API endpoints are how a profile was fetched, not something a person can open. */
const isApiUrl = (value: string) => /^https?:\/\/api\./i.test(value) || /\/pub\//.test(value) || /\/api\//.test(value);
const pageLinks = (links: string[]) => links.filter((link) => /^https?:\/\//i.test(link) && !isApiUrl(link));
function linkLabel(link: string): string {
  try { return `Profile on ${new URL(link).hostname.replace(/^www\./, '')}`; } catch { return 'Profile'; }
}
/** A plausible Unix time in seconds (2001–2286) or milliseconds. */
function asDate(value: string): string | null {
  if (!/^\d{10}(\d{3})?$/.test(value)) return null;
  const ms = value.length === 13 ? Number(value) : Number(value) * 1000;
  const date = new Date(ms);
  return Number.isNaN(date.getTime()) ? null : date.toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' });
}
function profileFacts(facts: Record<string, string>): [string, string][] {
  const out: [string, string][] = [];
  for (const [key, raw] of Object.entries(facts)) {
    const value = String(raw).trim();
    if (!value || /^https?:\/\//i.test(value)) continue;
    out.push([words(key), asDate(value) ?? value]);
  }
  return out;
}
