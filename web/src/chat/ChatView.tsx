import { useEffect, useRef, useState } from 'react';
import { apiGet, apiPost, PaymentRequiredError, type ModelList, type PreflightQuoteResponse, type ChatCompletionResponse } from '../api/client';
import { streamChat } from '../api/sse';
import { asNum, provenanceFromMetadata, useAppStore, type ProvenanceEntry } from '../store';
import { ProvenanceBadge } from './ProvenanceBadge';
import { ReceiptPanel } from './ReceiptPanel';

export function ChatView() {
  const { tenant, quoteId, model, messages, pendingQuote } = useAppStore();
  const { setModel, setQuoteId, pushMessage, updateLastAssistant, setPendingQuote, clearConversation } = useAppStore();
  const [models, setModels] = useState<string[]>([]);
  const [input, setInput] = useState('');
  const [busy, setBusy] = useState(false);
  const transcriptRef = useRef<HTMLDivElement>(null);
  const abortRef = useRef<AbortController | null>(null);

  useEffect(() => () => abortRef.current?.abort(), []);

  useEffect(() => {
    apiGet<ModelList>('/v1/models')
      .then((list) => setModels((list.data ?? []).map((m) => m.id ?? '').filter(Boolean)))
      .catch(() => setModels(['laplace-converse-001', 'laplace-completions-001']));
  }, []);

  useEffect(() => {
    const el = transcriptRef.current;
    if (!el) return;
    // Only auto-follow if the user is already near the bottom; otherwise a token stream
    // yanks them down every chunk while they're trying to read earlier replies.
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

    const history = useAppStore
      .getState()
      .messages.filter((m) => !m.streaming && !m.error)
      .map((m) => ({ role: m.role, content: m.content }));

    const payload = { model, stream: true, messages: history };

    abortRef.current?.abort();
    const ac = new AbortController();
    abortRef.current = ac;

    try {
      let pendingProvenance: ProvenanceEntry | null = null;
      for await (const chunk of streamChat('/v1/chat/completions', payload, { tenant, quoteId }, ac.signal)) {
        const delta = chunk.choices?.[0]?.delta;
        const lap = chunk.laplace;
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
    const history = messages
      .filter((m) => !m.streaming && !m.error)
      .map((m) => ({ role: m.role, content: m.content }));
    if (!history.some((m) => m.role === 'user')) return;
    setBusy(true);
    setPendingQuote(null);
    pushMessage({ role: 'assistant', content: '', provenance: [], streaming: true });
    try {
      const response = await apiPost<ChatCompletionResponse>(
        '/v1/chat/completions',
        { model, messages: history },
        { tenant, quoteId },
      );
      const content = response.choices?.[0]?.message?.content ?? '';
      const provenance = provenanceFromMetadata(response.metadata?.laplace?.provenance ?? undefined);
      updateLastAssistant((m) => ({ ...m, content, provenance, streaming: false }));
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
    <div className="chat-layout">
      <section className="chat-main">
        <div className="chat-toolbar">
          <select value={model} onChange={(e) => setModel(e.target.value)}>
            {(models.length ? models : [model]).map((id) => (
              <option key={id} value={id}>{id}</option>
            ))}
          </select>
          <button className="ghost" onClick={clearConversation}>Clear</button>
        </div>

        <div className="transcript" ref={transcriptRef}>
          {messages.length === 0 && (
            <div className="empty">
              <h2>Ask the substrate.</h2>
              <p>Every reply is grounded in witnessed consensus — μ and witness counts attached, evidence one click away.</p>
            </div>
          )}
          {messages.map((m, i) => (
            <div key={i} className={`message ${m.role}`}>
              <div className="content">
                {m.content || (m.streaming ? '…' : '')}
                {m.error && <span className="error"> [{m.error}]</span>}
              </div>
              {m.provenance.length > 0 && (
                <div className="badges">
                  {m.provenance.map((p, j) => (
                    <ProvenanceBadge key={j} entry={p} />
                  ))}
                </div>
              )}
            </div>
          ))}
        </div>

        {pendingQuote && (
          <div className="quote-banner">
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
                <button onClick={retryWithQuote} disabled={busy}>Retry with quote</button>
              </>
            ) : (
              <span>{pendingQuote.message}</span>
            )}
          </div>
        )}

        <div className="composer">
          <textarea
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
          <button onClick={() => void send()} disabled={busy || !input.trim()}>
            {busy ? '…' : 'Send'}
          </button>
        </div>
      </section>
      <ReceiptPanel />
    </div>
  );
}
