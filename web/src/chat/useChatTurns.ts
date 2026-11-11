/** Chat turn lifecycle: streaming send, quote preflight, and retry-with-quote, plus the busy flag they share. */
import { useEffect, useRef, useState } from 'react';

import { apiPost, describeFailure, PaymentRequiredError, type PreflightQuoteResponse, type ChatCompletionResponse } from '../api/client';

import { streamChat } from '../api/sse';

import { asNum, provenanceFromMetadata, useAppStore, type ChatPerformance, type ProvenanceEntry } from '../store';
import type { ForwardPassProof } from '../api/forwardProof';

const asOptionalNum = (value: string | number | null | undefined): number | undefined =>
  value == null ? undefined : asNum(value);

export function useChatTurns(
  input: string,
  setInput: (value: string) => void,
  loadConversations: () => Promise<void>,
) {

  const { tenant, quoteId, model, messages } = useAppStore();

  const { setQuoteId, pushMessage, updateLastAssistant, setPendingQuote } = useAppStore();

  const [busy, setBusy] = useState(false);

  const abortRef = useRef<AbortController | null>(null);

  useEffect(() => () => abortRef.current?.abort(), []);


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

    const prompt = input;

    if (!prompt.trim() || busy) return;

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

        const forwardProof = lap?.forward_proof;
        if (forwardProof) {
          updateLastAssistant((m) => ({ ...m, forwardProof }));
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
        updateLastAssistant((m) => ({ ...m, streaming: false, error: 'Response interrupted.' }));
        return;

      }

      if (e instanceof PaymentRequiredError) {

        updateLastAssistant((m) => ({ ...m, streaming: false, error: 'payment required' }));

        await requestQuote('chat.completions', e.message);

      } else {

        updateLastAssistant((m) => ({

          ...m,

          streaming: false,

          error: describeFailure(e).message,

        }));

      }

    } finally {

      setBusy(false);

      void loadConversations();

    }

  }



  async function retryWithQuote() {

    // Retry sends only the newest user turn and the session key.
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
      const forwardProof = (response.metadata?.laplace as
        | { forward_proof?: ForwardPassProof }
        | undefined)?.forward_proof;

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

      updateLastAssistant((m) => ({
        ...m, content, provenance, performance, forwardProof, streaming: false,
      }));

    } catch (e) {

      updateLastAssistant((m) => ({

        ...m,

        streaming: false,

        error: describeFailure(e).message,

      }));

    } finally {

      setBusy(false);

    }

  }

  return { busy, send, retryWithQuote };

}
