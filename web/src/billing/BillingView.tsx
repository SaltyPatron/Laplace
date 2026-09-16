import { useRef, useState } from 'react';
import { Banner, Button, ErrorText, Muted, ReadStatus, Table, TableScroll, Td, Th, useReadResource } from '@ui';
import {
  apiGet, apiPost, type BillingCatalogResponse, type BillingPlansResponse,
  type PlanSubscribeResponse, type UsageResponse,
} from '../api/client';
import { useAppStore } from '../store';
import styles from './BillingView.module.css';

export function BillingView() {
  const tenant = useAppStore((state) => state.tenant);
  // All view-local reads AND pending mutation receipts belong to this tenant.
  return <BillingWorkspace key={tenant} tenant={tenant} />;
}

function BillingWorkspace({ tenant }: { tenant: string }) {
  const plansRead = useReadResource({
    key: JSON.stringify(['billing-plans', tenant]),
    read: (signal) => apiGet<BillingPlansResponse>('/v1/billing/plans', { tenant, signal }),
  });
  const servicesRead = useReadResource({
    key: JSON.stringify(['billing-catalog', tenant]),
    read: (signal) => apiGet<BillingCatalogResponse>('/v1/billing/catalog', { tenant, signal }),
  });
  const usageRead = useReadResource({
    key: JSON.stringify(['billing-usage', tenant]),
    read: (signal) => apiGet<UsageResponse>('/v1/billing/usage', { tenant, signal }),
  });
  const [checkout, setCheckout] = useState<PlanSubscribeResponse | null>(null);
  const [error, setError] = useState('');
  const [pendingPlan, setPendingPlan] = useState<string | null>(null);
  const submitting = useRef(false);
  const plans = plansRead.data?.data ?? [];
  const services = servicesRead.data?.data ?? [];
  const usage = usageRead.data;

  async function subscribe(planId: string) {
    if (!planId || submitting.current) return;
    submitting.current = true;
    setPendingPlan(planId);
    setError('');
    setCheckout(null);
    try {
      setCheckout(await apiPost<PlanSubscribeResponse>(
        `/v1/billing/plans/${encodeURIComponent(planId)}/subscribe`, { tenant }, { tenant }));
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : 'Subscribe failed.');
    } finally {
      submitting.current = false;
      setPendingPlan(null);
    }
  }

  return <div className={styles.root}>
    {error && <ErrorText role="alert">{error}</ErrorText>}
    <h2>Plans</h2>
    <ReadStatus label="Plans" resource={plansRead} />
    {plansRead.data && plans.length === 0 && <Muted>No plans were returned by the catalog.</Muted>}
    <div className={styles.planGrid}>
      {plans.map((plan) => <div key={plan.plan_id} className={styles.planCard}>
        <h3>{plan.name}</h3>
        <p className={styles.price}>{plan.monthly_price_cents == null ? 'Price not reported' : `$${(Number(plan.monthly_price_cents) / 100).toFixed(2)}/mo`}</p>
        <Muted>{plan.description}</Muted>
        <ul className={styles.credits}>
          {Object.entries(plan.monthly_credits ?? {}).map(([service, credits]) => <li key={service}>
            <span>{service}</span><span>{credits.toLocaleString()}</span>
          </li>)}
        </ul>
        <Button disabled={!plan.plan_id || pendingPlan != null} loading={pendingPlan === plan.plan_id}
          onClick={() => void subscribe(plan.plan_id ?? '')}>Subscribe</Button>
      </div>)}
    </div>
    {checkout && <Banner>
      <strong>{checkout.plan_id}</strong> — {checkout.amount_cents == null ? 'Amount not reported' : (Number(checkout.amount_cents) / 100).toFixed(2)}{' '}
      {checkout.currency}, status {checkout.status}.{' '}
      {checkout.stripe_checkout_url ? <a href={checkout.stripe_checkout_url} target="_blank" rel="noreferrer">Complete checkout</a>
        : <span>No checkout URL was returned.</span>}
    </Banner>}
    <h2>Metered services</h2>
    <ReadStatus label="Metered services" resource={servicesRead} />
    {servicesRead.data && services.length === 0 && <Muted>No services were returned by the catalog.</Muted>}
    {services.length > 0 && <TableScroll className={styles.catalogScroll}>
      <Table className={styles.catalog}>
        <thead><tr><Th>Service</Th><Th>Unit</Th><Th>Unit price</Th><Th>Base fee</Th></tr></thead>
        <tbody>{services.map((service) => <tr key={service.service_id}>
          <Td>{service.display_name}</Td><Td>{service.unit}</Td>
          <Td>{service.unit_price_cents == null ? 'Not reported' : (Number(service.unit_price_cents) / 100).toFixed(2)} {service.currency}</Td>
          <Td>{service.base_fee_cents == null ? 'Not reported' : `${(Number(service.base_fee_cents) / 100).toFixed(2)} ${service.currency}`}</Td>
        </tr>)}</tbody>
      </Table>
    </TableScroll>}
    <h2>Usage — {tenant}</h2>
    <ReadStatus label="Usage" resource={usageRead} />
    {usage && (usage.entries?.length ? <>
      <p>Total: {usage.total_amount_cents == null ? 'Not reported' : (Number(usage.total_amount_cents) / 100).toFixed(2)} usd</p>
      <TableScroll className={styles.catalogScroll}>
        <Table className={styles.catalog}>
          <thead><tr><Th>Service</Th><Th>Units</Th><Th>Amount</Th><Th>Executed</Th></tr></thead>
          <tbody>{usage.entries.map((entry, index) => <tr key={index}>
            <Td>{entry.serviceId}</Td><Td>{entry.units}</Td>
            <Td>{entry.amountCents == null ? 'Not reported' : (Number(entry.amountCents) / 100).toFixed(2)}</Td>
            <Td>{entry.executedAt ? new Date(entry.executedAt).toLocaleString() : 'Not reported'}</Td>
          </tr>)}</tbody>
        </Table>
      </TableScroll>
    </> : <Muted>No usage recorded for this tenant.</Muted>)}
  </div>;
}
