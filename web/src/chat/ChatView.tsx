import { useEffect, useRef, useState } from 'react';

import {
  Banner,
  Button,
  ConsensusBadge,
  Muted,
  TextArea,
} from '@ui';

import { apiPost, PaymentRequiredError, type PreflightQuoteResponse, type ChatCompletionResponse } from '../api/client';

import { streamChat } from '../api/sse';

import { asNum, provenanceFromMetadata, useAppStore, type ChatPerformance, type ProvenanceEntry } from '../store';

import { ReceiptPanel } from './ReceiptPanel';

import styles from './ChatView.module.css';

const asOptionalNum = (value: string | number | null | undefined): number | undefined =>
  value == null ? undefined : asNum(value);



export function ChatView() {

  const { tenant, quoteId, model, messages, pendingQuote, exploreSeedPrompt } = useAppStore();

  const { setQuoteId, pushMessage, updateLastAssistant, setPendingQuote, setExploreSeedPrompt, clearConversation } = useAppStore();

  const [input, setInput] = useState('');

  const [busy, setBusy] = useState(false);

  const transcriptRef = useRef<HTMLDivElement>(null);

  const abortRef = useRef<AbortController | null>(null);



  useEffect(() => () => abortRef.current?.abort(), []);



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



  async function requestQuote(serviceId: string, message: string) {

    try {

      const quote = await apiPost<PreflightQuoteResponse>(

        '/v1/billing/preflight',

        { service_id: serviceId, units: 1, tenant },

        { tenant },

      );

      setPendingQuote({ serviceId, quote, message });

      if (quote.quote_id) setQuoteId(quote.quote_id);

    } catch {

      setPendingQuote({ serviceId, message });

    }

  }



  async function send() {

    const prompt = input.trim();

    if (!prompt || busy) return;

    setBusy(true);

    setInput('');

    setPendingQuote(null);

    pushMessage({ role: 'user', content: prompt, provenance: [] });

    pushMessage({ role: 'assistant', content: '', provenance: [], streaming: true });



    // Conversation state is substrate-resident (spec 34): only the new turn is
    // sent, with the session key carrying continuity — history is never resent.
    const session = useAppStore.getState().session;
    const payload = {
      model,
      stream: true,
      messages: [{ role: 'user', content: prompt }],
      ...(session ? { session } : {}),
    };



    abortRef.current?.abort();

    const ac = new AbortController();

    abortRef.current = ac;



    try {

      let pendingProvenance: ProvenanceEntry | null = null;

      for await (const chunk of streamChat(
        '/v1/chat/completions', payload, { tenant, quoteId }, ac.signal,
        (key) => useAppStore.getState().setSession(key),
      )) {

        const delta = chunk.choices?.[0]?.delta;

        const lap = chunk.laplace;

        if (lap?.performance) {
          const p = lap.performance;
          updateLastAssistant((m) => ({
            ...m,
            performance: {
              substrateMs: asNum(p.substrate_ms),
              elapsedMs: asNum(p.elapsed_ms),
              firstResultMs: asOptionalNum(p.first_result_ms),
              outputUtf8Bytes: asNum(p.output_utf8_bytes),
              outputCodepoints: asNum(p.output_codepoints),
              outputWords: asNum(p.output_words),
              generatedTokens: asOptionalNum(p.generated_tokens),
              generatedTokensPerSecond: asOptionalNum(p.generated_tokens_per_second),
            },
          }));
        }

        if (delta?.content !== undefined || lap) {

          const text = delta?.content ?? '';

          if (lap?.eff_mu !== undefined || lap?.witnesses !== undefined) {

            pendingProvenance = { reply: text.trim(), effMu: lap.eff_mu, witnesses: lap.witnesses };

          }

          const lineProv = pendingProvenance;

          pendingProvenance = null;

          updateLastAssistant((m) => ({

            ...m,

            content: m.content + text,

            provenance: lineProv ? [...m.provenance, lineProv] : m.provenance,

          }));

        }

      }

      updateLastAssistant((m) => ({ ...m, streaming: false }));

    } catch (e) {

      if (e instanceof DOMException && e.name === 'AbortError') {

        return;

      }

      if (e instanceof PaymentRequiredError) {

        updateLastAssistant((m) => ({ ...m, streaming: false, error: 'payment required' }));

        await requestQuote('chat.completions', e.message);

      } else {

        updateLastAssistant((m) => ({

          ...m,

          streaming: false,

          error: e instanceof Error ? e.message : 'Request failed.',

        }));

      }

    } finally {

      setBusy(false);

    }

  }



  async function retryWithQuote() {

    // Same law as the live path: resend only the newest user turn + session key.
    const lastUser = [...messages]
      .reverse()
      .find((m) => m.role === 'user' && !m.streaming && !m.error);
    if (!lastUser) return;

    setBusy(true);

    setPendingQuote(null);

    pushMessage({ role: 'assistant', content: '', provenance: [], streaming: true });

    try {

      const session = useAppStore.getState().session;

      const response = await apiPost<ChatCompletionResponse>(

        '/v1/chat/completions',

        {
          model,
          messages: [{ role: 'user', content: lastUser.content }],
          ...(session ? { session } : {}),
        },

        { tenant, quoteId },

      );

      const content = response.choices?.[0]?.message?.content ?? '';

      const provenance = provenanceFromMetadata(response.metadata?.laplace?.provenance ?? undefined);

      const sessionKey = (response.metadata as { session?: string } | undefined)?.session;

      const rawPerformance = (response.metadata as { performance?: {
        substrate_ms: string | number;
        elapsed_ms: string | number;
        first_result_ms?: string | number;
        output_utf8_bytes: string | number;
        output_codepoints: string | number;
        output_words: string | number;
        generated_tokens?: string | number;
        generated_tokens_per_second?: string | number;
      } } | undefined)?.performance;

      const performance: ChatPerformance | undefined = rawPerformance ? {
        substrateMs: asNum(rawPerformance.substrate_ms),
        elapsedMs: asNum(rawPerformance.elapsed_ms),
        firstResultMs: asOptionalNum(rawPerformance.first_result_ms),
        outputUtf8Bytes: asNum(rawPerformance.output_utf8_bytes),
        outputCodepoints: asNum(rawPerformance.output_codepoints),
        outputWords: asNum(rawPerformance.output_words),
        generatedTokens: asOptionalNum(rawPerformance.generated_tokens),
        generatedTokensPerSecond: asOptionalNum(rawPerformance.generated_tokens_per_second),
      } : undefined;

      if (sessionKey) useAppStore.getState().setSession(sessionKey);

      updateLastAssistant((m) => ({ ...m, content, provenance, performance, streaming: false }));

    } catch (e) {

      updateLastAssistant((m) => ({

        ...m,

        streaming: false,

        error: e instanceof Error ? e.message : 'Request failed.',

      }));

    } finally {

      setBusy(false);

    }

  }



  return (

    <div className={styles.layout}>

      <section className={styles.main}>

        <div className={styles.toolbar}>
          <Muted className={styles.modeHint}>
            One witnessed forward pass: resolve every constituent, route typed relations,
            follow physical trajectories, then update the frontier after each selection.
          </Muted>

          <Button variant="ghost" onClick={clearConversation}>Clear</Button>

        </div>



        <div className={styles.transcript} ref={transcriptRef}>

          {messages.length === 0 && (

            <div className={styles.empty}>

              <h2>Ask the substrate.</h2>

              <p>Every reply is grounded in witnessed consensus — μ and witness counts attached, evidence one click away.</p>

            </div>

          )}

          {messages.map((m, i) => (

            <div key={i} className={`${styles.message} ${m.role === 'user' ? styles.user : styles.assistant}`}>

              <div>

                {/* A finished turn with nothing in it must still say so — an
                    empty string renders an invisible bubble, which reads as
                    "the question was never asked" rather than "no reply". */}
                {m.content || (m.streaming ? '…' : m.error ? '' : <span className={styles.messageEmpty}>no reply — the substrate returned nothing for this turn</span>)}

                {m.error && <span className={styles.messageError}> [{m.error}]</span>}

              </div>

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

              {/* This surface promises μ and witness counts on every reply. When
                  the endpoint returns `laplace: {}` for the content chunk there
                  is nothing to bind a badge to, and silently rendering no
                  badges reads as "this reply has no witnesses" rather than
                  "the receipt did not come back". Say which it is. */}
              {m.role === 'assistant' && !m.streaming && !m.error && m.content && m.provenance.length === 0 && (

                <Muted className={styles.noReceipts}>

                  no receipt returned for this reply — look the terms up in Evidence →

                </Muted>

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
