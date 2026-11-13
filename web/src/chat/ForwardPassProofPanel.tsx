import type { ForwardPassProof } from '../api/forwardProof';
import styles from './ForwardPassProofPanel.module.css';
import { Table } from '@ui';

function compact(value: string | undefined): string {
  if (!value) return '—';
  return value.length <= 24 ? value : `${value.slice(0, 10)}…${value.slice(-10)}`;
}

/** The receipt of the turn that produced this response, as the Engine wrote it, and the Engine's trace. */
export function ForwardPassProofPanel({ proof }: { proof: ForwardPassProof }) {
  return (
    <details open className={styles.proof} data-forward-pass-proof="ordinary-chat-turn">
      <summary>
        Forward pass proof
        <span>{proof.disposition || 'no disposition'}</span>
        <span>exact producing turn · no replay</span>
        <span>{proof.witnessed ? `turn ${proof.ordinal} witnessed` : 'not witnessed'}</span>
        <span>{proof.steps.length} steps</span>
        <span>{proof.turns_before} earlier turns</span>
      </summary>

      <div className={styles.body}>
        <div className={styles.law}>
          RESOLVE → COUPLE → ORIENT → ROUTE → SCAN → COMPOSE → PROPOSE → STEER → SELECT → REALIZE → WITNESS
        </div>

        <dl className={styles.receipt}>
          <div><dt>session</dt><dd><code>{proof.session}</code> of <code>{proof.participant}</code></dd></div>
          <div><dt>prompt</dt><dd><code title={proof.prompt_id}>{compact(proof.prompt_id)}</code></dd></div>
          <div><dt>response</dt><dd><code title={proof.response_id}>{compact(proof.response_id)}</code></dd></div>
          <div><dt>turn</dt><dd><code title={proof.turn_id}>{compact(proof.turn_id)}</code></dd></div>
          <div><dt>place in the session</dt><dd>{proof.ordinal} · {proof.turns_before} before it</dd></div>
          <div><dt>witness</dt><dd>{proof.witnessed ? 'recorded by the Engine before this receipt was returned' : proof.reason ?? 'not recorded'}</dd></div>
        </dl>

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
