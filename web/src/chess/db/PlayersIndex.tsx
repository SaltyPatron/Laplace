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
import { chessPlayers, playerPath } from './api';
import type { ChessPlayerRow, ChessPlayersResponse } from './types';
import styles from './ChessDb.module.css';

const PAGE = 50;

function safeOffset(raw: string | null): number {
  const value = Number(raw ?? 0);
  return Number.isFinite(value) && value > 0 ? Math.floor(value / PAGE) * PAGE : 0;
}

/** A game count; one that hit the read's cap is shown as "N+". */
function gamesText(player: ChessPlayerRow): string {
  return `${player.games.toLocaleString()}${player.games_complete ? '' : '+'}`;
}

/** A URL-addressable player search: the query and the page survive refresh/back. */
export function PlayersIndex() {
  const [params, setParams] = useSearchParams();
  const search = params.get('q')?.trim() ?? '';
  const offset = safeOffset(params.get('offset'));

  const [draft, setDraft] = useState(search);
  const [data, setData] = useState<ChessPlayersResponse | null>(null);
  const [err, setErr] = useState<string | null>(null);

  useEffect(() => setDraft(search), [search]);

  useEffect(() => {
    let stale = false;
    setData(null);
    setErr(null);
    // The player read needs a search; without one the page only prompts for it.
    if (!search) return () => { stale = true; };
    chessPlayers({ search, limit: PAGE, offset })
      .then((next) => { if (!stale) setData(next); })
      .catch((e) => { if (!stale) setErr(e instanceof Error ? e.message : String(e)); });
    return () => { stale = true; };
  }, [search, offset]);

  function updateParams(updates: Record<string, string | null>) {
    const next = new URLSearchParams(params);
    for (const [key, value] of Object.entries(updates)) {
      if (value) next.set(key, value);
      else next.delete(key);
    }
    setParams(next);
  }

  function submit() {
    updateParams({ q: draft.trim() || null, offset: null });
  }

  function movePage(nextOffset: number) {
    updateParams({ offset: nextOffset > 0 ? String(nextOffset) : null });
  }

  return (
    <Stack gap={4}>
      <header className={styles.hero}>
        <div className={styles.heroTitleRow}>
          <div>
            <h2>Player database</h2>
            <Muted>
              Search by a name as the games write it, or by one word of it: a surname
              finds every name that contains it.
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
          placeholder="Carlsen, Fischer, Tal, Mikhail…"
          ariaLabel="Find a chess player"
          submitLabel="Search players"
          submitDisabled={!draft.trim()}
        >
          {search ? (
            <Button
              variant="ghost"
              type="button"
              onClick={() => {
                setDraft('');
                updateParams({ q: null, offset: null });
              }}
            >
              Clear
            </Button>
          ) : null}
        </LookupRow>
      </header>

      {!search ? (
        <Panel title="Find a player">
          <div className={styles.emptyResult}>
            <strong>Search for a player to see their recorded games.</strong>
            <Muted>
              Players are listed by the names their games were recorded under; there is no
              roster to browse without a search.
            </Muted>
          </div>
        </Panel>
      ) : (
        <Panel title={`Player matches for “${search}”`}>
          <div className={styles.resultToolbar}>
            <Muted aria-live="polite">
              {data
                ? `${data.total.toLocaleString()} matching player${data.total === 1 ? '' : 's'}`
                : err ? '' : 'Reading player index…'}
            </Muted>
          </div>

          {err ? <ErrorText role="alert">Could not read players: {err}</ErrorText> : null}
          {!err && data === null ? <LoadingText>Searching recorded games…</LoadingText> : null}
          {data && data.players.length === 0 ? (
            <div className={styles.emptyResult}>
              <strong>No recorded player matched “{search}”.</strong>
              <Muted>Try the surname alone, or the name exactly as a game writes it.</Muted>
            </div>
          ) : null}

          {data && data.players.length > 0 ? (
            <>
              <Table>
                <thead>
                  <tr>
                    <Th>Player</Th>
                    <Th>Games</Th>
                  </tr>
                </thead>
                <tbody>
                  {data.players.map((player) => (
                    <tr key={player.name}>
                      <Td>
                        <Link className={styles.playerLink} to={playerPath(player.name)}>
                          {player.name}
                        </Link>
                      </Td>
                      <Td title={player.games_complete ? undefined : 'at least this many games'}>
                        {gamesText(player)}
                      </Td>
                    </tr>
                  ))}
                </tbody>
              </Table>

              <div className={styles.pager}>
                <Button variant="ghost" disabled={offset <= 0} onClick={() => movePage(offset - PAGE)}>
                  ‹ Previous
                </Button>
                <Muted>
                  {offset + 1}–{offset + data.players.length} of {data.total.toLocaleString()}
                </Muted>
                <Button
                  variant="ghost"
                  disabled={offset + data.players.length >= data.total}
                  onClick={() => movePage(offset + PAGE)}
                >
                  Next ›
                </Button>
              </div>
            </>
          ) : null}
        </Panel>
      )}
    </Stack>
  );
}
