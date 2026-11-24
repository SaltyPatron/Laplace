import { useCallback, useEffect, useMemo, useRef, useState } from 'react';

import {
  Badge,
  Banner,
  Button,
  Muted,
  Select,
  TextArea,
  Tooltip,
  TooltipContent,
  TooltipTrigger,
} from '@ui';

import { apiGet } from '../api/client';
import {
  COMPUTE_ENVELOPES,
  FIRMWARE_PROFILES,
  type ComputeEnvelopePreset,
  type FirmwareProfile,
} from '../api/forwardProof';

import { asNum, useAppStore } from '../store';

import { ChatTranscript } from './ChatTranscript';
import { ReceiptPanel } from './ReceiptPanel';
import { useChatTurns } from './useChatTurns';

import styles from './ChatView.module.css';



interface FirmwareCatalogEntry {
  id: string;
  label: string;
  summary: string;
  path?: string | null;
  id_hex?: string | null;
  directives?: Record<string, string> | null;
  bytes?: number | null;
  available_on_disk?: boolean;
}

function formatFirmwareTooltip(entry: FirmwareCatalogEntry): string {
  const dirs = Object.entries(entry.directives ?? {})
    .slice(0, 8)
    .map(([k, v]) => `${k}=${v}`)
    .join(' · ');
  return dirs ? `${entry.summary} (${dirs})` : entry.summary;
}

export function ChatView() {

  const {
    tenant,
    session,
    model,
    firmware,
    envelope,
    messages,
    pendingQuote,
    exploreSeedPrompt,
    authUser,
    servicePrincipal,
    devPrincipal,
  } = useAppStore();

  const { setExploreSeedPrompt, clearConversation, setFirmware, setEnvelope } = useAppStore();

  const [input, setInput] = useState('');
  const [firmwares, setFirmwares] = useState<FirmwareCatalogEntry[]>(() =>
    FIRMWARE_PROFILES.map((profile) => ({ ...profile })),
  );

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
    const controller = new AbortController();
    void apiGet<{ object?: string; data?: FirmwareCatalogEntry[] }>('/v1/firmwares', { signal: controller.signal })
      .then((res) => {
        if (res?.data && res.data.length > 0) {
          setFirmwares(res.data);
        }
      })
      .catch(() => {
        // Keep built-in FIRMWARE_PROFILES fallback when offline or unserved.
      });
    return () => controller.abort();
  }, []);

  const selectedFirmware = useMemo(
    () => firmwares.find((item) => item.id === firmware)
      ?? { id: firmware, label: firmware, summary: 'Detached firmware profile' },
    [firmwares, firmware],
  );
  const selectedDirectives = useMemo(
    () => Object.entries(selectedFirmware.directives ?? {}),
    [selectedFirmware],
  );
  const selectedFirmwareTooltip = useMemo(
    () => formatFirmwareTooltip(selectedFirmware),
    [selectedFirmware],
  );
  const lastUserPrompt = useMemo(
    () => [...messages].reverse().find((m) => m.role === 'user' && !m.streaming && !m.error)?.content ?? null,
    [messages],
  );



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

          <label className={styles.sessions}>
            <span>Firmware</span>
            <Select
              aria-label="Firmware profile"
              title={selectedFirmwareTooltip}
              value={firmware}
              onChange={(event) => setFirmware(event.target.value as FirmwareProfile)}
            >
              {firmwares.map((profile) => (
                <option key={profile.id} value={profile.id} title={formatFirmwareTooltip(profile)}>
                  {profile.label}
                </option>
              ))}
              {firmwares.every((profile) => profile.id !== firmware) && (
                <option value={firmware}>{firmware}</option>
              )}
            </Select>
          </label>

          <Tooltip>
            <TooltipTrigger asChild>
              <Badge title={selectedFirmwareTooltip}>
                fw: {selectedFirmware.id}
                {selectedDirectives.length > 0 ? ` (${selectedDirectives.length})` : ''}
              </Badge>
            </TooltipTrigger>
            <TooltipContent>
              <div><strong>{selectedFirmware.label}</strong></div>
              <div>{selectedFirmware.summary}</div>
              {selectedFirmware.id_hex && (
                <div>blake3: <code>{selectedFirmware.id_hex.slice(0, 16)}…</code></div>
              )}
              {selectedDirectives.length > 0 && (
                <div>{selectedDirectives.slice(0, 10).map(([k, v]) => `${k} ${v}`).join(' · ')}</div>
              )}
            </TooltipContent>
          </Tooltip>

          <label className={styles.sessions}>
            <span>Envelope</span>
            <Select
              aria-label="Compute envelope"
              value={envelope}
              onChange={(event) => setEnvelope(event.target.value as ComputeEnvelopePreset)}
            >
              {COMPUTE_ENVELOPES.map((item) => (
                <option key={item.id} value={item.id} title={item.bounds}>
                  {item.label}
                </option>
              ))}
            </Select>
          </label>

          <div className={styles.scopeBadges} aria-label="Authority scope and compute envelope">
            <Badge>
              authority:{' '}
              {authUser
                ? `${authUser.tenantId}`
                : servicePrincipal
                  ? `${servicePrincipal.kind}:${servicePrincipal.tenantId}`
                  : devPrincipal
                    ? `dev:${devPrincipal}`
                    : tenant}
            </Badge>
            <Badge>model: {model}</Badge>
          </div>

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

          {lastUserPrompt && (
            <Button
              variant="ghost"
              onClick={() => void retryWithQuote()}
              disabled={busy}
              title={`Re-run previous prompt using ${selectedFirmware.id}`}
            >
              Re-run with selected firmware
            </Button>
          )}

          <Button variant="ghost" onClick={clearConversation}>New</Button>

        </div>



        <ChatTranscript
          messages={messages}
          transcriptRef={transcriptRef}
          busy={busy}
          onRetry={() => void retryWithQuote()}
        />



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
