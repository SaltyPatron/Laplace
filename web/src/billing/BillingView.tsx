import { useEffect, useState } from 'react';
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
      {error && <p className="error">{error}</p>}

      <h2>Plans</h2>
      <div className="plan-grid">
        {plans.map((p) => (
          <div key={p.plan_id} className="plan-card">
            <h3>{p.name}</h3>
            <p className="price">${(((p.monthly_price_cents ?? 0) as number) / 100).toFixed(0)}/mo</p>
            <p className="hint">{p.description}</p>
            <ul className="credits">
              {Object.entries(p.monthly_credits ?? {}).map(([service, credits]) => (
                <li key={service}>
                  <span>{service}</span>
                  <span>{credits.toLocaleString()}</span>
                </li>
              ))}
            </ul>
            <button onClick={() => void subscribe(p.plan_id ?? '')}>Subscribe</button>
          </div>
        ))}
      </div>

      {checkout && (
        <div className="quote-banner">
          <strong>{checkout.plan_id}</strong> — {(((checkout.amount_cents ?? 0) as number) / 100).toFixed(2)}{' '}
          {checkout.currency}, status {checkout.status}.{' '}
          {checkout.stripe_checkout_url ? (
            <a href={checkout.stripe_checkout_url} target="_blank" rel="noreferrer">Complete checkout</a>
          ) : (
            <span>Stripe is not configured; the quote awaits manual approval.</span>
          )}
        </div>
      )}

      <h2>Metered services</h2>
      <table className="catalog">
        <thead>
          <tr><th>Service</th><th>Unit</th><th>Unit price</th><th>Base fee</th></tr>
        </thead>
        <tbody>
          {services.map((s) => (
            <tr key={s.service_id}>
              <td>{s.display_name}</td>
              <td>{s.unit}</td>
              <td>{(((s.unit_price_cents ?? 0) as number) / 100).toFixed(2)} {s.currency}</td>
              <td>{s.base_fee_cents ? `${((s.base_fee_cents as number) / 100).toFixed(2)} ${s.currency}` : '—'}</td>
            </tr>
          ))}
        </tbody>
      </table>

      <h2>Usage — {tenant}</h2>
      {usage && usage.entries && usage.entries.length > 0 ? (
        <>
          <p>Total: {(((usage.total_amount_cents ?? 0) as number) / 100).toFixed(2)} usd</p>
          <table className="catalog">
            <thead>
              <tr><th>Service</th><th>Units</th><th>Amount</th><th>Executed</th></tr>
            </thead>
            <tbody>
              {usage.entries.map((u, i) => (
                <tr key={i}>
                  <td>{u.serviceId}</td>
                  <td>{u.units}</td>
                  <td>{(((u.amountCents ?? 0) as number) / 100).toFixed(2)}</td>
                  <td>{u.executedAt ? new Date(u.executedAt).toLocaleString() : ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      ) : (
        <p className="hint">No usage recorded for this tenant.</p>
      )}
    </div>
  );
}
