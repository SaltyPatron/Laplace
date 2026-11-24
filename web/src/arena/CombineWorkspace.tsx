import { useState } from 'react';
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
import { arenaCombine, type ArenaCombineResponse } from './api';
import { EntityIdLink, ShareBreakdown } from './ArenaShared';
import styles from './ArenaView.module.css';

export function CombineWorkspace() {
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
