import { useEffect, useState } from 'react';
import { Button, ErrorText, Muted, Stack, Text } from '@ui';
import { apiGet, type PreflightQuoteResponse } from '../../api/client';
import { useAppStore } from '../../store';
import { preflight } from '../api';
import { useExploreStore } from '../store';
import type { BillingReceipt } from '../types';
import styles from './GatePrompt.module.css';

export function GatePrompt({
  serviceId,
  label,
  units = 1,
  onReady,
  receipt,
}: {
  serviceId: string;
  label: string;
  units?: number;
  onReady: () => void | Promise<void>;
  receipt?: BillingReceipt | null;
}) {
  const { tenant } = useAppStore();
  const { setQuoteId } = useExploreStore();
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [quote, setQuote] = useState<PreflightQuoteResponse | null>(null);
  const [planHint, setPlanHint] = useState<string | null>(null);
  const [bypassed, setBypassed] = useState(false);

  useEffect(() => {
    apiGet<{ data?: { monthly_credits?: Record<string, number> }[] }>('/v1/billing/plans')
      .then((r) => {
        let max = 0;
        for (const plan of r.data ?? []) {
          const credits = plan.monthly_credits?.[serviceId];
          if (typeof credits === 'number' && credits > max) max = credits;
        }
        if (max > 0) setPlanHint(`Plans include up to ${max.toLocaleString()} ${serviceId}/mo`);
      })
      .catch(() => {});
  }, [serviceId]);

  async function unlock() {
    setBusy(true);
    setErr(null);
    try {
      const q = await preflight(serviceId, tenant, units);
      setQuote(q);
      if (q.quote_id) setQuoteId(q.quote_id);
      await onReady();
      if (!receipt) setBypassed(true);
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className={styles.gate}>
      <Stack gap={3}>
        <Text>{label}</Text>
        {planHint ? <Muted>{planHint}</Muted> : null}
        <Button type="button" disabled={busy} loading={busy} onClick={() => void unlock()}>
          {busy ? 'Preflight…' : `Unlock (${serviceId})`}
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
