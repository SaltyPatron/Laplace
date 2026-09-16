import { useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { Banner, Button, ErrorText, Muted, ReadStatus, Table, TableScroll, Td, Th, useReadResource } from '@ui';
import {
  apiGet, apiPost, type BillingCatalogResponse, type BillingPlansResponse,
  type PlanSubscribeResponse, type UsageResponse,
} from '../api/client';
import { AccountControls } from '../auth/AccountControls';
import { useAppStore } from '../store';
import styles from './BillingView.module.css';

function amount(cents: number | undefined | null, currency: string | undefined | null) {
  if (cents == null) return 'Not reported';
  const code = currency?.toUpperCase() || 'USD';
  try { return new Intl.NumberFormat(undefined, { style: 'currency', currency: code }).format(cents / 100); }
  catch { return `${(cents / 100).toFixed(2)} ${code}`; }
}

export function BillingView() {
  const { tenant, authReady, authUser } = useAppStore();
  // Reads and pending mutation receipts cannot cross a tenant or identity change.
  return <BillingWorkspace key={JSON.stringify([tenant, authReady, authUser?.id])} tenant={tenant} />;
}

function BillingWorkspace({ tenant }: { tenant: string }) {
  const { authUser, authProviders, authReady } = useAppStore();
  const signedIn = authReady && !!authUser;
  const plansRead = useReadResource({
    key: JSON.stringify(['billing-plans', tenant]),
    read: (signal) => apiGet<BillingPlansResponse>('/v1/billing/plans', { tenant, signal }),
  });
  const servicesRead = useReadResource({
    key: JSON.stringify(['billing-catalog', tenant]),
    read: (signal) => apiGet<BillingCatalogResponse>('/v1/billing/catalog', { tenant, signal }),
  });
  const usageRead = useReadResource({
    key: JSON.stringify(['billing-usage', tenant, authUser?.id]),
    enabled: signedIn,
    read: (signal) => apiGet<UsageResponse>('/v1/billing/usage', { tenant, signal }),
  });
  const accountRead = useReadResource({
    key: JSON.stringify(['billing-permissions', tenant, authUser?.id]),
    enabled: signedIn,
    read: (signal) => apiGet<{ tenantId: string; workspaces: { tenantId: string; role: string }[] }>(
      '/v1/account', { tenant, signal }),
  });
  const current = accountRead.data?.workspaces.find((workspace) => workspace.tenantId === tenant);
  const canManage = signedIn && accountRead.status === 'ready' && accountRead.data?.tenantId === tenant
    && (current?.role === 'owner' || current?.role === 'admin');
  const [checkout, setCheckout] = useState<PlanSubscribeResponse | null>(null);
  const [error, setError] = useState('');
  const [pendingPlan, setPendingPlan] = useState<string | null>(null);
  const submitting = useRef(false);
  const plans = (plansRead.data?.data ?? []).filter((plan) => plan.active !== false);
  const services = (servicesRead.data?.data ?? []).filter((service) => service.active !== false);
  const usage = usageRead.data;

  function refreshBilling() {
    setCheckout(null); setError('');
    void plansRead.refresh(); void servicesRead.refresh();
    if (signedIn) { void usageRead.refresh(); void accountRead.refresh(); }
  }

  async function subscribe(planId: string) {
    if (!planId || submitting.current || !canManage) return;
    submitting.current = true;
    setPendingPlan(planId);
    setError('');
    setCheckout(null);
    try {
      setCheckout(await apiPost<PlanSubscribeResponse>(
        `/v1/billing/plans/${encodeURIComponent(planId)}/subscribe`, { tenant }, { tenant }));
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : 'Subscription checkout failed.');
    } finally {
      submitting.current = false;
      setPendingPlan(null);
    }
  }

  return <div className={styles.root}>
    <h1>Workspace subscription and billing</h1>
    <p><Link to="/settings">Manage company settings, members, API keys, and invoices</Link></p>
    <Button variant="ghost" disabled={pendingPlan != null} onClick={refreshBilling}>Refresh billing</Button>
    {error && <ErrorText role="alert">{error}</ErrorText>}
    {!authReady ? <Muted>Loading account…</Muted> : !authUser ? <Banner>
      <p>Sign in to choose your company workspace and subscribe.</p>
      <AccountControls user={null} providers={authProviders} returnUrl="/billing" />
      {authProviders.length === 0 && <ErrorText>No browser sign-in provider is configured on this host.</ErrorText>}
    </Banner> : <>
      <p>Billing workspace: <strong>{tenant}</strong>. {!canManage && accountRead.status === 'ready'
        && 'Only workspace owners and administrators can purchase or change subscriptions.'}</p>
      <ReadStatus label="Workspace permissions" resource={accountRead} />
    </>}
    <h2>Plans</h2>
    <ReadStatus label="Plans" resource={plansRead} />
    {plansRead.data && plans.length === 0 && <Muted>No plans were returned by the catalog.</Muted>}
    <div className={styles.planGrid}>
      {plans.map((plan) => <div key={plan.plan_id} className={styles.planCard}>
        <h3>{plan.name}</h3>
        <p className={styles.price}>{plan.monthly_price_cents == null ? 'Price not reported' : `${amount(plan.monthly_price_cents, plan.currency)}/mo`}</p>
        <Muted>{plan.description}</Muted>
        <ul className={styles.credits}>
          {Object.entries(plan.monthly_credits ?? {}).map(([service, credits]) => <li key={service}>
            <span>{service}</span><span>{credits.toLocaleString()}</span>
          </li>)}
        </ul>
        <Button disabled={!canManage || !plan.plan_id || pendingPlan != null} loading={pendingPlan === plan.plan_id}
          onClick={() => void subscribe(plan.plan_id ?? '')}>Subscribe</Button>
      </div>)}
    </div>
    {checkout && <Banner>
      <strong>{checkout.plan_id}</strong> — {amount(checkout.amount_cents, checkout.currency)}, status {checkout.status}.{' '}
      {checkout.stripe_checkout_url ? <a href={checkout.stripe_checkout_url} target="_blank" rel="noreferrer">Continue to secure checkout</a>
        : <span>Checkout is unavailable. No subscription has been activated by this request.</span>}
    </Banner>}
    <h2>Metered services</h2>
    <ReadStatus label="Metered services" resource={servicesRead} />
    {servicesRead.data && services.length === 0 && <Muted>No services were returned by the catalog.</Muted>}
    {services.length > 0 && <TableScroll className={styles.catalogScroll}>
      <Table className={styles.catalog}>
        <thead><tr><Th>Service</Th><Th>Unit</Th><Th>Unit price</Th><Th>Base fee</Th></tr></thead>
        <tbody>{services.map((service) => <tr key={service.service_id}>
          <Td>{service.display_name}</Td><Td>{service.unit}</Td>
          <Td>{amount(service.unit_price_cents, service.currency)}</Td>
          <Td>{amount(service.base_fee_cents, service.currency)}</Td>
        </tr>)}</tbody>
      </Table>
    </TableScroll>}
    <h2>Usage — {tenant}</h2>
    {!signedIn ? <Muted>Sign in to view workspace usage.</Muted> : <ReadStatus label="Usage" resource={usageRead} />}
    {usage && (usage.entries?.length ? <>
      <p>Recorded total: {usage.total_amount_cents == null ? 'Not reported' : (Number(usage.total_amount_cents) / 100).toFixed(2)}</p>
      <TableScroll className={styles.catalogScroll}>
        <Table className={styles.catalog}>
          <thead><tr><Th>Service</Th><Th>Units</Th><Th>Recorded amount</Th><Th>Executed</Th></tr></thead>
          <tbody>{usage.entries.map((entry, index) => <tr key={index}>
            <Td>{entry.serviceId}</Td><Td>{entry.units}</Td>
            <Td>{entry.amountCents == null ? 'Not reported' : (Number(entry.amountCents) / 100).toFixed(2)}</Td>
            <Td>{entry.executedAt ? new Date(entry.executedAt).toLocaleString() : 'Not reported'}</Td>
          </tr>)}</tbody>
        </Table>
      </TableScroll>
      <Muted>Historical usage rows do not include currency. Stripe invoices show the billed currency and final invoice totals.</Muted>
    </> : <Muted>No usage recorded for this tenant.</Muted>)}
  </div>;
}
