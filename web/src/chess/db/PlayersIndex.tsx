import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { Button, ErrorText, Input, LoadingText, Muted, Panel, Stack, Table, Td, Th } from '@ui';
import { chessPlayers } from './api';
import { RecordCell, scoreText } from './RecordBar';
import type { ChessPlayersResponse } from './types';
import styles from './ChessDb.module.css';

const PAGE = 50;

/**
 * The roster — everyone the corpus has ever witnessed at a board, ranked by how
 * much of them it witnessed. There is no minimum-games floor and no rating cut:
 * a one-game player is a real witness with a one-game record, just further down.
 *
 * Search is a content-address lookup, not a filter over this page: the server
 * folds the typed name the way the decomposer did and hashes it, so a name hits
 * one player directly however deep in the corpus he sits.
 */
export function PlayersIndex() {
  const [params, setParams] = useSearchParams();
  const search = params.get('q') ?? '';
  const offset = Number(params.get('offset') ?? 0);

  const [draft, setDraft] = useState(search);
  const [data, setData] = useState<ChessPlayersResponse | null>(null);
  const [err, setErr] = useState<string | null>(null);

  useEffect(() => setDraft(search), [search]);

  useEffect(() => {
    let stale = false;
    setData(null);
    setErr(null);
    chessPlayers({ limit: PAGE, offset, search: search || undefined })
      .then((d) => { if (!stale) setData(d); })
      .catch((e) => { if (!stale) setErr(e instanceof Error ? e.message : String(e)); });
    return () => { stale = true; };
  }, [search, offset]);

  const submit = (e: React.FormEvent) => {
    e.preventDefault();
    setParams(draft.trim() ? { q: draft.trim() } : {});
  };

  return (
    <Stack gap={4}>
      <header className={styles.hero}>
        <h2>Players</h2>
        <Muted>
          Every player the substrate has witnessed at a board, ranked by games recorded.
          Records are counted off the game headers themselves — nothing is inferred.
        </Muted>
        <form className={styles.searchRow} onSubmit={submit}>
          <Input
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            placeholder="Find a player — “Tal, Mikhail” or “mikhail tal”"
            aria-label="Find a player by name"
          />
          <Button type="submit">Find</Button>
          {search ? (
            <Button variant="ghost" type="button" onClick={() => setParams({})}>Clear</Button>
          ) : null}
        </form>
      </header>

      <Panel title={search ? `Search — “${search}”` : 'Most-witnessed players'}>
        {err ? <ErrorText>{err}</ErrorText> : null}
        {!err && data === null ? <LoadingText>Counting careers…</LoadingText> : null}
        {data && data.players.length === 0 ? (
          <Muted>
            {search
              ? `No player named “${search}” has been witnessed. Names resolve exactly — try the
                 form the source records, e.g. “Tal, Mikhail”.`
              : 'No games have been ingested yet.'}
          </Muted>
        ) : null}

        {data && data.players.length > 0 ? (
          <>
            <Table>
              <thead>
                <tr>
                  <Th>#</Th>
                  <Th>Player</Th>
                  <Th>Games</Th>
                  <Th>W–L–D</Th>
                  <Th>Score</Th>
                </tr>
              </thead>
              <tbody>
                {data.players.map((p) => (
                  <tr key={p.id}>
                    <Td>{p.rank || '—'}</Td>
                    <Td>
                      <Link className={styles.playerLink} to={`/chess/players/${p.id}`}>{p.name}</Link>
                    </Td>
                    <Td>{p.record.games.toLocaleString()}</Td>
                    <Td><RecordCell record={p.record} /></Td>
                    <Td>{scoreText(p.record)}</Td>
                  </tr>
                ))}
              </tbody>
            </Table>

            {!search ? (
              <div className={styles.pager}>
                <Button
                  variant="ghost"
                  disabled={offset <= 0}
                  onClick={() => setParams({ offset: String(Math.max(0, offset - PAGE)) })}
                >
                  ‹ Previous
                </Button>
                <Muted>
                  {offset + 1}–{offset + data.players.length} of {data.total.toLocaleString()} ranked
                </Muted>
                <Button
                  variant="ghost"
                  disabled={offset + data.players.length >= data.total}
                  onClick={() => setParams({ offset: String(offset + PAGE) })}
                >
                  Next ›
                </Button>
              </div>
            ) : null}
          </>
        ) : null}
      </Panel>
    </Stack>
  );
}
