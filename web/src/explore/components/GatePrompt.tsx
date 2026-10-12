import { useEffect, useRef, useState } from 'react';
import { Button, ErrorText, Muted, Stack, Text } from '@ui';
import { apiGetCached, type PreflightQuoteResponse } from '../../api/client';
import { useAppStore } from '../../store';
import { preflight } from '../api';
import { useExploreStore } from '../store';
import type { BillingReceipt } from '../types';
import styles from './GatePrompt.module.css';

interface GatePromptProps {
  serviceId: string;
  label: string;
  units?: number;
  onReady: () => void | Promise<void>;
  receipt?: BillingReceipt | null;
}

export function GatePrompt(props: GatePromptProps) {
  const scope = useAppStore((state) => JSON.stringify([state.tenant, state.authUser?.id]));
  return <GatePromptBody key={JSON.stringify([scope, props.serviceId, props.units ?? 1])} {...props} />;
}

function GatePromptBody({ serviceId, label, units = 1, onReady, receipt }: GatePromptProps) {
  const { tenant } = useAppStore();
  const { setQuoteId } = useExploreStore();
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [quote, setQuote] = useState<PreflightQuoteResponse | null>(null);
  const [planHint, setPlanHint] = useState<string | null>(null);
  const [bypassed, setBypassed] = useState(false);
  const readyRef = useRef(onReady);
  const autoBypassKey = useRef<string | null>(null);
  const mounted = useRef(true);
  const unlocking = useRef(false);

  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; };
  }, []);

  useEffect(() => {
    readyRef.current = onReady;
  }, [onReady]);

  useEffect(() => {
    let active = true;
    const key = `${tenant}\0${serviceId}`;
    apiGetCached<{
      billing_enforced?: boolean;
      data?: { monthly_credits?: Record<string, number> }[];
    }>('/v1/billing/plans', 60_000, { tenant })
      .then(async (r) => {
        let max = 0;
        for (const plan of r.data ?? []) {
          const credits = plan.monthly_credits?.[serviceId];
          if (typeof credits === 'number' && credits > max) max = credits;
        }
        if (active && max > 0) setPlanHint(`Plans include up to ${max.toLocaleString()} ${serviceId}/mo`);

        // The development host explicitly keeps billing non-gating. Do not make
        // users manufacture a quote merely to view data the server will execute
        // without one. The public plans response is the server-authoritative
        // enforcement state, so production still renders the normal gate.
        if (!active || r.billing_enforced !== false || autoBypassKey.current === key) return;
        autoBypassKey.current = key;
        setBypassed(true);
        setBusy(true);
        setErr(null);
        try {
          await readyRef.current();
        } catch (e) {
          if (!active) return;
          autoBypassKey.current = null;
          setBypassed(false);
          setErr(e instanceof Error ? e.message : String(e));
        } finally {
          if (active) setBusy(false);
        }
      })
      // A configuration read must never manufacture a gate failure. If the API
      // is temporarily unavailable, leave the explicit control available.
      .catch(() => {});
    return () => { active = false; };
  }, [serviceId, tenant]);

  async function unlock() {
    if (unlocking.current) return;
    unlocking.current = true;
    setBusy(true);
    setErr(null);
    try {
      const q = await preflight(serviceId, tenant, units);
      if (!mounted.current) return;
      setQuote(q);
      if (q.quote_id) setQuoteId(q.quote_id);
      await onReady();
    } catch (e) {
      if (mounted.current) setErr(e instanceof Error ? e.message : String(e));
    } finally {
      unlocking.current = false;
      if (mounted.current) setBusy(false);
    }
  }

  return (
    <div className={styles.gate}>
      <Stack gap={3}>
        <Text>{label}</Text>
        {planHint ? <Muted>{planHint}</Muted> : null}
        <Button type="button" disabled={busy} loading={busy} onClick={() => void unlock()}>
          {busy ? (bypassed ? 'Loading…' : 'Preflight…') : `Unlock (${serviceId})`}
        </Button>
        {quote ? (
          <Muted className={styles.quoteDetail}>
            {(Number(quote.amount_cents ?? 0) / 100).toFixed(2)} {quote.currency} — status {quote.status}
            {quote.stripe_checkout_url ? (
              <>
                {' '}
                <a href={quote.stripe_checkout_url} target="_blank" rel="noreferrer">Stripe checkout</a>
              </>
            ) : quote.status === 'awaiting_manual_approval' ? (
              <span> (awaiting manual approval)</span>
            ) : null}
          </Muted>
        ) : null}
        {receipt ? (
          <Muted>
            Receipt: {receipt.amount_cents / 100} {receipt.currency} — {receipt.service_id}
          </Muted>
        ) : bypassed ? (
          <Muted>Billing bypass active — no charge applied.</Muted>
        ) : null}
        {err ? <ErrorText>{err}</ErrorText> : null}
      </Stack>
    </div>
  );
}
