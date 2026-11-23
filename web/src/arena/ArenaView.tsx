import { useEffect, useState } from 'react';
import { Link as RouterLink } from 'react-router-dom';
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
  Select,
  Tab,
  TabList,
  Table,
  TableScroll,
  Td,
  TextArea,
  Th,
} from '@ui';
import { describeFailure } from '../api/client';
import { FIRMWARE_PROFILES, SHARE_BP_TOTAL, type FirmwareProfile } from '../api/forwardProof';
import { useAppStore } from '../store';
import {
  arenaBlindMatchup,
  arenaChallenge,
  arenaCombine,
  arenaHighwayStep,
  arenaVerify,
  type ArenaBlindMatchupResponse,
  type ArenaChallengeResponse,
  type ArenaCombineResponse,
  type ArenaEntityRef,
  type ArenaHighwayStepResponse,
  type ArenaVerifyResponse,
  type ArenaWitnessShareView,
} from './api';
import styles from './ArenaView.module.css';

type ArenaTab = 'combine' | 'highway_race' | 'graphle' | 'provenance_detective' | 'blind_matchup';

const ARENA_TABS: { id: ArenaTab; label: string; summary: string }[] = [
  { id: 'combine', label: 'Craft (COMBINE)', summary: 'Constructive discovery, Merkle pairing, 4D midpoint, and witnessed bridge strands' },
  { id: 'highway_race', label: 'Highway Race', summary: 'Live hop-by-hop traversal from Start to Target across attested mesh neighbors' },
  { id: 'graphle', label: 'Graphle', summary: 'Spoiler-free deduction from tier, 64-bit highway mask, and 4D quadrant clues' },
  { id: 'provenance_detective', label: 'Provenance Detective', summary: 'Identify the true highest-standing witness behind an attested claim' },
  { id: 'blind_matchup', label: 'Blind Matchup', summary: 'Anonymized head-to-head of two Laplace firmware profiles with OODA reveal' },
];

function todaySeed(): string {
  return new Date().toISOString().slice(0, 10).replace(/-/g, '');
}

function EntityIdLink({ idHex, label }: { idHex: string; label?: string | null }) {
  const clean = idHex.toLowerCase();
  const isHex = /^[0-9a-f]{32}$/.test(clean);
  if (!isHex) {
    return <code>{label || clean}</code>;
  }
  return (
    <RouterLink className={styles.entityLink} to={`/explore/entity/${clean}`} title={clean}>
      {label ? <span>{label}</span> : null}
      <code className={styles.hex}>{clean}</code>
    </RouterLink>
  );
}

function ShareBreakdown({ shares, totalBp }: { shares: ArenaWitnessShareView[]; totalBp: number }) {
  if (shares.length === 0) {
    return <Muted>No witness share attribution recorded for this result.</Muted>;
  }
  const conserved = totalBp === SHARE_BP_TOTAL;
  return (
    <div className={styles.stack}>
      <div className={styles.rowHeader}>
        <strong>Witness Attribution &amp; Fisher share_bp</strong>
        <Chip variant={conserved ? 'engineOk' : 'engineMissing'}>
          Σ {totalBp} / {SHARE_BP_TOTAL} bp {conserved ? '(conserved)' : '(partial)'}
        </Chip>
      </div>
      <TableScroll>
        <Table>
          <thead>
            <tr>
              <Th scope="col">Witness ID</Th>
              <Th scope="col">Label</Th>
              <Th scope="col">Trust</Th>
              <Th scope="col">Fisher I(e)</Th>
              <Th scope="col">Games</Th>
              <Th scope="col">share_bp</Th>
              <Th scope="col">Share %</Th>
            </tr>
          </thead>
          <tbody>
            {shares.map((s) => (
              <tr key={`${s.witness_id}:${s.label}`}>
                <Td><EntityIdLink idHex={s.witness_id} /></Td>
                <Td>{s.label}</Td>
                <Td className={styles.num}>{s.trust.toFixed(3)}</Td>
                <Td className={styles.num}>{s.fisher_info.toFixed(2)}</Td>
                <Td className={styles.num}>{s.games.toLocaleString()}</Td>
                <Td className={styles.num}><code>{s.share_bp}</code></Td>
                <Td className={styles.num}>{((s.share_bp / SHARE_BP_TOTAL) * 100).toFixed(2)}%</Td>
              </tr>
            ))}
          </tbody>
        </Table>
      </TableScroll>
    </div>
  );
}

export function ArenaView() {
  const [tab, setTab] = useState<ArenaTab>('combine');
  const activeTab = ARENA_TABS.find((t) => t.id === tab) ?? ARENA_TABS[0];

  return (
    <div className={styles.page}>
      <header className={styles.hero}>
        <div>
          <h2>Knowledge Arena &amp; Constructive Discovery</h2>
          <Muted>{activeTab.summary}</Muted>
        </div>
        <Badge>Live substrate · 4D geometry · Fisher consensus</Badge>
      </header>

      <TabList className={styles.tabList} aria-label="Knowledge Arena modes">
        {ARENA_TABS.map((item) => (
          <Tab
            key={item.id}
            id={`arena-tab-${item.id}`}
            aria-controls={`arena-panel-${item.id}`}
            selected={tab === item.id}
            className={`${styles.tabButton} ${tab === item.id ? styles.tabActive : ''}`}
            onClick={() => setTab(item.id)}
          >
            {item.label}
          </Tab>
        ))}
      </TabList>

      <div id={`arena-panel-${tab}`} role="tabpanel" aria-labelledby={`arena-tab-${tab}`} className={styles.panelWrap}>
        {tab === 'combine' && <CombineWorkspace />}
        {tab === 'highway_race' && <HighwayRaceWorkspace />}
        {tab === 'graphle' && <GraphleWorkspace />}
        {tab === 'provenance_detective' && <ProvenanceDetectiveWorkspace />}
        {tab === 'blind_matchup' && <BlindMatchupWorkspace />}
      </div>
    </div>
  );
}

function CombineWorkspace() {
  const { tenant, quoteId } = useAppStore();
  const [a, setA] = useState('whale');
  const [b, setB] = useState('cetacean');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [result, setResult] = useState<ArenaCombineResponse | null>(null);

  async function submit() {
    if (!a.trim() || !b.trim() || busy) return;
    setBusy(true);
    setErr(null);
    try {
      const data = await arenaCombine(a.trim(), b.trim(), { tenant, quoteId });
      setResult(data);
    } catch (e) {
      setErr(describeFailure(e).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className={styles.stack}>
      <Panel title="Constructive Discovery — COMBINE(A, B)">
        <form className={styles.inlineForm} onSubmit={(e) => { e.preventDefault(); void submit(); }}>
          <Field label="Concept A (text or 32-hex ID)">
            <Input value={a} onChange={(e) => setA(e.target.value)} placeholder="whale" />
          </Field>
          <Field label="Concept B (text or 32-hex ID)">
            <Input value={b} onChange={(e) => setB(e.target.value)} placeholder="cetacean" />
          </Field>
          <div className={styles.formSubmit}>
            <Button type="submit" loading={busy} disabled={busy || !a.trim() || !b.trim()}>
              Combine A + B
            </Button>
          </div>
        </form>
        {err && <ErrorText role="alert">{err}</ErrorText>}
      </Panel>

      {busy && !result && <LoadingText>Decomposing Merkle trees and probing witnessed bridges…</LoadingText>}

      {result && (
        <div className={styles.stack}>
          <div className={styles.grid2}>
            <Panel title={`Side A · ${result.side_a.label}`}>
              <div className={styles.stack}>
                <EntityIdLink idHex={result.side_a.id_hex} />
                <div className={styles.chipRow}>
                  <Chip variant={result.side_a.witnessed ? 'confirm' : 'engineMissing'}>
                    {result.side_a.witnessed ? 'Witnessed' : 'Unwitnessed (novel anchor)'}
                  </Chip>
                  {result.side_a.tier != null && <Badge>tier {result.side_a.tier}</Badge>}
                  {result.side_a.hub_type && <Badge>{result.side_a.hub_type}</Badge>}
                </div>
                <div className={styles.monoLine}>
                  coord 4D: [{result.side_a.coord.map((v) => v.toFixed(5)).join(', ')}]
                </div>
                {result.side_a.record && (
                  <div className={styles.chipRow}>
                    <Chip variant="confirm">{result.side_a.record.confirmed} confirmed</Chip>
                    <Chip variant="draw">{result.side_a.record.contested} contested</Chip>
                    <Chip variant="refute">{result.side_a.record.refuted} refuted</Chip>
                    <Badge>{result.side_a.record.thin} thin</Badge>
                  </div>
                )}
              </div>
            </Panel>

            <Panel title={`Side B · ${result.side_b.label}`}>
              <div className={styles.stack}>
                <EntityIdLink idHex={result.side_b.id_hex} />
                <div className={styles.chipRow}>
                  <Chip variant={result.side_b.witnessed ? 'confirm' : 'engineMissing'}>
                    {result.side_b.witnessed ? 'Witnessed' : 'Unwitnessed (novel anchor)'}
                  </Chip>
                  {result.side_b.tier != null && <Badge>tier {result.side_b.tier}</Badge>}
                  {result.side_b.hub_type && <Badge>{result.side_b.hub_type}</Badge>}
                </div>
                <div className={styles.monoLine}>
                  coord 4D: [{result.side_b.coord.map((v) => v.toFixed(5)).join(', ')}]
                </div>
                {result.side_b.record && (
                  <div className={styles.chipRow}>
                    <Chip variant="confirm">{result.side_b.record.confirmed} confirmed</Chip>
                    <Chip variant="draw">{result.side_b.record.contested} contested</Chip>
                    <Chip variant="refute">{result.side_b.record.refuted} refuted</Chip>
                    <Badge>{result.side_b.record.thin} thin</Badge>
                  </div>
                )}
              </div>
            </Panel>
          </div>

          <Panel title="Merkle Composition &amp; 4D Geometric Midpoint">
            <dl className={styles.receipt}>
              <div>
                <dt>Novel Merkle Pair ID</dt>
                <dd><EntityIdLink idHex={result.novel_merkle_id} /></dd>
              </div>
              <div>
                <dt>4D Midpoint Centroid</dt>
                <dd><code>[{result.midpoint_4d.map((v) => v.toFixed(6)).join(', ')}]</code></dd>
              </div>
              <div>
                <dt>Midpoint 4-Ball Radius</dt>
                <dd><code>{result.midpoint_radius.toFixed(6)}</code></dd>
              </div>
              <div>
                <dt>4D Separation Distance</dt>
                <dd><code>{result.distance_4d.toFixed(6)}</code></dd>
              </div>
            </dl>
            {result.synthesis_summary && (
              <div className={styles.synthesisBox}>
                <div className={styles.rowHeader}>
                  <strong>Constructive Firmware Synthesis (Read-Only)</strong>
                  {result.synthesis_firmware && <Badge>{result.synthesis_firmware}</Badge>}
                </div>
                <p>{result.synthesis_summary}</p>
              </div>
            )}
          </Panel>

          <Panel title={`Witnessed Bridge Strands (${result.bridges.length})`}>
            {result.bridges.length === 0 ? (
              <Muted>No intersecting bridge entities witnessed yet between A and B.</Muted>
            ) : (
              <TableScroll>
                <Table>
                  <thead>
                    <tr>
                      <Th scope="col">Bridge Entity</Th>
                      <Th scope="col">Hop Strand</Th>
                      <Th scope="col">Standing</Th>
                      <Th scope="col">Source</Th>
                    </tr>
                  </thead>
                  <tbody>
                    {result.bridges.map((br) => (
                      <tr key={`${br.bridge_id}:${br.relation_a}:${br.relation_b}`}>
                        <Td><EntityIdLink idHex={br.bridge_id} label={br.bridge_label} /></Td>
                        <Td>
                          <span className={styles.strand}>
                            <span>{result.side_a.label}</span>
                            <code className={styles.relTag}>—{br.relation_a}→</code>
                            <strong>{br.bridge_label}</strong>
                            <code className={styles.relTag}>←{br.relation_b}—</code>
                            <span>{result.side_b.label}</span>
                          </span>
                        </Td>
                        <Td><ConsensusBadge mu={br.eff_mu} witnesses={br.witnesses} tone="explore" /></Td>
                        <Td><Badge>{br.source}</Badge></Td>
                      </tr>
                    ))}
                  </tbody>
                </Table>
              </TableScroll>
            )}
          </Panel>

          {result.shared_tape.length > 0 && (
            <Panel title={`Shared Matchup Tape (${result.shared_tape.length})`}>
              <ul className={styles.plainList}>
                {result.shared_tape.map((t, i) => (
                  <li key={`${t.type}:${t.fact}:${i}`}>
                    <Badge>{t.type}</Badge> <span>{t.fact}</span>{' '}
                    {t.eff_mu != null && <ConsensusBadge mu={Number(t.eff_mu)} tone="explore" />}
                  </li>
                ))}
              </ul>
            </Panel>
          )}

          <Panel title="Source Attribution">
            <ShareBreakdown shares={result.shares} totalBp={result.shares_total_bp} />
          </Panel>
        </div>
      )}
    </div>
  );
}

function HighwayRaceWorkspace() {
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

function GraphleWorkspace() {
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

function ProvenanceDetectiveWorkspace() {
  const { tenant, quoteId } = useAppStore();
  const [seed, setSeed] = useState(todaySeed);
  const [selectedWitness, setSelectedWitness] = useState('');
  const [busy, setBusy] = useState(false);
  const [verifying, setVerifying] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [challenge, setChallenge] = useState<ArenaChallengeResponse | null>(null);
  const [verdict, setVerdict] = useState<ArenaVerifyResponse | null>(null);
  const [revealOnly, setRevealOnly] = useState(false);

  async function loadChallenge(chosenSeed = seed) {
    setBusy(true);
    setErr(null);
    setVerdict(null);
    setRevealOnly(false);
    setSelectedWitness('');
    try {
      const ch = await arenaChallenge('provenance_detective', chosenSeed, { tenant, quoteId });
      setChallenge(ch);
      if (ch.detective?.candidates.length) {
        setSelectedWitness(ch.detective.candidates[0].witness_id);
      }
    } catch (e) {
      setErr(describeFailure(e).message);
    } finally {
      setBusy(false);
    }
  }

  useEffect(() => {
    void loadChallenge(seed);
    // Load initial Provenance Detective case once on mount.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function submitVerdict(reveal = false) {
    if (verifying || !challenge) return;
    if (!reveal && !selectedWitness) return;
    setVerifying(true);
    setErr(null);
    try {
      const res = await arenaVerify(
        {
          mode: 'provenance_detective',
          seed: challenge.seed,
          guess: reveal ? '' : selectedWitness,
          reveal: true,
        },
        { tenant, quoteId },
      );
      setVerdict(res);
      setRevealOnly(reveal);
    } catch (e) {
      setErr(describeFailure(e).message);
    } finally {
      setVerifying(false);
    }
  }

  const clue = challenge?.detective;

  return (
    <div className={styles.stack}>
      <Panel title="Provenance Detective">
        <div className={styles.inlineForm}>
          <Field label="Deterministic Seed">
            <Input value={seed} onChange={(e) => setSeed(e.target.value)} placeholder="YYYYMMDD" />
          </Field>
          <div className={styles.formSubmit}>
            <Button onClick={() => void loadChallenge(seed)} loading={busy} disabled={busy}>
              Load Case
            </Button>
          </div>
        </div>
        {challenge && <Muted>{challenge.instructions}</Muted>}
        {err && <ErrorText role="alert">{err}</ErrorText>}
      </Panel>

      {busy && !clue && <LoadingText>Collecting attested claims and candidate witnesses…</LoadingText>}

      {challenge && clue && (
        <div className={styles.stack}>
          <Panel title="Attested Claim Under Investigation">
            <div className={styles.stack}>
              <EntityIdLink idHex={clue.claim_entity_id} label={clue.claim_entity_label} />
              <p className={styles.claimText}>{clue.claim_summary}</p>
              {clue.record && (
                <div className={styles.chipRow}>
                  <Chip variant="confirm">{clue.record.confirmed} confirmed</Chip>
                  <Chip variant="draw">{clue.record.contested} contested</Chip>
                  <Chip variant="refute">{clue.record.refuted} refuted</Chip>
                  <Badge>{clue.record.thin} thin</Badge>
                </div>
              )}
            </div>
          </Panel>

          <Panel title="Candidate Witnesses">
            <div className={styles.candidateList}>
              {clue.candidates.map((cand) => {
                const picked = selectedWitness === cand.witness_id;
                return (
                  <label key={cand.witness_id} className={`${styles.candidateCard} ${picked ? styles.candidatePicked : ''}`}>
                    <input
                      type="radio"
                      name="detective-witness"
                      value={cand.witness_id}
                      checked={picked}
                      onChange={() => setSelectedWitness(cand.witness_id)}
                    />
                    <div className={styles.candidateBody}>
                      <EntityIdLink idHex={cand.witness_id} label={cand.label} />
                      <Badge>{cand.kind}</Badge>
                    </div>
                  </label>
                );
              })}
            </div>
            <div className={styles.actionsRow}>
              <Button
                loading={verifying}
                disabled={verifying || !selectedWitness}
                onClick={() => void submitVerdict(false)}
              >
                Submit Verdict
              </Button>
              <Button variant="ghost" disabled={verifying} onClick={() => void submitVerdict(true)}>
                Reveal Highest-Standing Witness
              </Button>
            </div>
          </Panel>

          {verdict && (
            <Panel title="Provenance Verdict">
              <div className={styles.stack}>
                <div className={styles.chipRow}>
                  <Chip variant={verdict.correct ? 'engineOk' : revealOnly ? 'draw' : 'refute'}>
                    {verdict.correct
                      ? 'Correct witness identified!'
                      : revealOnly
                        ? 'Highest-standing witness revealed'
                        : 'Incorrect witness'}
                  </Chip>
                  <Badge>consensus rating {verdict.consensus_rating.toFixed(2)}</Badge>
                </div>
                {verdict.target_id && (
                  <div>
                    <strong>True highest-standing witness: </strong>
                    <EntityIdLink idHex={verdict.target_id} label={verdict.target_label} />
                  </div>
                )}
                <ShareBreakdown shares={verdict.shares} totalBp={verdict.shares_total_bp} />
              </div>
            </Panel>
          )}
        </div>
      )}
    </div>
  );
}

function BlindMatchupWorkspace() {
  const { tenant, quoteId } = useAppStore();
  const [prompt, setPrompt] = useState('Explain how consensus standing and 4D trajectories compose meaning.');
  const [firmwareA, setFirmwareA] = useState<FirmwareProfile>('deductive');
  const [firmwareB, setFirmwareB] = useState<FirmwareProfile>('constructive');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [matchup, setMatchup] = useState<ArenaBlindMatchupResponse | null>(null);
  const [vote, setVote] = useState<'left' | 'right' | 'tie' | 'revealed' | null>(null);

  async function runMatchup() {
    if (!prompt.trim() || busy) return;
    setBusy(true);
    setErr(null);
    setVote(null);
    try {
      const res = await arenaBlindMatchup(
        { prompt: prompt.trim(), firmware_a: firmwareA, firmware_b: firmwareB },
        { tenant, quoteId },
      );
      setMatchup(res);
    } catch (e) {
      setErr(describeFailure(e).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className={styles.stack}>
      <Panel title="Blind Firmware Matchup (Read-Only OODA)">
        <div className={styles.stack}>
          <div className={styles.row2}>
            <Field label="Firmware Profile A">
              <Select
                aria-label="Firmware Profile A"
                value={firmwareA}
                onChange={(e) => setFirmwareA(e.target.value as FirmwareProfile)}
              >
                {FIRMWARE_PROFILES.map((fw) => (
                  <option key={fw.id} value={fw.id}>{fw.label} — {fw.summary}</option>
                ))}
              </Select>
            </Field>
            <Field label="Firmware Profile B">
              <Select
                aria-label="Firmware Profile B"
                value={firmwareB}
                onChange={(e) => setFirmwareB(e.target.value as FirmwareProfile)}
              >
                {FIRMWARE_PROFILES.map((fw) => (
                  <option key={fw.id} value={fw.id}>{fw.label} — {fw.summary}</option>
                ))}
              </Select>
            </Field>
          </div>
          <Field label="Prompt">
            <TextArea
              rows={3}
              value={prompt}
              onChange={(e) => setPrompt(e.target.value)}
              placeholder="Enter prompt for blind side-by-side firmware evaluation…"
            />
          </Field>
          <div className={styles.actionsRow}>
            <Button loading={busy} disabled={busy || !prompt.trim()} onClick={() => void runMatchup()}>
              Run Blind Matchup
            </Button>
          </div>
          {err && <ErrorText role="alert">{err}</ErrorText>}
        </div>
      </Panel>

      {busy && !matchup && <LoadingText>Running both firmwares read-only in randomized Left/Right slots…</LoadingText>}

      {matchup && (
        <div className={styles.stack}>
          <div className={styles.grid2}>
            <Panel title={vote ? `Slot Left · ${matchup.reveal_left.firmware}` : 'Slot Left (Anonymized)'}>
              <div className={styles.stack}>
                <div className={styles.chipRow}>
                  <Badge>salience {matchup.left.top_salience.toFixed(3)}</Badge>
                  <Badge>conf {matchup.left.mean_confidence.toFixed(3)}</Badge>
                  <Badge>{matchup.left.wall_us.toLocaleString()} µs</Badge>
                  <Badge>{matchup.left.db_probes} probes</Badge>
                  <Badge>{matchup.left.ooda_rounds_run} OODA</Badge>
                </div>
                <pre className={styles.responseBox}>{matchup.left.response || '(no tokens emitted)'}</pre>
                <div className={styles.monoLine}>
                  prompt: <EntityIdLink idHex={matchup.left.prompt_id} />
                  {matchup.left.response_id ? <> · response: <EntityIdLink idHex={matchup.left.response_id} /></> : null}
                </div>
              </div>
            </Panel>

            <Panel title={vote ? `Slot Right · ${matchup.reveal_right.firmware}` : 'Slot Right (Anonymized)'}>
              <div className={styles.stack}>
                <div className={styles.chipRow}>
                  <Badge>salience {matchup.right.top_salience.toFixed(3)}</Badge>
                  <Badge>conf {matchup.right.mean_confidence.toFixed(3)}</Badge>
                  <Badge>{matchup.right.wall_us.toLocaleString()} µs</Badge>
                  <Badge>{matchup.right.db_probes} probes</Badge>
                  <Badge>{matchup.right.ooda_rounds_run} OODA</Badge>
                </div>
                <pre className={styles.responseBox}>{matchup.right.response || '(no tokens emitted)'}</pre>
                <div className={styles.monoLine}>
                  prompt: <EntityIdLink idHex={matchup.right.prompt_id} />
                  {matchup.right.response_id ? <> · response: <EntityIdLink idHex={matchup.right.response_id} /></> : null}
                </div>
              </div>
            </Panel>
          </div>

          <Panel title={vote ? 'Firmware & Provenance Reveal' : 'Cast Blind Vote to Reveal'}>
            {!vote ? (
              <div className={styles.actionsRow}>
                <Button onClick={() => setVote('left')}>Left is stronger</Button>
                <Button variant="ghost" onClick={() => setVote('tie')}>Tie</Button>
                <Button onClick={() => setVote('right')}>Right is stronger</Button>
                <Button variant="ghost" onClick={() => setVote('revealed')}>Reveal without voting</Button>
              </div>
            ) : (
              <div className={styles.stack}>
                <div className={styles.chipRow}>
                  <Chip variant="engineOk">
                    {vote === 'revealed' ? 'Revealed' : `Voted: ${vote}`}
                  </Chip>
                  <Badge>{matchup.swapped ? 'presentation order swapped' : 'presentation order direct'}</Badge>
                </div>
                <div className={styles.grid2}>
                  <div className={styles.stack}>
                    <div className={styles.rowHeader}>
                      <strong>Left: <code>{matchup.reveal_left.firmware}</code></strong>
                      <Badge>{matchup.reveal_left.disposition}</Badge>
                    </div>
                    {matchup.reveal_left.firmware_id && (
                      <div className={styles.monoLine}>
                        firmware_id: <EntityIdLink idHex={matchup.reveal_left.firmware_id} />
                      </div>
                    )}
                    <ShareBreakdown
                      shares={matchup.reveal_left.shares}
                      totalBp={matchup.reveal_left.shares_total_bp}
                    />
                  </div>
                  <div className={styles.stack}>
                    <div className={styles.rowHeader}>
                      <strong>Right: <code>{matchup.reveal_right.firmware}</code></strong>
                      <Badge>{matchup.reveal_right.disposition}</Badge>
                    </div>
                    {matchup.reveal_right.firmware_id && (
                      <div className={styles.monoLine}>
                        firmware_id: <EntityIdLink idHex={matchup.reveal_right.firmware_id} />
                      </div>
                    )}
                    <ShareBreakdown
                      shares={matchup.reveal_right.shares}
                      totalBp={matchup.reveal_right.shares_total_bp}
                    />
                  </div>
                </div>
              </div>
            )}
          </Panel>
        </div>
      )}
    </div>
  );
}
