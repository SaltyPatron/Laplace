import { useEffect, useMemo, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { Button, ErrorText, Input, LoadingText, Muted, Panel, Table, Td, Th, Select } from '@ui';
import { StatCard } from '../../explore/components/StatCard';
import { chessLaplaceGames, playerPath } from './api';
import type { ChessGamesResponse } from './types';
import { DeepestOpeningCell, useDeepestOpenings } from '../openings/Openings';
import styles from './ChessDb.module.css';

const PAGE = 200;
/** The PGN result tokens a game can be filtered by. */
const RESULTS = ['1-0', '1/2-1/2', '0-1'] as const;
type ResultFilter = 'all' | typeof RESULTS[number];

export function LaplaceGames() {
  const [params, setParams] = useSearchParams();
  const query = params.get('q')?.trim().toLowerCase() ?? '';
  const result = (RESULTS as readonly string[]).includes(params.get('result') ?? '')
    ? params.get('result') as ResultFilter
    : 'all';
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

  // One batched opening read for the loaded page, not per filter.
  const deepest = useDeepestOpenings(data?.games ?? null);
  const filtered = useMemo(() => (data?.games ?? []).filter((game) => {
    const matchesText = !query || [game.white, game.black, game.event, game.eco, game.date]
      .some((value) => value?.toLowerCase().includes(query));
    return matchesText && (result === 'all' || game.result === result);
  }), [data, query, result]);

  function update(key: string, value: string | null, resetPage = true) {
    const next = new URLSearchParams(params);
    if (value) next.set(key, value);
    else next.delete(key);
    if (resetPage) next.delete('offset');
    setParams(next);
  }

  const count = (token: string) => data?.games.filter((game) => game.result === token).length ?? 0;

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
              The games Laplace itself recorded from the browser and connected boards,
              separate from the imported chess archive.
            </Muted>
          </div>
          <Button asChild><Link to="/play">Play Laplace</Link></Button>
        </div>
      </header>

      <div className={styles.statGrid}>
        <StatCard label="Games recorded" value={(data?.total ?? 0).toLocaleString()} />
        <StatCard label="White wins in this batch" value={count('1-0').toLocaleString()} />
        <StatCard label="Draws in this batch" value={count('1/2-1/2').toLocaleString()} />
        <StatCard label="Black wins in this batch" value={count('0-1').toLocaleString()} />
      </div>

      <Panel title="Filter this game batch">
        <div className={styles.gameFilters}>
          <label className={styles.filterField}>
            <span>Player, event, ECO, or date</span>
            <Input
              value={params.get('q') ?? ''}
              onChange={(event) => update('q', event.target.value || null)}
              placeholder="Filter this game batch…"
            />
          </label>
          <label className={styles.filterField}>
            <span>Result</span>
            <Select value={result} onChange={(event) => update('result', event.target.value === 'all' ? null : event.target.value)}>
              <option value="all">All results</option>
              <option value="1-0">White won (1-0)</option>
              <option value="1/2-1/2">Draw (1/2-1/2)</option>
              <option value="0-1">Black won (0-1)</option>
            </Select>
          </label>
        </div>
      </Panel>

      <Panel title="Recorded games">
        {error ? <ErrorText role="alert">Could not read Laplace games: {error}</ErrorText> : null}
        {!error && data === null ? <LoadingText>Reading Laplace’s games…</LoadingText> : null}
        {data && data.games.length === 0 ? (
          <div className={styles.emptyResult}>
            <strong>No Laplace games are recorded in this batch yet.</strong>
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
                <Th>White</Th>
                <Th>Black</Th>
                <Th>Result</Th>
                <Th>Event</Th>
                <Th>ECO</Th>
                <Th title="The deepest named opening this game follows, read from the substrate">Opening</Th>
              </tr>
            </thead>
            <tbody>
              {filtered.map((game) => (
                <tr key={game.id}>
                  <Td><Link className={styles.playerLink} to={`/chess/games/${game.id}`}>{game.date ?? 'undated'}</Link></Td>
                  <Td><PlayerName name={game.white} /></Td>
                  <Td><PlayerName name={game.black} /></Td>
                  <Td><span className={styles.resultToken}>{game.result ?? '—'}</span></Td>
                  <Td>{game.event ?? '—'}</Td>
                  <Td>{game.eco ?? '—'}</Td>
                  <Td><DeepestOpeningCell id={game.id} openings={deepest} /></Td>
                </tr>
              ))}
            </tbody>
          </Table>
        ) : null}

        {data && data.games.length > 0 ? (
          <div className={styles.pager}>
            <Button variant="ghost" disabled={offset === 0} onClick={() => update('offset', offset > PAGE ? String(offset - PAGE) : null, false)}>
              ‹ Newer
            </Button>
            <Muted>{offset + 1}–{offset + data.games.length} of {data.total.toLocaleString()}</Muted>
            <Button variant="ghost" disabled={offset + data.games.length >= data.total} onClick={() => update('offset', String(offset + PAGE), false)}>
              Older ›
            </Button>
          </div>
        ) : null}
      </Panel>
    </div>
  );
}

/** A player as the game's tag writes the name, linked to that name's page. */
function PlayerName({ name }: { name: string | null }) {
  if (!name) return <>Unknown</>;
  return <Link className={styles.playerLink} to={playerPath(name)}>{name}</Link>;
}
