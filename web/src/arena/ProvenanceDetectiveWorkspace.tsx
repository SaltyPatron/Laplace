import { useEffect, useState } from 'react';
import {
  Badge,
  Button,
  Chip,
  ErrorText,
  Field,
  Input,
  LoadingText,
  Muted,
  Panel,
  UnstyledButton,
} from '@ui';
import { describeFailure } from '../api/client';
import { useAppStore } from '../store';
import { arenaChallenge, arenaVerify, type ArenaChallengeResponse, type ArenaVerifyResponse } from './api';
import { EntityIdLink, ShareBreakdown, todaySeed } from './ArenaShared';
import styles from './ArenaView.module.css';

export function ProvenanceDetectiveWorkspace() {
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
            <div className={styles.candidateList} role="radiogroup" aria-label="Candidate witnesses">
              {clue.candidates.map((cand) => {
                const picked = selectedWitness === cand.witness_id;
                return (
                  <UnstyledButton
                    key={cand.witness_id}
                    type="button"
                    role="radio"
                    aria-checked={picked}
                    className={`${styles.candidateCard} ${picked ? styles.candidatePicked : ''}`}
                    onClick={() => setSelectedWitness(cand.witness_id)}
                  >
                    <div className={styles.candidateBody}>
                      <EntityIdLink idHex={cand.witness_id} label={cand.label} />
                      <Badge>{cand.kind}</Badge>
                    </div>
                  </UnstyledButton>
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
                Reveal Primary Witness
              </Button>
            </div>
          </Panel>

          {verdict && (
            <Panel title="Attestation &amp; Glicko-2 Verdict">
              <div className={styles.stack}>
                <div className={styles.chipRow}>
                  <Chip variant={revealOnly ? 'draw' : verdict.correct ? 'engineOk' : 'refute'}>
                    {revealOnly ? 'Revealed' : verdict.correct ? 'Correct Witness!' : 'Incorrect Witness'}
                  </Chip>
                  <Badge>consensus rating {verdict.consensus_rating.toFixed(2)}</Badge>
                </div>
                {verdict.target_id && (
                  <EntityIdLink idHex={verdict.target_id} label={verdict.target_label} />
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
