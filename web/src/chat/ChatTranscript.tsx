import type { RefObject } from 'react';
import { Link } from 'react-router-dom';
import { Button, ConsensusBadge, Muted } from '@ui';
import { summarizeWitnessShares } from '../api/forwardProof';
import type { ChatMessage } from '../store';
import { ForwardPassProofPanel } from './ForwardPassProofPanel';
import { classifyTurn, lastStage } from './turnState';
import styles from './ChatView.module.css';

export function ChatTranscript({
  messages,
  transcriptRef,
  busy,
  onRetry,
}: {
  messages: ChatMessage[];
  transcriptRef: RefObject<HTMLDivElement | null>;
  busy: boolean;
  onRetry: () => void;
}) {
  return (
    <div className={styles.transcript} ref={transcriptRef}>
      {messages.length === 0 && (
        <div className={styles.empty}>
          <h2>Ask a question.</h2>
          <p>Answers are composed only from what sources have attested. Each answer shows how well supported it is, and you can open the evidence behind it.</p>
        </div>
      )}

      {messages.map((m, i) => (
        <div key={i} className={`${styles.message} ${m.role === 'user' ? styles.user : styles.assistant}`}>
          {m.role === 'user' ? <div>{m.content}</div> : (() => {
            const outcome = classifyTurn(m);
            if (outcome.state === 'pending') return <div className={styles.messageEmpty} role="status">Working on an answer…</div>;
            if (outcome.state === 'answered') return <div>{m.content}</div>;
            return (
              <div className={styles.outcome} data-turn-state={outcome.state} role="status">
                <strong>{outcome.state === 'abstained' ? 'No answer: not enough evidence.' : outcome.stage ? `Couldn’t answer: stopped at the ${outcome.stage} step.` : 'Couldn’t answer this one.'}</strong>
                <span className={styles.messageEmpty}>{outcome.state === 'abstained' ? outcome.reason : outcome.message}</span>
                {i === messages.length - 1 && !busy && <Button size="sm" variant="ghost" onClick={onRetry}>Try again</Button>}
              </div>
            );
          })()}

          {m.provenance.length > 0 && (
            <div className={styles.badges}>
              {m.provenance.map((p, j) => (
                <ConsensusBadge
                  key={j}
                  tone="chat"
                  ordUsed={p.ordUsed}
                  mu={p.effMu}
                  witnesses={p.witnesses}
                />
              ))}
            </div>
          )}

          {m.role === 'assistant' && !m.streaming && !m.error && m.content && m.provenance.length === 0 && (
            <Muted className={styles.noReceipts}>
              no receipt returned for this reply — look the terms up in Evidence →
            </Muted>
          )}

          {m.forwardProof && (() => {
            const proof = m.forwardProof;
            const stage = lastStage(proof);
            const dbProbes = proof.work?.db_probes ?? proof.db_probes;
            const wallUs = proof.work?.wall_us ?? proof.wall_us;
            const shares = summarizeWitnessShares(proof.witness_shares);
            return (
              <details className={styles.trace}>
                <summary>
                  OODA &amp; Reasoning trace
                  {proof.disposition ? ` · ${proof.disposition}` : ''}
                  {stage ? ` · reached ${stage}` : ''}
                  {proof.top_salience != null ? ` · salience ${Number(proof.top_salience).toFixed(2)}` : ''}
                  {dbProbes != null ? ` · ${dbProbes} probes` : ''}
                  {wallUs != null ? ` · ${wallUs} µs` : ''}
                  {shares.count > 0 ? ` · Σ share_bp ${shares.sumBp}/${shares.expectedBp}` : ''}
                </summary>
                <Link className={styles.forwardProofLink} to={`/explore/walk?turn=${i}`}>Open the full trace page</Link>
                <ForwardPassProofPanel proof={proof} />
              </details>
            );
          })()}

          {m.performance && (
            <div className={styles.performance}>
              substrate {m.performance.substrateMs.toFixed(1)} ms
              {' · '}first {m.performance.firstResultMs?.toFixed(1) ?? '—'} ms
              {' · '}total {m.performance.elapsedMs.toFixed(1)} ms
              {' · '}{m.performance.outputWords} words
              {' · '}{m.performance.outputCodepoints} codepoints
              {m.performance.generatedTokens != null
                ? ` · ${m.performance.generatedTokens} trajectory tokens`
                : ''}
              {m.performance.generatedTokensPerSecond != null
                ? ` · ${m.performance.generatedTokensPerSecond.toFixed(1)} trajectory tokens/s`
                : ''}
            </div>
          )}
        </div>
      ))}
    </div>
  );
}
