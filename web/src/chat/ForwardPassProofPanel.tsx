import {
  SHARE_BP_TOTAL,
  summarizeWitnessShares,
  type AuthorityScopeReceipt,
  type ForwardPassProof,
} from '../api/forwardProof';
import styles from './ForwardPassProofPanel.module.css';
import { Chip, Muted, Table } from '@ui';

function compact(value: string | null | undefined): string {
  if (!value) return '—';
  return value.length <= 24 ? value : `${value.slice(0, 10)}…${value.slice(-10)}`;
}

function formatWallUs(wallUs: number | null | undefined): string {
  if (wallUs == null || !Number.isFinite(wallUs)) return '—';
  return wallUs >= 1000 ? `${(wallUs / 1000).toFixed(2)} ms (${wallUs.toLocaleString()} µs)` : `${wallUs.toLocaleString()} µs`;
}

function formatScope(scope: string | AuthorityScopeReceipt | null | undefined): string {
  if (!scope) return 'Workspace default';
  if (typeof scope === 'string') return scope;
  return [scope.mode, scope.tenant, scope.principal, scope.witness_count != null ? `${scope.witness_count} witnesses` : null]
    .filter(Boolean)
    .join(' · ') || 'Workspace default';
}

/** The receipt of the turn that produced this response, as the Engine wrote it, and the Engine's trace. */
export function ForwardPassProofPanel({ proof }: { proof: ForwardPassProof }) {
  const shares = proof.witness_shares ?? [];
  const shareSummary = summarizeWitnessShares(shares);
  const wallUs = proof.work?.wall_us ?? proof.wall_us;
  const dbProbes = proof.work?.db_probes ?? proof.db_probes;
  const oodaRounds = proof.ooda_rounds_run;

  return (
    <details open className={styles.proof} data-forward-pass-proof="ordinary-chat-turn">
      <summary>
        Forward pass proof
        <span>{proof.disposition || 'no disposition'}</span>
        {proof.firmware && <span>firmware: {proof.firmware}</span>}
        <span>exact producing turn · no replay</span>
        <span>{proof.witnessed ? `turn ${proof.ordinal} witnessed` : 'not witnessed'}</span>
        {proof.top_salience != null && <span>top salience {Number(proof.top_salience).toFixed(2)}</span>}
        {dbProbes != null && <span>{dbProbes.toLocaleString()} DB probes</span>}
        {wallUs != null && <span>{formatWallUs(wallUs)}</span>}
        {oodaRounds != null && <span>{oodaRounds} OODA round{oodaRounds === 1 ? '' : 's'}</span>}
        <span>{proof.steps.length} steps</span>
        <span>{proof.turns_before} earlier turns</span>
      </summary>

      <div className={styles.body}>
        <div className={styles.law}>
          RESOLVE → COUPLE → ORIENT → ROUTE → SCAN → COMPOSE → PROPOSE → STEER → SELECT → REALIZE → WITNESS
        </div>

        <dl className={styles.receipt}>
          <div><dt>disposition</dt><dd><code>{proof.disposition || '—'}</code></dd></div>
          <div><dt>firmware</dt><dd><code>{proof.firmware ?? 'program'}</code>{proof.firmware_id ? <> · <code title={proof.firmware_id}>{compact(proof.firmware_id)}</code></> : null}</dd></div>
          <div><dt>compute envelope</dt><dd><code>{proof.envelope ?? 'standard'}</code></dd></div>
          <div><dt>authority scope</dt><dd>{formatScope(proof.authority_scope)}</dd></div>
          <div><dt>session</dt><dd><code>{proof.session}</code> of <code>{proof.participant}</code></dd></div>
          <div><dt>prompt</dt><dd><code title={proof.prompt_id}>{compact(proof.prompt_id)}</code></dd></div>
          <div><dt>response</dt><dd><code title={proof.response_id}>{compact(proof.response_id)}</code></dd></div>
          <div><dt>turn</dt><dd><code title={proof.turn_id}>{compact(proof.turn_id)}</code></dd></div>
          <div><dt>place in the session</dt><dd>{proof.ordinal} · {proof.turns_before} before it</dd></div>
          <div><dt>witness</dt><dd>{proof.witnessed ? 'recorded by the Engine before this receipt was returned' : proof.reason ?? 'not recorded'}</dd></div>
        </dl>

        <section>
          <h4>OODA, Salience &amp; Work Counters</h4>
          <dl className={styles.metricsGrid}>
            <div><dt>top salience</dt><dd>{proof.top_salience != null ? Number(proof.top_salience).toFixed(3) : '—'}</dd></div>
            <div><dt>mean confidence</dt><dd>{proof.mean_confidence != null ? Number(proof.mean_confidence).toFixed(3) : '—'}</dd></div>
            <div><dt>DB probes</dt><dd>{dbProbes != null ? dbProbes.toLocaleString() : '—'}</dd></div>
            <div><dt>wall time (wall_us)</dt><dd>{formatWallUs(wallUs)}</dd></div>
            <div><dt>OODA rounds</dt><dd>{oodaRounds != null ? oodaRounds : '—'}</dd></div>
            <div><dt>OODA verified / unverified</dt><dd>{proof.ooda_verified != null || proof.ooda_unverified != null ? `${proof.ooda_verified ?? 0} / ${proof.ooda_unverified ?? 0}` : '—'}</dd></div>
            <div><dt>rows scanned</dt><dd>{proof.work?.rows_scanned != null ? proof.work.rows_scanned.toLocaleString() : '—'}</dd></div>
            <div><dt>hops / tokens out</dt><dd>{proof.work?.hops != null || proof.work?.tokens_out != null ? `${proof.work?.hops ?? '—'} hops · ${proof.work?.tokens_out ?? '—'} tokens` : '—'}</dd></div>
            {proof.culprit_id && (
              <div><dt>culprit contrast</dt><dd><code title={proof.culprit_id}>{compact(proof.culprit_id)}</code>{proof.culprit_contrast != null ? ` (${proof.culprit_contrast.toFixed(3)})` : ''}</dd></div>
            )}
          </dl>
        </section>

        <section>
          <div className={styles.sectionHeader}>
            <h4>Witness Attribution &amp; Fisher share_bp Conservation</h4>
            {shares.length > 0 && (
              <Chip variant={shareSummary.conserved ? 'engineOk' : 'engineMissing'}>
                Σ {shareSummary.sumBp} / {SHARE_BP_TOTAL} bp {shareSummary.conserved ? '(conserved)' : '(mismatch)'}
              </Chip>
            )}
          </div>
          {shares.length === 0 ? (
            <Muted className={styles.empty}>No per-witness Fisher share_bp attribution returned on this turn.</Muted>
          ) : (
            <div className={styles.tableWrap}>
              <Table>
                <thead>
                  <tr>
                    <th>witness</th>
                    <th>label</th>
                    <th>trust</th>
                    <th>Fisher I(e)</th>
                    <th>games</th>
                    <th>share_bp</th>
                    <th>share %</th>
                  </tr>
                </thead>
                <tbody>
                  {shares.map((item, idx) => (
                    <tr key={`${item.witness}:${idx}`}>
                      <td title={item.witness}><code>{compact(item.witness)}</code></td>
                      <td>{item.label ?? '—'}</td>
                      <td>{item.trust != null ? Number(item.trust).toFixed(3) : '—'}</td>
                      <td>{item.fisher_info != null ? Number(item.fisher_info).toFixed(3) : '—'}</td>
                      <td>{item.games != null ? Number(item.games).toLocaleString() : '—'}</td>
                      <td>{item.share_bp}</td>
                      <td>{((Number(item.share_bp) / SHARE_BP_TOTAL) * 100).toFixed(2)}%</td>
                    </tr>
                  ))}
                </tbody>
              </Table>
            </div>
          )}
        </section>

        <section>
          <h4>Steps</h4>
          {proof.steps.length === 0 ? (
            <p className={styles.empty}>Nothing was emitted.</p>
          ) : (
            <div className={styles.tableWrap}>
              <Table>
                <thead>
                  <tr><th>step</th><th>kind</th><th>emitted</th><th>grounds</th><th>continuity</th><th>confidence</th></tr>
                </thead>
                <tbody>
                  {proof.steps.map((step) => (
                    <tr key={step.step}>
                      <td>{step.step}</td>
                      <td>{step.kind}</td>
                      <td title={step.id}>{step.text}</td>
                      <td>{step.grounds.toFixed(2)}</td>
                      <td>{step.continuity}</td>
                      <td>{step.confidence.toFixed(3)}</td>
                    </tr>
                  ))}
                </tbody>
              </Table>
            </div>
          )}
        </section>

        <section>
          <h4>The Engine's trace of this turn</h4>
          <pre className={styles.trace}>{proof.trace}</pre>
        </section>
      </div>
    </details>
  );
}
