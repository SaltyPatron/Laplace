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
import {
  arenaChallenge,
  arenaHighwayStep,
  type ArenaChallengeResponse,
  type ArenaEntityRef,
  type ArenaHighwayStepResponse,
} from './api';
import { EntityIdLink, ShareBreakdown, todaySeed } from './ArenaShared';
import styles from './ArenaView.module.css';

export function HighwayRaceWorkspace() {
  const { tenant, quoteId } = useAppStore();
  const [seed, setSeed] = useState(todaySeed);
  const [busy, setBusy] = useState(false);
  const [stepBusy, setStepBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [challenge, setChallenge] = useState<ArenaChallengeResponse | null>(null);
  const [current, setCurrent] = useState<ArenaEntityRef | null>(null);
  const [trail, setTrail] = useState<{ id: string; label: string; relation?: string }[]>([]);
  const [stepResult, setStepResult] = useState<ArenaHighwayStepResponse | null>(null);

  async function loadChallenge(chosenSeed = seed) {
    setBusy(true);
    setErr(null);
    setStepResult(null);
    try {
      const ch = await arenaChallenge('highway_race', chosenSeed, { tenant, quoteId });
      setChallenge(ch);
      if (ch.start) {
        setCurrent(ch.start);
        setTrail([{ id: ch.start.id, label: ch.start.label }]);
      } else {
        setCurrent(null);
        setTrail([]);
      }
    } catch (e) {
      setErr(describeFailure(e).message);
    } finally {
      setBusy(false);
    }
  }

  useEffect(() => {
    void loadChallenge(seed);
    // Load initial live challenge once on mount.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function takeHop(nextId: string, relation: string) {
    if (!current || !challenge?.target || stepBusy || stepResult?.reached_target) return;
    setStepBusy(true);
    setErr(null);
    try {
      const res = await arenaHighwayStep(
        {
          current_id: current.id,
          next_id: nextId,
          target_id: challenge.target.id,
          step_count: Math.max(0, trail.length - 1),
        },
        { tenant, quoteId },
      );
      setStepResult(res);
      if (res.valid && res.next_entity) {
        setCurrent(res.next_entity);
        setTrail((prev) => [...prev, { id: res.next_entity!.id, label: res.next_entity!.label, relation }]);
      }
    } catch (e) {
      setErr(describeFailure(e).message);
    } finally {
      setStepBusy(false);
    }
  }

  return (
    <div className={styles.stack}>
      <Panel title="Highway Race">
        <div className={styles.inlineForm}>
          <Field label="Deterministic Seed">
            <Input value={seed} onChange={(e) => setSeed(e.target.value)} placeholder="YYYYMMDD" />
          </Field>
          <div className={styles.formSubmit}>
            <Button onClick={() => void loadChallenge(seed)} loading={busy} disabled={busy}>
              Load Challenge
            </Button>
          </div>
        </div>
        {challenge && <Muted>{challenge.instructions}</Muted>}
        {err && <ErrorText role="alert">{err}</ErrorText>}
      </Panel>

      {busy && !challenge && <LoadingText>Selecting live Highway Race endpoints…</LoadingText>}

      {challenge?.start && challenge.target && (
        <div className={styles.stack}>
          <div className={styles.grid2}>
            <Panel title="Current Position">
              {current && (
                <div className={styles.stack}>
                  <EntityIdLink idHex={current.id} label={current.label} />
                  <div className={styles.chipRow}>
                    <Badge>hops taken: {Math.max(0, trail.length - 1)}</Badge>
                    {current.hub_type && <Badge>{current.hub_type}</Badge>}
                  </div>
                </div>
              )}
            </Panel>

            <Panel title="Race Target (T)">
              <div className={styles.stack}>
                <EntityIdLink idHex={challenge.target.id} label={challenge.target.label} />
                <div className={styles.chipRow}>
                  {challenge.optimal_hint_hops != null && (
                    <Badge>baseline path ~{challenge.optimal_hint_hops} hops</Badge>
                  )}
                  {challenge.target.hub_type && <Badge>{challenge.target.hub_type}</Badge>}
                </div>
              </div>
            </Panel>
          </div>

          <Panel title="Recorded Hop Strand">
            <div className={styles.strandWrap}>
              {trail.map((node, idx) => (
                <span key={`${node.id}:${idx}`} className={styles.strandItem}>
                  {idx > 0 && <code className={styles.relTag}>—{node.relation ?? 'hop'}→</code>}
                  <EntityIdLink idHex={node.id} label={node.label} />
                </span>
              ))}
            </div>
          </Panel>

          {stepResult?.reached_target && (
            <Panel title="Target Reached — Provenance &amp; Standing Receipt">
              <div className={styles.stack}>
                <Chip variant="engineOk">
                  Reached target in {stepResult.step_count} hop{stepResult.step_count === 1 ? '' : 's'}!
                </Chip>
                {stepResult.target_record && (
                  <div className={styles.chipRow}>
                    <Chip variant="confirm">{stepResult.target_record.confirmed} confirmed</Chip>
                    <Chip variant="draw">{stepResult.target_record.contested} contested</Chip>
                    <Chip variant="refute">{stepResult.target_record.refuted} refuted</Chip>
                    <Badge>{stepResult.target_record.thin} thin</Badge>
                  </div>
                )}
                <ShareBreakdown shares={stepResult.shares} totalBp={stepResult.shares_total_bp} />
              </div>
            </Panel>
          )}

          {!stepResult?.reached_target && current && (
            <Panel title={`Attested Mesh Neighbors of ${current.label} (${current.neighbors.length})`}>
              {current.neighbors.length === 0 ? (
                <Muted>No outgoing or member hops found for this entity. Try another seed.</Muted>
              ) : (
                <TableScroll>
                  <Table>
                    <thead>
                      <tr>
                        <Th scope="col">Neighbor</Th>
                        <Th scope="col">Relation</Th>
                        <Th scope="col">Standing</Th>
                        <Th scope="col">Action</Th>
                      </tr>
                    </thead>
                    <tbody>
                      {current.neighbors.map((link, idx) => {
                        const isTarget = link.id.toLowerCase() === challenge.target!.id.toLowerCase();
                        return (
                          <tr key={`${link.id}:${link.relation}:${idx}`}>
                            <Td><EntityIdLink idHex={link.id} label={link.label} /></Td>
                            <Td><Badge>{link.relation || 'mesh'}</Badge></Td>
                            <Td>
                              <ConsensusBadge
                                mu={link.eff_mu != null ? Number(link.eff_mu) : undefined}
                                witnesses={link.witnesses}
                                tone="explore"
                              />
                            </Td>
                            <Td>
                              <Button
                                size="sm"
                                variant={isTarget ? 'primary' : 'ghost'}
                                disabled={stepBusy}
                                onClick={() => void takeHop(link.id, link.relation || 'mesh')}
                              >
                                {isTarget ? 'Finish at Target' : 'Hop here'}
                              </Button>
                            </Td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </Table>
                </TableScroll>
              )}
            </Panel>
          )}
        </div>
      )}
    </div>
  );
}
