import { useState } from 'react';
import {
  Badge,
  Button,
  Chip,
  ErrorText,
  Field,
  LoadingText,
  Panel,
  Select,
  TextArea,
} from '@ui';
import { describeFailure } from '../api/client';
import { FIRMWARE_PROFILES, type FirmwareProfile } from '../api/forwardProof';
import { useAppStore } from '../store';
import { arenaBlindMatchup, type ArenaBlindMatchupResponse } from './api';
import { EntityIdLink, ShareBreakdown } from './ArenaShared';
import styles from './ArenaView.module.css';

export function BlindMatchupWorkspace() {
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
