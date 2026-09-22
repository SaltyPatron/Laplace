import type { ForwardPassProof } from '../api/forwardProof';
import styles from './ForwardPassProofPanel.module.css';

function compact(value: string | undefined): string {
  if (!value) return '—';
  return value.length <= 24 ? value : `${value.slice(0, 10)}…${value.slice(-10)}`;
}

function fp1e9(value: number | undefined): string {
  if (value == null) return '—';
  return (value / 1_000_000_000).toFixed(3);
}

export function ForwardPassProofPanel({ proof }: { proof: ForwardPassProof }) {
  return (
    <details open className={styles.proof} data-forward-pass-proof="ordinary-chat-turn">
      <summary>
        Forward pass proof
        <span>{proof.completion ? 'complete' : proof.disposition}</span>
        <span>exact producing turn · no replay</span>
        <span>program {compact(proof.program_id)}</span>
        <span>{proof.response_witnessed ? 'response persisted' : 'response witness missing'}</span>
        <span>{proof.events.length} native receipts</span>
        <span>{proof.prior_discourse_ids.length} discourse ids</span>
      </summary>

      <div className={styles.body}>
        <div className={styles.law}>
          RESOLVE → COUPLE → ORIENT → ROUTE → SCAN → COMPOSE → PROPOSE → STEER → SELECT → REALIZE → WITNESS
        </div>

        <dl className={styles.receipt}>
          <div><dt>session</dt><dd><code>{proof.session}</code></dd></div>
          <div><dt>prompt occurrence</dt><dd><code>{compact(proof.prompt_occurrence_key)}</code></dd></div>
          <div><dt>program</dt><dd><code title={proof.program_id}>{compact(proof.program_id)}</code></dd></div>
          <div><dt>semantic act</dt><dd><code title={proof.semantic_act_id}>{compact(proof.semantic_act_id)}</code></dd></div>
          <div><dt>output fingerprint</dt><dd><code title={proof.output_fingerprint}>{compact(proof.output_fingerprint)}</code></dd></div>
          <div><dt>terminal</dt><dd>{proof.disposition} · {proof.output_count} outputs</dd></div>
          <div><dt>obligations</dt><dd>{proof.satisfied_obligations}/{proof.required_obligations} satisfied · {proof.remaining_required} remaining</dd></div>
          <div><dt>response witness</dt><dd>{proof.response_witnessed ? 'persisted before this receipt was returned' : 'not confirmed'}</dd></div>
        </dl>

        <section>
          <h4>Substrate discourse consumed by this turn</h4>
          {proof.prior_discourse_ids.length === 0 ? (
            <p className={styles.empty}>No prior discourse identities were supplied.</p>
          ) : (
            <div className={styles.ids}>
              {proof.prior_discourse_ids.map((id, i) => (
                <code key={`${id}-${i}`} title={id}>{i + 1}. {compact(id)}</code>
              ))}
            </div>
          )}
        </section>

        <section>
          <h4>Exact producing lifecycle receipts</h4>
          <div className={styles.tableWrap}>
            <table>
              <thead>
                <tr>
                  <th>step</th><th>stage</th><th>entity</th><th>candidates</th>
                  <th>context</th><th>Q→K</th><th>exact</th><th>trajectory</th>
                  <th>support</th><th>standing</th>
                </tr>
              </thead>
              <tbody>
                {proof.events.map((row, i) => (
                  <tr key={`${row.step}-${row.routing_round}-${row.event}-${i}`}>
                    <td>{row.step}.{row.routing_round}</td>
                    <td>{row.event}</td>
                    <td title={row.entity_id_hex}>{row.entity || compact(row.entity_id_hex)}</td>
                    <td>{row.candidate_count}</td>
                    <td>{row.ordered_context_count}</td>
                    <td>{row.proposal_channel_count}</td>
                    <td>{row.exact_channel_count}</td>
                    <td>{row.sequence_occurrences}</td>
                    <td>
                      {row.support_relation
                        ? `${row.support_outbound === false ? '←' : '→'} ${row.support_relation}`
                        : row.declared_result ? 'declared result' : '—'}
                      {row.support_witnesses != null ? ` · ${row.support_witnesses} wit` : ''}
                    </td>
                    <td>
                      {row.support_rating != null
                        ? `μ ${fp1e9(row.support_rating)} · RD ${fp1e9(row.support_rd)}`
                        : '—'}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      </div>
    </details>
  );
}
