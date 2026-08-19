import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import {
  Button,
  ErrorText,
  LoadingText,
  LookupRow,
  Muted,
  Panel,
  Stack,
  Table,
  Td,
  Th,
} from '@ui';
import { chessPlayers } from './api';
import type { ChessPlayersResponse } from './types';
import styles from './ChessDb.module.css';

const PAGE = 50;
const ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'.split('');
const SORTS = ['relevance', 'strength', 'games', 'rating', 'rd'] as const;
type PlayerSort = typeof SORTS[number];
type SortDirection = 'asc' | 'desc';

function safeOffset(raw: string | null): number {
  const value = Number(raw ?? 0);
  return Number.isFinite(value) && value > 0 ? Math.floor(value / PAGE) * PAGE : 0;
}

function safeSort(raw: string | null, searching: boolean): PlayerSort {
  if (SORTS.includes(raw as PlayerSort)) return raw as PlayerSort;
  return searching ? 'relevance' : 'strength';
}

/** A URL-addressable player browser: query, initial, order, direction and page all survive refresh/back. */
export function PlayersIndex() {
  const [params, setParams] = useSearchParams();
  const search = params.get('q')?.trim() ?? '';
  const initial = params.get('initial')?.toUpperCase().slice(0, 1) ?? '';
  const offset = safeOffset(params.get('offset'));
  const sort = safeSort(params.get('sort'), !!search);
  const direction: SortDirection = params.get('direction') === 'asc' ? 'asc' : 'desc';

  const [draft, setDraft] = useState(search);
  const [data, setData] = useState<ChessPlayersResponse | null>(null);
  const [err, setErr] = useState<string | null>(null);

  useEffect(() => setDraft(search), [search]);

  useEffect(() => {
    let stale = false;
    setData(null);
    setErr(null);
    chessPlayers({
      limit: PAGE,
      offset,
      search: search || undefined,
      initial: initial || undefined,
      sort,
      direction,
    })
      .then((next) => { if (!stale) setData(next); })
      .catch((e) => { if (!stale) setErr(e instanceof Error ? e.message : String(e)); });
    return () => { stale = true; };
  }, [search, initial, offset, sort, direction]);

  function updateParams(
    updates: Record<string, string | null>,
    { resetPage = true }: { resetPage?: boolean } = {},
  ) {
    const next = new URLSearchParams(params);
    for (const [key, value] of Object.entries(updates)) {
      if (value) next.set(key, value);
      else next.delete(key);
    }
    if (resetPage) next.delete('offset');
    setParams(next);
  }

  function submit() {
    const query = draft.trim();
    updateParams({ q: query || null, initial: null });
  }

  function chooseInitial(letter: string) {
    updateParams({ initial: initial === letter ? null : letter, q: null });
    setDraft('');
  }

  function chooseSort(nextSort: PlayerSort) {
    if (nextSort === 'relevance') {
      updateParams({ sort: 'relevance', direction: 'desc' });
      return;
    }
    const nextDirection: SortDirection = sort === nextSort
      ? (direction === 'desc' ? 'asc' : 'desc')
      : nextSort === 'rd' ? 'asc' : 'desc';
    updateParams({ sort: nextSort, direction: nextDirection });
  }

  function movePage(nextOffset: number) {
    updateParams(
      { offset: nextOffset > 0 ? String(nextOffset) : null },
      { resetPage: false },
    );
  }

  const title = search
    ? `Player matches for “${search}”`
    : initial
      ? `Players beginning with ${initial}`
      : 'Player standings';

  return (
    <Stack gap={4}>
      <header className={styles.hero}>
        <div className={styles.heroTitleRow}>
          <div>
            <h2>Player database</h2>
            <Muted>
              Search by surname, full name, or a close spelling. Browse by initial when you
              do not know the exact source spelling.
            </Muted>
          </div>
          <Link className={styles.laplaceGamesLink} to="/chess/laplace">
            <span aria-hidden="true">◆</span>
            Games Laplace played
          </Link>
        </div>

        <LookupRow
          value={draft}
          onChange={setDraft}
          onSubmit={submit}
          placeholder="Karpov, Fischer, Mikhail Tal…"
          ariaLabel="Find a chess player"
          submitLabel="Search players"
          submitDisabled={!draft.trim()}
        >
          {search || initial ? (
            <Button
              variant="ghost"
              type="button"
              onClick={() => {
                setDraft('');
                updateParams({ q: null, initial: null });
              }}
            >
              Clear
            </Button>
          ) : null}
        </LookupRow>

        <nav className={styles.alphabet} aria-label="Browse players by first letter">
          {ALPHABET.map((letter) => (
            <button
              key={letter}
              type="button"
              aria-pressed={initial === letter}
              data-active={initial === letter}
              className={styles.letter}
              onClick={() => chooseInitial(letter)}
            >
              {letter}
            </button>
          ))}
        </nav>
      </header>

      <Panel title={title}>
        <div className={styles.resultToolbar}>
          <Muted aria-live="polite">
            {data
              ? search
                ? `${data.total.toLocaleString()} matching player${data.total === 1 ? '' : 's'}`
                : `Showing ${offset + 1}–${offset + data.players.length}`
              : 'Reading player index…'}
          </Muted>
          {search ? (
            <Button
              size="sm"
              variant="nav"
              active={sort === 'relevance'}
              onClick={() => chooseSort('relevance')}
            >
              Best match {sort === 'relevance' ? (direction === 'asc' ? '↑' : '↓') : ''}
            </Button>
          ) : null}
        </div>

        {err ? <ErrorText role="alert">Could not read players: {err}</ErrorText> : null}
        {!err && data === null ? <LoadingText>Searching witnessed careers…</LoadingText> : null}
        {data && data.players.length === 0 ? (
          <div className={styles.emptyResult}>
            <strong>
              {search || initial
                ? `No witnessed player matched “${search || initial}”.`
                : 'No chess careers have been witnessed yet.'}
            </strong>
            {search || initial
              ? <Muted>Try a surname, a nearby spelling, or choose a first letter.</Muted>
              : <Muted>Ingest a PGN or finish a recorded game to populate this index.</Muted>}
          </div>
        ) : null}

        {data && data.players.length > 0 ? (
          <>
            <Table>
              <thead>
                <tr>
                  <Th>#</Th>
                  <Th>Player</Th>
                  <SortableHead label="Games" value="games" active={sort} direction={direction} onSort={chooseSort} />
                  <SortableHead label="Conservative" value="strength" active={sort} direction={direction} onSort={chooseSort} />
                  <SortableHead label="Rating" value="rating" active={sort} direction={direction} onSort={chooseSort} />
                  <SortableHead label="±RD" value="rd" active={sort} direction={direction} onSort={chooseSort} />
                </tr>
              </thead>
              <tbody>
                {data.players.map((player) => (
                  <tr key={player.id}>
                    <Td>{player.rank || '—'}</Td>
                    <Td>
                      <Link className={styles.playerLink} to={`/chess/players/${player.id}`}>
                        {player.name}
                      </Link>
                    </Td>
                    <Td>{player.games.toLocaleString()}</Td>
                    <Td title="rating − 2·RD">{player.eff_mu.toFixed(0)}</Td>
                    <Td>{player.rating.toFixed(0)}</Td>
                    <Td>±{player.rd.toFixed(0)}</Td>
                  </tr>
                ))}
              </tbody>
            </Table>

            <div className={styles.pager}>
              <Button variant="ghost" disabled={offset <= 0} onClick={() => movePage(offset - PAGE)}>
                ‹ Previous
              </Button>
              <Muted>Page {Math.floor(offset / PAGE) + 1}</Muted>
              <Button
                variant="ghost"
                disabled={data.players.length < PAGE || (!!search && offset + data.players.length >= data.total)}
                onClick={() => movePage(offset + PAGE)}
              >
                Next ›
              </Button>
            </div>
          </>
        ) : null}
      </Panel>
    </Stack>
  );
}

function SortableHead({
  label,
  value,
  active,
  direction,
  onSort,
}: {
  label: string;
  value: Exclude<PlayerSort, 'relevance'>;
  active: PlayerSort;
  direction: SortDirection;
  onSort: (sort: PlayerSort) => void;
}) {
  const selected = active === value;
  return (
    <Th aria-sort={selected ? (direction === 'asc' ? 'ascending' : 'descending') : 'none'}>
      <button className={styles.sortButton} type="button" onClick={() => onSort(value)}>
        {label}
        <span className={styles.sortMark} aria-hidden="true">
          {selected ? (direction === 'asc' ? '↑' : '↓') : '↕'}
        </span>
      </button>
    </Th>
  );
}
