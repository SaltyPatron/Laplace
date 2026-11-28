import { useEffect, useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import { Alert, Button, ErrorText, Input, LoadingText, Muted, Panel, Select, Stack, Table, Td, Th } from '@ui';
import { useAccess } from '../../auth/access';
import { StatCard } from '../../explore/components/StatCard';
import { chessGame, chessPlayer, linkChessPlayerFide, playerPath } from './api';
import { GameBoard } from './GameBoard';
import { OutcomeChip, RecordCell, recordText, scoreText } from './RecordBar';
import type {
  ChessFideLinkReceipt,
  ChessFideProfile,
  ChessGameResponse,
  ChessPlayerAlias,
  ChessPlayerResponse,
  ChessRecord,
} from './types';
import { DeepestOpeningCell, useDeepestOpenings } from '../openings/Openings';
import styles from './ChessDb.module.css';

const PAGE = 25;

function aliasKindLabel(kind: ChessPlayerAlias['kind']): string {
  switch (kind) {
    case 'chesscom': return 'Chess.com';
    case 'lichess': return 'Lichess';
    case 'fide': return 'FIDE';
  }
}

/**
 * One player, addressed by the name as a PGN White/Black tag writes it: the record and
 * its colour splits, the source's own Elo tags, opponents, and games, each game and
 * opponent linked to its own page. Every count is read from the server; the page derives
 * none of them.
 */
export function PlayerPage() {
  const { name } = useParams();
  const [params, setParams] = useSearchParams();
  const access = useAccess();
  const canManageIngest = !access.error
    && (access.data?.platform.permissions.includes('ingestion.manage') ?? false);
  const [player, setPlayer] = useState<ChessPlayerResponse | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [latest, setLatest] = useState<ChessGameResponse | null>(null);
  const [latestErr, setLatestErr] = useState<string | null>(null);
  const [refreshTick, setRefreshTick] = useState(0);
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
  }, [name, offset, refreshTick]);

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
        <div className={styles.heroNameLine}>
          {player.fide?.title ? <span className={styles.titleBadge}>{player.fide.title}</span> : null}
          <h2>{player.name}</h2>
          {player.fide ? <span className={styles.fideIdTag}>FIDE {player.fide.id}</span> : null}
        </div>
        <Muted>
          {player.overall.games === 0
            ? 'No recorded games under this name.'
            : `${player.overall.games.toLocaleString()} recorded games under this name`}
        </Muted>
      </header>

      {player.fide ? (
        <FideProfilePanel fide={player.fide} aliases={player.aliases ?? []} currentName={player.name} />
      ) : null}

      {canManageIngest ? (
        <AdminFideLinkPanel
          key={`${player.name}:${player.fide?.id ?? ''}`}
          playerName={player.name}
          initialFideId={player.fide?.id ?? ''}
          onLinked={() => setRefreshTick((value) => value + 1)}
        />
      ) : null}

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

function FideProfilePanel({
  fide,
  aliases,
  currentName,
}: {
  fide: ChessFideProfile;
  aliases: ChessPlayerAlias[];
  currentName: string;
}) {
  return (
    <Panel title="FIDE Profile">
      <div className={styles.fidePanelBody}>
        <div className={styles.fideIdentityRow}>
          {fide.title ? <span className={styles.titleBadge}>{fide.title}</span> : null}
          <strong>{fide.name ?? `FIDE ${fide.id}`}</strong>
          <span className={styles.fideIdTag}>ID {fide.id}</span>
          {fide.federation ? <span className={styles.fideMetaChip}>{fide.federation}</span> : null}
          {fide.birth_year != null ? <span className={styles.fideMetaChip}>Born {fide.birth_year}</span> : null}
        </div>

        <div className={styles.statGrid}>
          <StatCard label="Standard" value={fide.standard != null ? String(fide.standard) : '—'} />
          <StatCard label="Rapid" value={fide.rapid != null ? String(fide.rapid) : '—'} />
          <StatCard label="Blitz" value={fide.blitz != null ? String(fide.blitz) : '—'} />
        </div>

        {aliases.length > 0 ? (
          <div className={styles.fideAliases}>
            <Muted>Linked identities</Muted>
            <ul className={styles.aliasList}>
              {aliases.map((alias) => (
                <li key={`${alias.kind}:${alias.name}`}>
                  <Link
                    className={styles.aliasChip}
                    data-current={alias.name === currentName ? 'true' : undefined}
                    to={playerPath(alias.name)}
                  >
                    <span className={styles.aliasKind}>{aliasKindLabel(alias.kind)}</span>
                    <span>{alias.name}</span>
                    <span className={styles.aliasGames}>{alias.games.toLocaleString()}</span>
                  </Link>
                </li>
              ))}
            </ul>
          </div>
        ) : null}
      </div>
    </Panel>
  );
}

function AdminFideLinkPanel({
  playerName,
  initialFideId,
  onLinked,
}: {
  playerName: string;
  initialFideId: string;
  onLinked: () => void;
}) {
  const [fideId, setFideId] = useState(initialFideId);
  const [provider, setProvider] = useState<'chesscom' | 'lichess' | 'fide'>('chesscom');
  const [username, setUsername] = useState(playerName);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [receipt, setReceipt] = useState<ChessFideLinkReceipt | null>(null);

  async function handleSubmit(event: React.FormEvent) {
    event.preventDefault();
    setError(null);
    setReceipt(null);
    const trimmedFide = fideId.trim();
    const trimmedUser = username.trim();
    if (!/^\d{4,12}$/.test(trimmedFide)) {
      setError('FIDE ID must be 4 to 12 digits.');
      return;
    }
    if (!trimmedUser) {
      setError('Username is required.');
      return;
    }
    setBusy(true);
    try {
      const saved = await linkChessPlayerFide({
        fide_id: trimmedFide,
        provider,
        username: trimmedUser,
      });
      setReceipt(saved);
      onLinked();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Panel title="Link to FIDE ID (Admin)">
      <form className={styles.fideLinkForm} onSubmit={(e) => void handleSubmit(e)}>
        <Muted className={styles.lede}>
          Admin curated corpus operation (<code>AcademicCurated</code>, witness <code>Laplace Curated Chess</code>).
        </Muted>
        <div className={styles.fideLinkFields}>
          <label className={styles.filterField}>
            <span>FIDE ID</span>
            <Input
              value={fideId}
              onChange={(e) => setFideId(e.target.value)}
              placeholder="1503014"
              inputMode="numeric"
              maxLength={12}
              disabled={busy}
            />
          </label>
          <label className={styles.filterField}>
            <span>Provider</span>
            <Select
              value={provider}
              onChange={(e) => setProvider(e.target.value as 'chesscom' | 'lichess' | 'fide')}
              disabled={busy}
            >
              <option value="chesscom">Chess.com</option>
              <option value="lichess">Lichess</option>
              <option value="fide">FIDE alias</option>
            </Select>
          </label>
          <label className={styles.filterField}>
            <span>Username / alias</span>
            <Input
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              placeholder={playerName}
              maxLength={256}
              disabled={busy}
            />
          </label>
          <Button type="submit" loading={busy} disabled={busy}>
            Save &amp; witness link
          </Button>
        </div>
        {error ? <ErrorText role="alert">{error}</ErrorText> : null}
        {receipt ? (
          <Alert variant="success">
            Linked FIDE <strong>{receipt.fide_id}</strong> ↔ <strong>{receipt.provider}:{receipt.username}</strong>{' '}
            (witnessed by {receipt.witness} at {receipt.trust_class}).
          </Alert>
        ) : null}
      </form>
    </Panel>
  );
}
