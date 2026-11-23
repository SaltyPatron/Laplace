import { Link } from 'react-router-dom';
import {
  SHARE_BP_TOTAL,
  summarizeWitnessShares,
  type AuthorityScopeReceipt,
  type ForwardPassProof,
} from '../api/forwardProof';
import styles from './ForwardPassProofPanel.module.css';
import { Chip, Muted, Table } from '@ui';

type OodaAction = 'ANSWER' | 'CONTESTED' | 'ABSTAIN' | 'REFUSE';

interface CompatibleShare {
  witness?: string | null;
  witness_id?: string | null;
  label?: string | null;
  trust?: number | string | null;
  fisher_info?: number | string | null;
  games?: number | string | null;
  share_bp: number | string;
}

interface CompatibleProof extends Omit<ForwardPassProof, 'witness_shares'> {
  ooda?: {
    verified?: boolean | null;
    unverified?: boolean | null;
    rounds_run?: number | string | null;
    culprit_id?: string | null;
    culprit_contrast?: number | string | null;
  } | null;
  work?: (NonNullable<ForwardPassProof['work']> & {
    preset?: string | null;
    estimated_us?: number | string | null;
  }) | null;
  witness_shares?: CompatibleShare[] | null;
}

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

function resolveOodaAction(
  disposition: string | null | undefined,
  oodaUnverified: boolean | null | undefined,
  stepCount: number,
): { action: OodaAction; variant: 'engineOk' | 'draw' | 'refute' } {
  const raw = (disposition ?? '').trim();
  if (/refus|denied|blocked|forbid/i.test(raw)) {
    return { action: 'REFUSE', variant: 'refute' };
  }
  if (/abstain|unresolved|nothing emitted|no claim/i.test(raw) || (!raw && stepCount === 0)) {
    return { action: 'ABSTAIN', variant: 'draw' };
  }
  if (oodaUnverified === true || /contest|unverified|disput|ambig|tie|ask/i.test(raw)) {
    return { action: 'CONTESTED', variant: 'draw' };
  }
  return { action: 'ANSWER', variant: 'engineOk' };
}

/** The receipt of the turn that produced this response, as the Engine wrote it, and the Engine's trace. */
export function ForwardPassProofPanel({ proof }: { proof: ForwardPassProof }) {
  const compat = proof as CompatibleProof;
  const shares = compat.witness_shares ?? [];
  const normalizedShares = shares.map((item) => ({
    ...item,
    witnessHex: (item.witness_id ?? item.witness ?? '').trim(),
    bp: Number(item.share_bp) || 0,
  }));
  const shareSummary = summarizeWitnessShares(
    normalizedShares.map((s) => ({ witness: s.witnessHex, share_bp: s.bp })),
  );
  const totalPct = ((shareSummary.sumBp / SHARE_BP_TOTAL) * 100).toFixed(2);
  const totalGames = normalizedShares.reduce((acc, s) => acc + (Number(s.games) || 0), 0);

  const wallUs = compat.work?.wall_us ?? compat.wall_us;
  const dbProbes = compat.work?.db_probes ?? compat.db_probes;
  const oodaRounds = compat.ooda?.rounds_run != null ? Number(compat.ooda.rounds_run) : compat.ooda_rounds_run;
  const oodaVerified = compat.ooda?.verified ?? compat.ooda_verified;
  const oodaUnverified = compat.ooda?.unverified ?? compat.ooda_unverified;
  const culpritId = compat.ooda?.culprit_id ?? compat.culprit_id;
  const culpritContrast = compat.ooda?.culprit_contrast != null
    ? Number(compat.ooda.culprit_contrast)
    : compat.culprit_contrast;
  const envelopeName = compat.envelope ?? compat.work?.preset ?? 'standard';
  const oodaAction = resolveOodaAction(
    compat.disposition,
    typeof oodaUnverified === 'boolean' ? oodaUnverified : (oodaUnverified ?? 0) > 0,
    compat.steps.length,
  );
  const verifiedCount = typeof oodaVerified === 'number' ? oodaVerified : (oodaVerified ? 1 : 0);
  const unverifiedCount = typeof oodaUnverified === 'number' ? oodaUnverified : (oodaUnverified ? 1 : 0);

  return (
    <details open className={styles.proof} data-forward-pass-proof="ordinary-chat-turn">
      <summary>
        Forward pass proof
        <Chip variant={oodaAction.variant} data-ooda-action={oodaAction.action}>ACTION: {oodaAction.action}</Chip>
        <span>{compat.disposition || 'no disposition'}</span>
        {compat.firmware && <span>firmware: {compat.firmware}</span>}
        <span>exact producing turn · no replay</span>
        <span>{compat.witnessed ? `turn ${compat.ordinal} witnessed` : 'not witnessed'}</span>
        {compat.top_salience != null && <span>top salience {Number(compat.top_salience).toFixed(2)}</span>}
        {dbProbes != null && <span>{Number(dbProbes).toLocaleString()} DB probes</span>}
        {wallUs != null && <span>{formatWallUs(Number(wallUs))}</span>}
        {oodaRounds != null && <span>{oodaRounds} OODA round{oodaRounds === 1 ? '' : 's'}</span>}
        <span>{compat.steps.length} steps</span>
        <span>{compat.turns_before} earlier turns</span>
      </summary>

      <div className={styles.body}>
        <div className={styles.law}>
          RESOLVE → COUPLE → ORIENT → ROUTE → SCAN → COMPOSE → PROPOSE → STEER → SELECT → REALIZE → WITNESS
        </div>

        <dl className={styles.receipt}>
          <div><dt>ACTION disposition</dt><dd><Chip variant={oodaAction.variant}>{oodaAction.action}</Chip> <code>{compat.disposition || '—'}</code></dd></div>
          <div><dt>firmware</dt><dd><code>{compat.firmware ?? 'program'}</code>{compat.firmware_id ? <> · <code title={compat.firmware_id}>{compact(compat.firmware_id)}</code></> : null}</dd></div>
          <div><dt>compute envelope</dt><dd><code>{envelopeName}</code></dd></div>
          <div><dt>authority scope</dt><dd>{formatScope(compat.authority_scope)}</dd></div>
          <div><dt>session</dt><dd><code>{compat.session}</code> of <code>{compat.participant}</code></dd></div>
          <div><dt>prompt</dt><dd><code title={compat.prompt_id}>{compact(compat.prompt_id)}</code></dd></div>
          <div><dt>response</dt><dd><code title={compat.response_id}>{compact(compat.response_id)}</code></dd></div>
          <div><dt>turn</dt><dd><code title={compat.turn_id}>{compact(compat.turn_id)}</code></dd></div>
          <div><dt>place in the session</dt><dd>{compat.ordinal} · {compat.turns_before} before it</dd></div>
          <div><dt>witness</dt><dd>{compat.witnessed ? 'recorded by the Engine before this receipt was returned' : compat.reason ?? 'not recorded'}</dd></div>
        </dl>

        <section>
          <div className={styles.sectionHeader}>
            <h4>Gödel OODA (ACTION / BELIEF / RISK), Salience &amp; Work Counters</h4>
            <Chip variant={oodaAction.variant}>ACTION: {oodaAction.action}</Chip>
          </div>
          <dl className={styles.metricsGrid}>
            <div>
              <dt>ACTION</dt>
              <dd><Chip variant={oodaAction.variant}>{oodaAction.action}</Chip> <span>{compat.disposition || '—'}</span></dd>
            </div>
            <div>
              <dt>BELIEF · mean confidence</dt>
              <dd>{compat.mean_confidence != null ? Number(compat.mean_confidence).toFixed(3) : '—'}</dd>
            </div>
            <div>
              <dt>BELIEF · top salience</dt>
              <dd>{compat.top_salience != null ? Number(compat.top_salience).toFixed(3) : '—'}</dd>
            </div>
            <div>
              <dt>BELIEF · consensus counts</dt>
              <dd>{normalizedShares.length} witness{normalizedShares.length === 1 ? '' : 'es'} · {totalGames.toLocaleString()} games · {compat.steps.length} step{compat.steps.length === 1 ? '' : 's'}</dd>
            </div>
            <div>
              <dt>RISK · OODA rounds</dt>
              <dd>{oodaRounds != null ? oodaRounds : 0}</dd>
            </div>
            <div>
              <dt>RISK · OODA verified / unverified</dt>
              <dd>{verifiedCount} / {unverifiedCount}</dd>
            </div>
            <div>
              <dt>RISK · culprit contrast</dt>
              <dd>
                {culpritId ? (
                  <>
                    <Link to={`/explore/entity/${encodeURIComponent(culpritId)}`} title={culpritId}>
                      <code>{compact(culpritId)}</code>
                    </Link>
                    {culpritContrast != null ? ` (${Number(culpritContrast).toFixed(3)})` : ''}
                  </>
                ) : (
                  culpritContrast != null ? Number(culpritContrast).toFixed(3) : 'none'
                )}
              </dd>
            </div>
            <div><dt>DB probes</dt><dd>{dbProbes != null ? Number(dbProbes).toLocaleString() : '—'}</dd></div>
            <div><dt>wall time (wall_us)</dt><dd>{formatWallUs(wallUs != null ? Number(wallUs) : null)}</dd></div>
            <div><dt>rows scanned</dt><dd>{compat.work?.rows_scanned != null ? Number(compat.work.rows_scanned).toLocaleString() : '—'}</dd></div>
            <div><dt>hops / tokens out</dt><dd>{compat.work?.hops != null || compat.work?.tokens_out != null ? `${compat.work?.hops ?? '—'} hops · ${compat.work?.tokens_out ?? '—'} tokens` : '—'}</dd></div>
          </dl>
        </section>

        <section>
          <div className={styles.sectionHeader}>
            <h4>Witness Attribution &amp; Fisher share_bp Conservation</h4>
            {normalizedShares.length > 0 && (
              <Chip variant={shareSummary.conserved ? 'engineOk' : 'engineMissing'}>
                Σ {shareSummary.sumBp} / {SHARE_BP_TOTAL} bp ({totalPct}%) {shareSummary.conserved ? '(conserved)' : '(mismatch)'}
              </Chip>
            )}
          </div>
          {normalizedShares.length === 0 ? (
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
                  {normalizedShares.map((item, idx) => (
                    <tr key={`${item.witnessHex || 'witness'}:${idx}`}>
                      <td title={item.witnessHex || undefined}>
                        {item.witnessHex ? (
                          <Link to={`/explore/entity/${encodeURIComponent(item.witnessHex)}`}>
                            <code>{compact(item.witnessHex)}</code>
                          </Link>
                        ) : (
                          <code>—</code>
                        )}
                      </td>
                      <td>{item.label ?? '—'}</td>
                      <td>{item.trust != null ? Number(item.trust).toFixed(3) : '—'}</td>
                      <td>{item.fisher_info != null ? Number(item.fisher_info).toFixed(3) : '—'}</td>
                      <td>{item.games != null ? Number(item.games).toLocaleString() : '—'}</td>
                      <td>{item.bp}</td>
                      <td>{((item.bp / SHARE_BP_TOTAL) * 100).toFixed(2)}%</td>
                    </tr>
                  ))}
                </tbody>
                <tfoot>
                  <tr>
                    <td colSpan={4}><strong>Total</strong></td>
                    <td><strong>{totalGames.toLocaleString()}</strong></td>
                    <td><strong>{shareSummary.sumBp}</strong></td>
                    <td><strong>{totalPct}%</strong></td>
                  </tr>
                </tfoot>
              </Table>
            </div>
          )}
        </section>

        <section>
          <h4>Steps</h4>
          {compat.steps.length === 0 ? (
            <p className={styles.empty}>Nothing was emitted.</p>
          ) : (
            <div className={styles.tableWrap}>
              <Table>
                <thead>
                  <tr><th>step</th><th>kind</th><th>emitted</th><th>grounds</th><th>continuity</th><th>confidence</th></tr>
                </thead>
                <tbody>
                  {compat.steps.map((step) => (
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
          <pre className={styles.trace}>{compat.trace}</pre>
        </section>
      </div>
    </details>
  );
}
