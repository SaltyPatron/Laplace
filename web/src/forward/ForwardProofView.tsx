import { useState } from 'react';
import { Button, Muted, Panel, TextArea } from '@ui';
import { apiPost, type ChatCompletionResponse } from '../api/client';
import type { ForwardPassProof } from '../api/forwardProof';
import { useAppStore } from '../store';
import { ForwardPassProofPanel } from '../chat/ForwardPassProofPanel';
import styles from './ForwardProofView.module.css';

type Turn = { prompt: string; response: string; proof?: ForwardPassProof; error?: string };

export function ForwardProofView() {
  const { tenant, quoteId, model } = useAppStore();
  const [session, setSession] = useState<string>();
  const [input, setInput] = useState('');
  const [turns, setTurns] = useState<Turn[]>([]);
  const [busy, setBusy] = useState(false);

  async function runTurn() {
    const prompt = input.trim();
    if (!prompt || busy) return;
    setBusy(true);
    setInput('');
    try {
      const response = await apiPost<ChatCompletionResponse>('/v1/chat/completions', {
        model,
        stream: false,
        messages: [{ role: 'user', content: prompt }],
        ...(session ? { session } : {}),
      }, { tenant, quoteId });
      const nextSession = response.metadata?.session;
      if (nextSession) setSession(nextSession);
      const proof = (response.metadata?.laplace as { forward_proof?: ForwardPassProof } | undefined)?.forward_proof;
      setTurns((prior) => [...prior, {
        prompt,
        response: response.choices?.[0]?.message?.content ?? '',
        proof,
        ...(!proof ? { error: 'The producing chat turn returned no forward proof receipt.' } : {}),
      }]);
    } catch (error) {
      setTurns((prior) => [...prior, { prompt, response: '', error: error instanceof Error ? error.message : 'Request failed.' }]);
    } finally {
      setBusy(false);
    }
  }

  function reset() {
    setSession(undefined);
    setTurns([]);
    setInput('');
  }

  return <div className={styles.root}>
    <header className={styles.hero}>
      <span className={styles.eyebrow}>Executable cognition proof · ordinary chat path</span>
      <h2>Forward Pass Proof</h2>
      <p>Run a real multi-turn Laplace chat and inspect the exact native receipts that produced every answer. Each later turn shows the persisted substrate identities consumed as DISCOURSE; the client sends only the new prompt plus the session key.</p>
      <div className={styles.session}>
        <strong>Session</strong> <code>{session ?? 'new — minted by first turn'}</code>
        <Button variant="ghost" onClick={reset} disabled={busy}>New proof session</Button>
      </div>
    </header>

    <div className={styles.turns}>
      {turns.length === 0 && <Panel title="No turns yet"><Muted>Enter a prompt below. The response and its producing forward-program receipt will appear here.</Muted></Panel>}
      {turns.map((turn, index) => <section className={styles.turn} key={index}>
        <div className={styles.turnHead}><span>Turn {index + 1}</span><code>{turn.proof?.program_id ?? 'no program receipt'}</code></div>
        <div className={styles.exchange}>
          <div><strong>Prompt</strong><p>{turn.prompt}</p></div>
          <div><strong>Response</strong><p>{turn.response || 'no realized response'}</p></div>
        </div>
        {turn.error && <div className={styles.error}>{turn.error}</div>}
        {turn.proof && <ForwardPassProofPanel proof={turn.proof} />}
      </section>)}
    </div>

    <div className={styles.composer}>
      <TextArea value={input} rows={3} placeholder={turns.length ? 'Continue the same witnessed session…' : 'Prompt Laplace…'}
        onChange={(e) => setInput(e.target.value)}
        onKeyDown={(e) => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); void runTurn(); } }} />
      <Button onClick={() => void runTurn()} disabled={busy || !input.trim()} loading={busy}>{busy ? 'Running…' : 'Run forward pass'}</Button>
    </div>
  </div>;
}
