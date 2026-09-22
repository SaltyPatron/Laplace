import { Link, useSearchParams } from 'react-router-dom';
import { Muted, Panel } from '@ui';
import { useAppStore } from '../store';
import { ForwardPassProofPanel } from '../chat/ForwardPassProofPanel';
import styles from './ForwardProofView.module.css';

export function ForwardProofView() {
  const { session, messages } = useAppStore();
  const [search] = useSearchParams();
  const requested = Number(search.get('turn') ?? '-1');

  const turns = messages.flatMap((message, index) => {
    if (message.role !== 'assistant' || !message.forwardProof) return [];
    const prompt = [...messages.slice(0, index)].reverse().find((m) => m.role === 'user');
    return [{
      index,
      prompt: prompt?.content ?? '',
      response: message.content,
      proof: message.forwardProof,
      performance: message.performance,
    }];
  });

  return <div className={styles.root}>
    <header className={styles.hero}>
      <span className={styles.eyebrow}>Read-only proof of ordinary Chat execution</span>
      <h2>Forward Pass Proof</h2>
      <p>
        This page does not execute a prompt. It shows the receipts returned by the exact
        Chat turns that already ran through <code>/v1/chat/completions</code>.
      </p>
      <div className={styles.session}>
        <strong>Chat session</strong> <code>{session ?? 'no active session'}</code>
        <Link className={styles.chatLink} to="/chat">Back to Chat</Link>
      </div>
    </header>

    <div className={styles.turns}>
      {turns.length === 0 && (
        <Panel title="No producing Chat turn is loaded">
          <Muted>
            Run the real Chat path first. Its exact forward-program receipt will appear here;
            this proof page has no prompt box, replay button, or sibling execution path.
          </Muted>
        </Panel>
      )}

      {turns.map((turn, ordinal) => (
        <section
          className={`${styles.turn} ${requested === turn.index ? styles.selected : ''}`}
          id={`turn-${turn.index}`}
          key={turn.index}
        >
          <div className={styles.turnHead}>
            <span>Chat turn {ordinal + 1}</span>
            <code>{turn.proof.program_id ?? 'no program receipt'}</code>
          </div>
          <div className={styles.exchange}>
            <div><strong>Prompt already executed</strong><p>{turn.prompt}</p></div>
            <div><strong>Response produced</strong><p>{turn.response || 'no realized response'}</p></div>
          </div>
          {turn.performance && (
            <div className={styles.performance}>
              substrate {turn.performance.substrateMs.toFixed(1)} ms
              {' · '}first {turn.performance.firstResultMs?.toFixed(1) ?? '—'} ms
              {' · '}total {turn.performance.elapsedMs.toFixed(1)} ms
              {' · '}{turn.performance.outputWords} words
            </div>
          )}
          <ForwardPassProofPanel proof={turn.proof} />
        </section>
      ))}
    </div>
  </div>;
}
