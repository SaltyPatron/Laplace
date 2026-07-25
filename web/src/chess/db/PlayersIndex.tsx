import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { Button, ErrorText, Input, LoadingText, Muted, Panel, Stack, Table, Td, Th } from '@ui';
import { chessPlayers } from './api';
import type { ChessPlayersResponse } from './types';
import styles from './ChessDb.module.css';

const PAGE = 50;

const ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'.split('');

/**
 * The roster — everyone the corpus has ever witnessed at a board, ranked by how
 * much of them it witnessed. There is no minimum-games floor and no rating cut:
 * a one-game player is a real witness with a one-game record, just further down.
 *
 * Nothing here is cached. The ranking is a read of consensus cells the fold already
 * wrote, so paging is an OFFSET over an index and search is a content-address lookup.
 * There is no warm window, no TTL, and no depth beyond which the roster stops knowing.
 */
export function PlayersIndex() {
  const [params, setParams] = useSearchParams();
  const search = params.get('q') ?? '';
  const initial = params.get('initial') ?? '';
  const offset = Number(params.get('offset') ?? 0);

  const [draft, setDraft] = useState(search);
  const [data, setData] = useState<ChessPlayersResponse | null>(null);
  const [err, setErr] = useState<string | null>(null);

  useEffect(() => setDraft(search), [search]);

  useEffect(() => {
    let stale = false;
    setData(null);
    setErr(null);
    chessPlayers({ limit: PAGE, offset, search: search || undefined, initial: initial || undefined })
      .then((d) => { if (!stale) setData(d); })
      .catch((e) => { if (!stale) setErr(e instanceof Error ? e.message : String(e)); });
    return () => { stale = true; };
  }, [search, initial, offset]);

  const submit = (e: React.FormEvent) => {
    e.preventDefault();
    setParams(draft.trim() ? { q: draft.trim() } : {});
  };

  return (
    <Stack gap={4}>
      <header className={styles.hero}>
        <h2>Players</h2>
        <Muted>
          Every player the substrate has witnessed at a board, ranked by the conservative
          Glicko-2 estimate — rating − 2·RD, folded from his games at ingest. Not a win
          percentage: beating stronger opponents counts for more, and a thin record sinks
          on its own uncertainty rather than flattering itself.
        </Muted>
        {/*
          Browse by first letter. A name's first codepoint is vertex 1 of its trajectory,
          so this is an indexed range scan over the authoritative geometry — it reaches
          every player in the corpus, not just the ones a ranked page happened to include.
        */}
        <nav className={styles.alphabet} aria-label="Browse players by first letter">
          {ALPHABET.map((ch) => (
            <button
              key={ch}
              type="button"
              data-active={initial === ch}
              className={styles.letter}
              onClick={() => setParams(initial === ch ? {} : { initial: ch })}
            >
              {ch}
            </button>
          ))}
        </nav>
        <form className={styles.searchRow} onSubmit={submit}>
          <Input
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            placeholder="Find a player — “Tal, Mikhail”, “mikhail tal”, or just “tal”"
            aria-label="Find a player by name"
          />
          <Button type="submit">Find</Button>
          {search ? (
            <Button variant="ghost" type="button" onClick={() => setParams({})}>Clear</Button>
          ) : null}
        </form>
      </header>

      <Panel title={
        search ? `Search — “${search}”`
        : initial ? `Players — ${initial}`
        : 'Strongest players'
      }>
        {err ? <ErrorText>{err}</ErrorText> : null}
        {!err && data === null ? <LoadingText>Counting careers…</LoadingText> : null}
        {data && data.players.length === 0 ? (
          <Muted>
            {initial ? (
              <>No player whose name begins with “{initial}” has been witnessed.</>
            ) : search ? (
              <>
                No player named “{search}” has been witnessed. Names resolve by content
                address, so spell it the way the source records it — “Tal, Mikhail” or
                “mikhail tal” both land on the same player, however deep in the corpus he sits.
              </>
            ) : (
              'No games have been ingested yet.'
            )}
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
                  <Th>Rating</Th>
                  <Th>±RD</Th>
                </tr>
              </thead>
              <tbody>
                {data.players.map((p) => (
                  <tr key={p.id}>
                    <Td>{p.rank || '—'}</Td>
                    <Td>
                      <Link className={styles.playerLink} to={`/chess/players/${p.id}`}>{p.name}</Link>
                    </Td>
                    <Td>{p.games.toLocaleString()}</Td>
                    <Td title={`eff_mu ${p.eff_mu.toFixed(0)} = rating − 2·RD`}>
                      {p.eff_mu.toFixed(0)}
                    </Td>
                    <Td>±{p.rd.toFixed(0)}</Td>
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
                <Muted>{offset + 1}–{offset + data.players.length}</Muted>
                <Button
                  variant="ghost"
                  disabled={data.players.length < PAGE}
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
