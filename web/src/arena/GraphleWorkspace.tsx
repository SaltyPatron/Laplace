import { useEffect, useState } from 'react';
import {
  Badge,
  Button,
  Chip,
  ConsensusBadge,
  ErrorText,
  Field,
  Input,
  LoadingText,
  Muted,
  Panel,
  Table,
  TableScroll,
  Td,
  Th,
} from '@ui';
import { describeFailure } from '../api/client';
import { useAppStore } from '../store';
import { arenaChallenge, arenaVerify, type ArenaChallengeResponse, type ArenaVerifyResponse } from './api';
import { EntityIdLink, ShareBreakdown, todaySeed } from './ArenaShared';
import styles from './ArenaView.module.css';

export function GraphleWorkspace() {
  const { tenant, quoteId } = useAppStore();
  const [seed, setSeed] = useState(todaySeed);
  const [guess, setGuess] = useState('');
  const [busy, setBusy] = useState(false);
  const [verifying, setVerifying] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [challenge, setChallenge] = useState<ArenaChallengeResponse | null>(null);
  const [history, setHistory] = useState<{ guess: string; res: ArenaVerifyResponse }[]>([]);
  const [revealed, setRevealed] = useState<ArenaVerifyResponse | null>(null);

  async function loadChallenge(chosenSeed = seed) {
    setBusy(true);
    setErr(null);
    setHistory([]);
    setRevealed(null);
    setGuess('');
    try {
      const ch = await arenaChallenge('graphle', chosenSeed, { tenant, quoteId });
      setChallenge(ch);
    } catch (e) {
      setErr(describeFailure(e).message);
    } finally {
      setBusy(false);
    }
  }

  useEffect(() => {
    void loadChallenge(seed);
    // Load initial Graphle puzzle once on mount.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function submitGuess(revealAnswer = false) {
    if (verifying || !challenge) return;
    if (!revealAnswer && !guess.trim()) return;
    setVerifying(true);
    setErr(null);
    try {
      const res = await arenaVerify(
        {
          mode: 'graphle',
          seed: challenge.seed,
          guess: revealAnswer ? '' : guess.trim(),
          reveal: revealAnswer,
        },
        { tenant, quoteId },
      );
      if (revealAnswer || res.correct) {
        setRevealed(res);
      }
      if (!revealAnswer) {
        setHistory((prev) => [{ guess: guess.trim(), res }, ...prev]);
        setGuess('');
      }
    } catch (e) {
      setErr(describeFailure(e).message);
    } finally {
      setVerifying(false);
    }
  }

  const clues = challenge?.graphle;

  return (
    <div className={styles.stack}>
      <Panel title="Graphle — Geometric &amp; Highway Deduction">
        <div className={styles.inlineForm}>
          <Field label="Deterministic Seed">
            <Input value={seed} onChange={(e) => setSeed(e.target.value)} placeholder="YYYYMMDD" />
          </Field>
          <div className={styles.formSubmit}>
            <Button onClick={() => void loadChallenge(seed)} loading={busy} disabled={busy}>
              Load Puzzle
            </Button>
          </div>
        </div>
        {challenge && <Muted>{challenge.instructions}</Muted>}
        {err && <ErrorText role="alert">{err}</ErrorText>}
      </Panel>

      {busy && !clues && <LoadingText>Generating spoiler-free geometric clues…</LoadingText>}

      {challenge && clues && (
        <div className={styles.stack}>
          <Panel title="Progressive Substrate Clues (Spoiler-Free)">
            <dl className={styles.receipt}>
              <div>
                <dt>Challenge Token</dt>
                <dd><code>{challenge.challenge_token}</code></dd>
              </div>
              <div>
                <dt>Entity Tier &amp; Length</dt>
                <dd>
                  <code>tier {clues.tier ?? '?'}</code> · {clues.label_length} chars ·{' '}
                  <code>{clues.redacted_pattern}</code>
                </dd>
              </div>
              <div>
                <dt>64-bit Highway Mask</dt>
                <dd>
                  <code>{clues.mask_hex}</code> ({clues.highway_banks.join(', ')})
                </dd>
              </div>
              <div>
                <dt>4D Quadrant &amp; Coordinates</dt>
                <dd>
                  <code>{clues.quadrant}</code> · r4={clues.radius.toFixed(5)} · [
                  {clues.coord.map((v) => v.toFixed(4)).join(', ')}]
                </dd>
              </div>
              <div>
                <dt>Epistemic Standing</dt>
                <dd>
                  {clues.confirmed} confirmed · {clues.contested} contested · {clues.refuted} refuted ·{' '}
                  {clues.thin} thin
                </dd>
              </div>
              {clues.hub_type && (
                <div>
                  <dt>Hub Type</dt>
                  <dd><code>{clues.hub_type}</code></dd>
                </div>
              )}
            </dl>

            <div className={styles.stack}>
              <strong>Redacted Mesh Clues ({clues.redacted_neighbors.length})</strong>
              {clues.redacted_neighbors.length === 0 ? (
                <Muted>No redacted neighbors recorded on this entity.</Muted>
              ) : (
                <ul className={styles.plainList}>
                  {clues.redacted_neighbors.map((n, idx) => (
                    <li key={`${n.relation}:${idx}`}>
                      <Badge>{n.relation}</Badge> <span>{n.redacted_label}</span>{' '}
                      <ConsensusBadge
                        mu={n.eff_mu != null ? Number(n.eff_mu) : undefined}
                        witnesses={n.witnesses}
                        tone="explore"
                      />
                    </li>
                  ))}
                </ul>
              )}
            </div>
          </Panel>

          <Panel title="Submit Guess">
            <form
              className={styles.inlineForm}
              onSubmit={(e) => {
                e.preventDefault();
                void submitGuess(false);
              }}
            >
              <Field label="Your Guess (concept label or 32-hex ID)">
                <Input
                  value={guess}
                  disabled={verifying || !!revealed}
                  onChange={(e) => setGuess(e.target.value)}
                  placeholder="Enter entity label or 32-hex BLAKE3 ID…"
                />
              </Field>
              <div className={styles.formSubmit}>
                <Button type="submit" loading={verifying} disabled={verifying || !guess.trim() || !!revealed}>
                  Verify Guess
                </Button>
                <Button
                  type="button"
                  variant="ghost"
                  disabled={verifying || !!revealed}
                  onClick={() => void submitGuess(true)}
                >
                  Give up &amp; Reveal
                </Button>
              </div>
            </form>

            {history.length > 0 && (
              <TableScroll>
                <Table>
                  <thead>
                    <tr>
                      <Th scope="col">Guess</Th>
                      <Th scope="col">Guess ID</Th>
                      <Th scope="col">4D Distance</Th>
                      <Th scope="col">Verdict</Th>
                    </tr>
                  </thead>
                  <tbody>
                    {history.map((item, idx) => (
                      <tr key={`${item.guess}:${idx}`}>
                        <Td>{item.guess}</Td>
                        <Td>
                          {item.res.guess_id ? <EntityIdLink idHex={item.res.guess_id} /> : <span>—</span>}
                        </Td>
                        <Td className={styles.num}>
                          {item.res.distance_4d != null ? item.res.distance_4d.toFixed(5) : '—'}
                        </Td>
                        <Td>
                          <Chip variant={item.res.correct ? 'confirm' : 'draw'}>
                            {item.res.correct ? 'Exact match!' : 'Try again'}
                          </Chip>
                        </Td>
                      </tr>
                    ))}
                  </tbody>
                </Table>
              </TableScroll>
            )}
          </Panel>

          {revealed && (
            <Panel title="Graphle Reveal">
              <div className={styles.stack}>
                <div className={styles.chipRow}>
                  <Chip variant={revealed.correct ? 'engineOk' : 'draw'}>
                    {revealed.correct ? 'Solved!' : 'Revealed'}
                  </Chip>
                  <Badge>consensus rating {revealed.consensus_rating.toFixed(2)}</Badge>
                </div>
                {revealed.target_id && (
                  <EntityIdLink idHex={revealed.target_id} label={revealed.target_label} />
                )}
                <ShareBreakdown shares={revealed.shares} totalBp={revealed.shares_total_bp} />
              </div>
            </Panel>
          )}
        </div>
      )}
    </div>
  );
}
