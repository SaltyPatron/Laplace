import { useCallback, useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';

import {
  Banner,
  Button,
  ConsensusBadge,
  Muted,
  Select,
  TextArea,
} from '@ui';

import { apiGet } from '../api/client';

import { asNum, useAppStore } from '../store';

import { ReceiptPanel } from './ReceiptPanel';
import { ForwardPassProofPanel } from './ForwardPassProofPanel';
import { classifyTurn, lastStage } from './turnState';
import { useChatTurns } from './useChatTurns';

import styles from './ChatView.module.css';



export function ChatView() {

  const { session, messages, pendingQuote, exploreSeedPrompt, authUser } = useAppStore();

  const { setExploreSeedPrompt, clearConversation } = useAppStore();

  const [input, setInput] = useState('');

  const transcriptRef = useRef<HTMLDivElement>(null);


  const [conversations, setConversations] = useState<{
    sessionKey: string;
    title?: string | null;
    createdAt: string;
    lastTurnAt: string;
  }[]>([]);

  const loadConversations = useCallback(async () => {
    if (!authUser) {
      setConversations([]);
      return;
    }
    try {
      const result = await apiGet<{ conversations: {
        sessionKey: string;
        title?: string | null;
        createdAt: string;
        lastTurnAt: string;
      }[] }>('/v1/auth/conversations');
      setConversations(result.conversations ?? []);
    } catch {
      setConversations([]);
    }
  }, [authUser]);


  const { busy, send, retryWithQuote } = useChatTurns(input, setInput, loadConversations);


  useEffect(() => { void loadConversations(); }, [loadConversations]);



  useEffect(() => {

    if (!exploreSeedPrompt) return;

    setInput(exploreSeedPrompt);

    setExploreSeedPrompt(null);

  }, [exploreSeedPrompt, setExploreSeedPrompt]);





  useEffect(() => {

    const el = transcriptRef.current;

    if (!el) return;

    const nearBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 120;

    if (nearBottom) el.scrollTo({ top: el.scrollHeight });

  }, [messages]);




  return (

    <div className={styles.layout}>

      <section className={styles.main}>

        <div className={styles.toolbar}>
          <Muted className={styles.modeHint}>
            Replies are composed from witnessed consensus; each carries its receipt.
          </Muted>

          {authUser && conversations.length > 0 && (
            <label className={styles.sessions}>
              <span>Session</span>
              <Select
                value={session ?? ''}
                onChange={(event) => {
                  clearConversation();
                  if (event.target.value) useAppStore.getState().setSession(event.target.value);
                }}
              >
                <option value="">New conversation</option>
                {conversations.map((conversation) => (
                  <option key={conversation.sessionKey} value={conversation.sessionKey}>
                    {conversation.title || conversation.sessionKey}
                  </option>
                ))}
              </Select>
            </label>
          )}

          <Button variant="ghost" onClick={clearConversation}>New</Button>

        </div>



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
                return <div className={styles.outcome} data-turn-state={outcome.state} role="status">
                  <strong>{outcome.state === 'abstained' ? 'No answer: not enough evidence.' : outcome.stage ? `Couldn’t answer: stopped at the ${outcome.stage} step.` : 'Couldn’t answer this one.'}</strong>
                  <span className={styles.messageEmpty}>{outcome.state === 'abstained' ? outcome.reason : outcome.message}</span>
                  {i === messages.length - 1 && !busy && <Button size="sm" variant="ghost" onClick={() => void retryWithQuote()}>Try again</Button>}
                </div>;
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

              {/* A reply whose content chunk carried `laplace: {}` has no receipt to
                  bind μ/witness badges to; that absence is stated, so it is not read
                  as a reply with zero witnesses. */}
              {m.role === 'assistant' && !m.streaming && !m.error && m.content && m.provenance.length === 0 && (

                <Muted className={styles.noReceipts}>

                  no receipt returned for this reply — look the terms up in Evidence →

                </Muted>

              )}

              {m.forwardProof && (
                <details className={styles.trace}>
                  <summary>Reasoning trace{lastStage(m.forwardProof) ? ` · reached ${lastStage(m.forwardProof)}` : ''}</summary>
                  <Link className={styles.forwardProofLink} to={`/forward-proof?turn=${i}`}>Open the full trace page</Link>
                  <ForwardPassProofPanel proof={m.forwardProof} />
                </details>
              )}

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



        {pendingQuote && (

          <Banner>

            <strong>Quote required for {pendingQuote.serviceId}.</strong>{' '}

            {pendingQuote.quote ? (

              <>

                {(asNum(pendingQuote.quote.amount_cents) / 100).toFixed(2)}{' '}

                {pendingQuote.quote.currency} — status {pendingQuote.quote.status}.{' '}

                {pendingQuote.quote.stripe_checkout_url && (

                  <a href={pendingQuote.quote.stripe_checkout_url} target="_blank" rel="noreferrer">

                    Pay with Stripe

                  </a>

                )}{' '}

                <Button size="sm" onClick={retryWithQuote} disabled={busy} loading={busy}>

                  Retry with quote

                </Button>

              </>

            ) : (

              <span>{pendingQuote.message}</span>

            )}

          </Banner>

        )}



        <div className={styles.composer}>

          <TextArea

            value={input}

            placeholder="Ask about anything the witnesses attest to…"

            rows={2}

            onChange={(e) => setInput(e.target.value)}

            onKeyDown={(e) => {

              if (e.key === 'Enter' && !e.shiftKey) {

                e.preventDefault();

                void send();

              }

            }}

          />

          <Button onClick={() => void send()} disabled={busy || !input.trim()} loading={busy}>

            {busy ? '…' : 'Send'}

          </Button>

        </div>

      </section>

      <ReceiptPanel />

    </div>

  );

}
