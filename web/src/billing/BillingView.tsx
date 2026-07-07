import { useEffect, useState } from 'react';
import {
  Banner,
  Button,
  ErrorText,
  Muted,
  Table,
  TableScroll,
  Td,
  Th,
} from '@ui';
import {
  apiGet,
  apiPost,
  type BillingCatalogResponse,
  type BillingPlansResponse,
  type CatalogServiceView,
  type PlanSubscribeResponse,
  type PlanView,
  type UsageResponse,
} from '../api/client';
import { useAppStore } from '../store';

export function BillingView() {
  const tenant = useAppStore((s) => s.tenant);
  const [plans, setPlans] = useState<PlanView[]>([]);
  const [services, setServices] = useState<CatalogServiceView[]>([]);
  const [usage, setUsage] = useState<UsageResponse | null>(null);
  const [checkout, setCheckout] = useState<PlanSubscribeResponse | null>(null);
  const [error, setError] = useState('');

  useEffect(() => {
    apiGet<BillingPlansResponse>('/v1/billing/plans')
      .then((r) => setPlans(r.data ?? []))
      .catch(() => setError('Failed to load plans.'));
    apiGet<BillingCatalogResponse>('/v1/billing/catalog')
      .then((r) => setServices(r.data ?? []))
      .catch(() => {});
  }, []);

  useEffect(() => {
    apiGet<UsageResponse>('/v1/billing/usage', { tenant })
      .then(setUsage)
      .catch(() => {});
  }, [tenant]);

  async function subscribe(planId: string) {
    setError('');
    setCheckout(null);
    try {
      setCheckout(await apiPost<PlanSubscribeResponse>(
        `/v1/billing/plans/${encodeURIComponent(planId)}/subscribe`, { tenant }, { tenant }));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Subscribe failed.');
    }
  }

  return (
    <div className="billing">
      {error && <ErrorText>{error}</ErrorText>}

      <h2>Plans</h2>
      <div className="plan-grid">
        {plans.map((p) => (
          <div key={p.plan_id} className="plan-card">
            <h3>{p.name}</h3>
            <p className="price">${(((p.monthly_price_cents ?? 0) as number) / 100).toFixed(0)}/mo</p>
            <Muted>{p.description}</Muted>
            <ul className="credits">
              {Object.entries(p.monthly_credits ?? {}).map(([service, credits]) => (
                <li key={service}>
                  <span>{service}</span>
                  <span>{credits.toLocaleString()}</span>
                </li>
              ))}
            </ul>
            <Button onClick={() => void subscribe(p.plan_id ?? '')}>Subscribe</Button>
          </div>
        ))}
      </div>

      {checkout && (
        <Banner>
          <strong>{checkout.plan_id}</strong> — {(((checkout.amount_cents ?? 0) as number) / 100).toFixed(2)}{' '}
          {checkout.currency}, status {checkout.status}.{' '}
          {checkout.stripe_checkout_url ? (
            <a href={checkout.stripe_checkout_url} target="_blank" rel="noreferrer">Complete checkout</a>
          ) : (
            <span>Stripe is not configured; the quote awaits manual approval.</span>
          )}
        </Banner>
      )}

      <h2>Metered services</h2>
      <TableScroll>
        <Table className="catalog">
          <thead>
            <tr><Th>Service</Th><Th>Unit</Th><Th>Unit price</Th><Th>Base fee</Th></tr>
          </thead>
          <tbody>
            {services.map((s) => (
              <tr key={s.service_id}>
                <Td>{s.display_name}</Td>
                <Td>{s.unit}</Td>
                <Td>{(((s.unit_price_cents ?? 0) as number) / 100).toFixed(2)} {s.currency}</Td>
                <Td>{s.base_fee_cents ? `${((s.base_fee_cents as number) / 100).toFixed(2)} ${s.currency}` : '—'}</Td>
              </tr>
            ))}
          </tbody>
        </Table>
      </TableScroll>

      <h2>Usage — {tenant}</h2>
      {usage && usage.entries && usage.entries.length > 0 ? (
        <>
          <p>Total: {(((usage.total_amount_cents ?? 0) as number) / 100).toFixed(2)} usd</p>
          <TableScroll>
            <Table className="catalog">
              <thead>
                <tr><Th>Service</Th><Th>Units</Th><Th>Amount</Th><Th>Executed</Th></tr>
              </thead>
              <tbody>
                {usage.entries.map((u, i) => (
                  <tr key={i}>
                    <Td>{u.serviceId}</Td>
                    <Td>{u.units}</Td>
                    <Td>{(((u.amountCents ?? 0) as number) / 100).toFixed(2)}</Td>
                    <Td>{u.executedAt ? new Date(u.executedAt).toLocaleString() : ''}</Td>
                  </tr>
                ))}
              </tbody>
            </Table>
          </TableScroll>
        </>
      ) : (
        <Muted>No usage recorded for this tenant.</Muted>
      )}
    </div>
  );
}
