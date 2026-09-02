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
import { browseSubstrate } from './api';
import type { BrowseResponse } from './types';
import styles from './BrowseHome.module.css';

const PAGE = 50;
const DEFAULT_CAPACITY = 2048;
const MAX_CAPACITY = 32768;

function positiveInt(value: string | null, fallback: number) {
  if (value === null || value.trim() === '') return fallback;
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed >= 0 ? Math.floor(parsed) : fallback;
}

export function BrowseHome() {
  const [params, setParams] = useSearchParams();
  const query = params.get('q')?.trim() ?? '';
  const offset = positiveInt(params.get('offset'), 0);
  const capacity = Math.min(MAX_CAPACITY, positiveInt(params.get('capacity'), DEFAULT_CAPACITY));
  const [draft, setDraft] = useState(query);
  const [data, setData] = useState<BrowseResponse | null>(null);
  const [err, setErr] = useState<string | null>(null);

  useEffect(() => setDraft(query), [query]);

  useEffect(() => {
    if (!query) {
      setData(null);
      setErr(null);
      return;
    }
    let stale = false;
    setData(null);
    setErr(null);
    browseSubstrate({ query, offset, limit: PAGE, capacity })
      .then((next) => { if (!stale) setData(next); })
      .catch((reason) => { if (!stale) setErr(reason instanceof Error ? reason.message : String(reason)); });
    return () => { stale = true; };
  }, [query, offset, capacity]);

  function apply(updates: Record<string, string | null>, resetOffset = true) {
    const next = new URLSearchParams(params);
    for (const [key, value] of Object.entries(updates)) {
      if (value) next.set(key, value);
      else next.delete(key);
    }
    if (resetOffset) next.delete('offset');
    setParams(next);
  }

  function submit() {
    const next = draft.trim();
    if (!next) return;
    apply({ q: next, capacity: String(DEFAULT_CAPACITY) });
  }

  function clear() {
    setDraft('');
    setParams(new URLSearchParams());
  }

  function move(nextOffset: number) {
    apply({ offset: nextOffset > 0 ? String(nextOffset) : null }, false);
  }

  function expandFrontier() {
    const nextCapacity = Math.min(MAX_CAPACITY, Math.max(capacity + 1, capacity * 2));
    apply({ capacity: String(nextCapacity) });
  }

  return (
    <Stack gap={4}>
      <header className={styles.hero}>
        <p className={styles.eyebrow}>SUBSTRATE BROWSER</p>
        <h2>Browse Laplace like a reference site</h2>
        <Muted>
          Start with a name or surface, open a canonical entity, then keep following relations,
          compositions, evidence, 2D graph nodes, or the same neighborhood in 3D.
        </Muted>
        <LookupRow
          value={draft}
          onChange={setDraft}
          onSubmit={submit}
          placeholder="Hikaru, Japan, whale, Moby Dick, an entity id…"
          ariaLabel="Find a starting point in the substrate"
          submitLabel="Browse"
          submitDisabled={!draft.trim()}
        >
          {query ? <Button type="button" variant="ghost" onClick={clear}>Clear</Button> : null}
        </LookupRow>
      </header>

      {!query ? <BrowseDirectories /> : null}

      {err ? <ErrorText role="alert">Browse failed: {err}</ErrorText> : null}
      {query && !err && !data ? <LoadingText>Traversing the name lane…</LoadingText> : null}

      {data ? (
        <>
          <Panel title={`Browse results for “${data.query}”`}>
            <div className={styles.toolbar}>
              <Muted>
                {data.receipt.matched_entities.toLocaleString()} canonical result{data.receipt.matched_entities === 1 ? '' : 's'}
                {data.receipt.candidate_truncated ? ' inside the current frontier' : ''}
              </Muted>
              {data.receipt.candidate_truncated && capacity < MAX_CAPACITY ? (
                <Button type="button" size="sm" onClick={expandFrontier}>
                  Expand frontier to {Math.min(MAX_CAPACITY, capacity * 2).toLocaleString()}
                </Button>
              ) : null}
            </div>

            {data.hits.length === 0 ? (
              <div className={styles.empty}>
                <strong>No canonical entity was reached in this browse lane.</strong>
                <Muted>The exact surface can still be explored geometrically if it has not been witnessed.</Muted>
                <Link to={`/explore/notfound/${encodeURIComponent(data.query)}`}>Open its structural neighborhood ›</Link>
              </div>
            ) : (
              <Table>
                <thead>
                  <tr>
                    <Th>Entity</Th>
                    <Th>Type</Th>
                    <Th>Tier</Th>
                    <Th>Matched by</Th>
                    <Th>Conservative</Th>
                    <Th>Rating</Th>
                    <Th>±RD</Th>
                    <Th>Witnesses</Th>
                  </tr>
                </thead>
                <tbody>
                  {data.hits.map((hit) => (
                    <tr key={hit.id_hex}>
                      <Td>
                        <Link className={styles.entityLink} to={`/explore/entity/${hit.id_hex}`}>
                          {hit.label}
                        </Link>
                      </Td>
                      <Td>{hit.type}</Td>
                      <Td>{hit.tier}</Td>
                      <Td>{hit.match_kind === 'name' ? 'name / alias evidence' : 'exact surface'}</Td>
                      <Td>{hit.eff_mu != null ? hit.eff_mu.toFixed(3) : '—'}</Td>
                      <Td>{hit.rating != null ? hit.rating.toFixed(3) : '—'}</Td>
                      <Td>{hit.rd != null ? `±${hit.rd.toFixed(3)}` : '—'}</Td>
                      <Td>{hit.witnesses ? hit.witnesses.toLocaleString() : '—'}</Td>
                    </tr>
                  ))}
                </tbody>
              </Table>
            )}

            <div className={styles.pager}>
              <Button type="button" variant="ghost" disabled={offset <= 0} onClick={() => move(Math.max(0, offset - PAGE))}>
                ‹ Previous
              </Button>
              <Muted>Page {Math.floor(offset / PAGE) + 1}</Muted>
              <Button
                type="button"
                variant="ghost"
                disabled={data.hits.length < PAGE || offset + data.hits.length >= data.receipt.matched_entities}
                onClick={() => move(offset + PAGE)}
              >
                Next ›
              </Button>
            </div>
          </Panel>

          <Panel title="Browse-step receipt">
            <dl className={styles.receipt}>
              <div><dt>Query root</dt><dd>{data.receipt.query_root_id_hex}</dd></div>
              <div><dt>Word identities</dt><dd>{data.receipt.query_member_ids_hex.length.toLocaleString()}</dd></div>
              <div><dt>Name frontier</dt><dd>{data.receipt.candidate_names.toLocaleString()} / {data.receipt.candidate_capacity.toLocaleString()}</dd></div>
              <div><dt>Frontier complete</dt><dd>{data.receipt.candidate_truncated ? 'no — capacity reached' : 'yes for this lane'}</dd></div>
              <div><dt>Returned</dt><dd>{data.receipt.returned.toLocaleString()}</dd></div>
              <div><dt>Substrate read</dt><dd>{data.receipt.elapsed_us.toLocaleString()} μs</dd></div>
            </dl>
          </Panel>
        </>
      ) : null}
    </Stack>
  );
}

function BrowseDirectories() {
  return (
    <Panel title="Browse directories">
      <div className={styles.directoryGrid}>
        <Directory to="/chess" title="Chess" text="Players → careers → opponents → games → positions and lines." />
        <Directory to="/explore/highway" title="Highway" text="Structural altitude, lexical worlds, and typed relation lanes." />
        <Directory to="/explore/warehouse" title="Warehouse" text="Sources → stages → witness rosters → asserted entities." />
        <Directory to="/explore/walk" title="Walk" text="Follow a witnessed path step by step through the substrate." />
        <Directory to="/explore/matchup" title="Matchup" text="Compare two canonical entities through the same relation state." />
        <Directory to="/explore/audit" title="Audit" text="Inspect health and realization gaps behind what the browser can reach." />
      </div>
    </Panel>
  );
}

function Directory({ to, title, text }: { to: string; title: string; text: string }) {
  return (
    <Link className={styles.directoryCard} to={to}>
      <strong>{title}</strong>
      <span>{text}</span>
    </Link>
  );
}
