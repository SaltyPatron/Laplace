import { useEffect, useMemo, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { Button, ErrorText, Input, LoadingText, Muted, Panel, Table, Td, Th } from '@ui';
import { StatCard } from '../../explore/components/StatCard';
import { chessLaplaceGames } from './api';
import { OutcomeChip } from './RecordBar';
import type { ChessGameRow, ChessGamesResponse } from './types';
import styles from './ChessDb.module.css';

const PAGE = 200;
type OutcomeFilter = 'all' | 'win' | 'draw' | 'loss';
type SideFilter = 'all' | 'white' | 'black';

function outcomeName(game: ChessGameRow): OutcomeFilter {
  return game.outcome === 2 ? 'win' : game.outcome === 1 ? 'draw' : game.outcome === 0 ? 'loss' : 'all';
}

export function LaplaceGames() {
  const [params, setParams] = useSearchParams();
  const query = params.get('q')?.trim().toLowerCase() ?? '';
  const outcome = (['win', 'draw', 'loss'].includes(params.get('outcome') ?? '')
    ? params.get('outcome')
    : 'all') as OutcomeFilter;
  const side = (['white', 'black'].includes(params.get('side') ?? '')
    ? params.get('side')
    : 'all') as SideFilter;
  const rawOffset = Number(params.get('offset') ?? 0);
  const offset = Number.isFinite(rawOffset) && rawOffset > 0 ? Math.floor(rawOffset / PAGE) * PAGE : 0;

  const [data, setData] = useState<ChessGamesResponse | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let stale = false;
    setData(null);
    setError(null);
    chessLaplaceGames({ limit: PAGE, offset })
      .then((next) => { if (!stale) setData(next); })
      .catch((e) => { if (!stale) setError(e instanceof Error ? e.message : String(e)); });
    return () => { stale = true; };
  }, [offset]);

  const filtered = useMemo(() => (data?.games ?? []).filter((game) => {
    const matchesText = !query || [game.opponent, game.event, game.eco, game.played_on]
      .some((value) => value?.toLowerCase().includes(query));
    const matchesOutcome = outcome === 'all' || outcomeName(game) === outcome;
    const matchesSide = side === 'all' || (side === 'white') === game.as_white;
    return matchesText && matchesOutcome && matchesSide;
  }), [data, query, outcome, side]);

  function update(key: string, value: string | null, resetPage = true) {
    const next = new URLSearchParams(params);
    if (value) next.set(key, value);
    else next.delete(key);
    if (resetPage) next.delete('offset');
    setParams(next);
  }

  const wins = data?.games.filter((game) => game.outcome === 2).length ?? 0;
  const draws = data?.games.filter((game) => game.outcome === 1).length ?? 0;
  const losses = data?.games.filter((game) => game.outcome === 0).length ?? 0;

  return (
    <div className={styles.laplaceGames}>
      <header className={styles.hero}>
        <nav className={styles.crumbs}>
          <Link to="/chess">Player database</Link> <span>/</span> <span>Laplace games</span>
        </nav>
        <div className={styles.heroTitleRow}>
          <div>
            <h2>Games Laplace played</h2>
            <Muted>
              A dedicated record of browser and connected-board games attributed to Laplace —
              separate from the imported chess archive.
            </Muted>
          </div>
          <Button asChild><Link to="/play">Play Laplace</Link></Button>
        </div>
      </header>

      <div className={styles.statGrid}>
        <StatCard label="Games in this batch" value={(data?.games.length ?? 0).toLocaleString()} />
        <StatCard label="Wins" value={wins.toLocaleString()} />
        <StatCard label="Draws" value={draws.toLocaleString()} />
        <StatCard label="Losses" value={losses.toLocaleString()} />
      </div>

      <Panel title="Find a Laplace game">
        <div className={styles.gameFilters}>
          <label className={styles.filterField}>
            <span>Opponent, event, ECO, or date</span>
            <Input
              value={params.get('q') ?? ''}
              onChange={(event) => update('q', event.target.value || null)}
              placeholder="Search this game batch…"
            />
          </label>
          <label className={styles.filterField}>
            <span>Outcome</span>
            <select value={outcome} onChange={(event) => update('outcome', event.target.value === 'all' ? null : event.target.value)}>
              <option value="all">All outcomes</option>
              <option value="win">Wins</option>
              <option value="draw">Draws</option>
              <option value="loss">Losses</option>
            </select>
          </label>
          <label className={styles.filterField}>
            <span>Side</span>
            <select value={side} onChange={(event) => update('side', event.target.value === 'all' ? null : event.target.value)}>
              <option value="all">Either side</option>
              <option value="white">White</option>
              <option value="black">Black</option>
            </select>
          </label>
        </div>
      </Panel>

      <Panel title="Recorded games">
        {error ? <ErrorText role="alert">Could not read Laplace games: {error}</ErrorText> : null}
        {!error && data === null ? <LoadingText>Reading Laplace’s games…</LoadingText> : null}
        {data && data.games.length === 0 ? (
          <div className={styles.emptyResult}>
            <strong>No attributed Laplace games are recorded in this batch yet.</strong>
            <Muted>New completed browser and Lichess games will appear here when recording is enabled.</Muted>
          </div>
        ) : null}
        {data && data.games.length > 0 && filtered.length === 0 ? (
          <div className={styles.emptyResult}>
            <strong>No games match these filters.</strong>
            <Muted>Clear a filter or move to another batch.</Muted>
          </div>
        ) : null}
        {filtered.length > 0 ? (
          <Table>
            <thead>
              <tr>
                <Th>Date</Th>
                <Th>Side</Th>
                <Th>Opponent</Th>
                <Th>Outcome</Th>
                <Th>Event</Th>
                <Th>ECO</Th>
              </tr>
            </thead>
            <tbody>
              {filtered.map((game) => (
                <tr key={game.id}>
                  <Td><Link className={styles.playerLink} to={`/chess/games/${game.id}`}>{game.played_on ?? 'undated'}</Link></Td>
                  <Td>{game.as_white ? 'White' : 'Black'}</Td>
                  <Td>
                    {game.opponent_id
                      ? <Link className={styles.playerLink} to={`/chess/players/${game.opponent_id}`}>{game.opponent}</Link>
                      : game.opponent || 'Unknown'}
                  </Td>
                  <Td><OutcomeChip outcome={game.outcome} /></Td>
                  <Td>{game.event ?? '—'}</Td>
                  <Td>{game.eco ?? '—'}</Td>
                </tr>
              ))}
            </tbody>
          </Table>
        ) : null}

        {data && data.games.length > 0 ? (
          <div className={styles.pager}>
            <Button variant="ghost" disabled={offset === 0} onClick={() => update('offset', String(Math.max(0, offset - PAGE)), false)}>
              ‹ Newer
            </Button>
            <Muted>{offset + 1}–{offset + data.games.length}</Muted>
            <Button variant="ghost" disabled={data.games.length < PAGE} onClick={() => update('offset', String(offset + PAGE), false)}>
              Older ›
            </Button>
          </div>
        ) : null}
      </Panel>
    </div>
  );
}
