import { useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { Banner, Button, ErrorText, Muted, ReadStatus, Table, TableScroll, Td, Th, useReadResource } from '@ui';
import {
  apiGet, apiGetCached, apiPost, type BillingCatalogResponse, type BillingPlansResponse,
  type EntitlementsResponse, type PlanSubscribeResponse, type UsageResponse,
} from '../api/client';
import { AccountControls } from '../auth/AccountControls';
import { useAppStore } from '../store';
import { formatCents, formatCurrencyCents as amount } from './amounts';
import styles from './BillingView.module.css';

export function BillingView() {
  const { tenant, authUser } = useAppStore();
  // Reads and pending mutation receipts cannot cross a tenant or identity change.
  // Auth readiness alone must not remount public catalog reads and duplicate requests.
  return <BillingWorkspace key={JSON.stringify([tenant, authUser?.id])} tenant={tenant} />;
}

function BillingWorkspace({ tenant }: { tenant: string }) {
  const { authUser, authProviders, authReady } = useAppStore();
  const signedIn = authReady && !!authUser;
  const plansRead = useReadResource({
    key: JSON.stringify(['billing-plans', tenant]),
    read: (signal) => apiGetCached<BillingPlansResponse>('/v1/billing/plans', 60_000, { tenant, signal }),
  });
  const servicesRead = useReadResource({
    key: JSON.stringify(['billing-catalog', tenant]),
    read: (signal) => apiGetCached<BillingCatalogResponse>('/v1/billing/catalog', 60_000, { tenant, signal }),
  });
  const usageRead = useReadResource({
    key: JSON.stringify(['billing-usage', tenant, authUser?.id]),
    enabled: signedIn,
    read: (signal) => apiGet<UsageResponse>('/v1/billing/usage', { tenant, signal }),
  });
  const entitlementsRead = useReadResource({
    key: JSON.stringify(['billing-entitlements', tenant, authUser?.id]),
    enabled: signedIn,
    read: (signal) => apiGet<EntitlementsResponse>('/v1/billing/entitlements', { tenant, signal }),
  });
  const accountRead = useReadResource({
    key: JSON.stringify(['billing-permissions', tenant, authUser?.id]),
    enabled: signedIn,
    read: (signal) => apiGet<{
      tenantId: string;
      workspaces: { tenantId: string; role: string }[];
      configuration?: { billingEnforced: boolean; stripeConfigured: boolean; stripeMode: string; commercialCatalogApproved: boolean };
    }>(
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
  const entitlements = entitlementsRead.data?.data ?? [];
  const managedSubscription = entitlements.find((entry) =>
    !!entry.stripe_subscription_id && ['active', 'trialing', 'past_due', 'unpaid', 'incomplete', 'paused'].includes(entry.status));
  const catalogCurrencies = [...new Set([
    ...plans.map((plan) => plan.currency), ...services.map((service) => service.currency),
  ].filter(Boolean))];
  const usageCurrency = catalogCurrencies.length === 1 ? catalogCurrencies[0] : null;
  const checkoutEnabled = accountRead.data?.configuration?.stripeMode !== 'live'
    || accountRead.data.configuration?.commercialCatalogApproved === true;

  function refreshBilling() {
    setCheckout(null); setError('');
    void plansRead.refresh(); void servicesRead.refresh();
    if (signedIn) { void usageRead.refresh(); void entitlementsRead.refresh(); void accountRead.refresh(); }
  }

  async function subscribe(planId: string) {
    if (!planId || submitting.current || !canManage) return;
    submitting.current = true;
    setPendingPlan(planId);
    setError('');
    setCheckout(null);
    try {
      const result = await apiPost<PlanSubscribeResponse>(
        `/v1/billing/plans/${encodeURIComponent(planId)}/subscribe`, { tenant }, { tenant });
      setCheckout(result);
      if (result.stripe_checkout_url) window.location.assign(result.stripe_checkout_url);
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : 'Subscription checkout failed.');
    } finally {
      submitting.current = false;
      setPendingPlan(null);
    }
  }

  async function manageBilling() {
    if (submitting.current || !canManage) return;
    submitting.current = true; setPendingPlan('portal'); setError('');
    try {
      const result = await apiPost<{ url: string }>('/v1/billing/portal', {}, { tenant });
      window.location.assign(result.url);
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : 'Billing management failed.');
    } finally {
      submitting.current = false; setPendingPlan(null);
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
    {accountRead.data?.configuration?.stripeMode === 'sandbox' && <Banner>
      <strong>Stripe sandbox.</strong> Checkout, signed webhooks, subscription state, invoices, and the customer portal are exercised here without moving real money.
    </Banner>}
    {accountRead.data?.configuration && !accountRead.data.configuration.billingEnforced && <Banner>
      <strong>Development access remains open.</strong> Payment does not unlock or withhold functionality on this host; subscriptions can be tested without turning development into a paywall.
    </Banner>}
    {accountRead.data?.configuration?.stripeMode === 'live' && !accountRead.data.configuration.commercialCatalogApproved && <Banner>
      <strong>Live checkout is intentionally disabled.</strong> The operator must approve the measured commercial catalog before Laplace can accept real payments; development and existing account access remain available.
    </Banner>}
    <p className={styles.principle}>Every plan addresses the same witnessed Laplace knowledge world. Plans may change declared service allowances, execution depth and breadth, concurrency, and support—not what Laplace is permitted to know.</p>
    {signedIn && <section className={styles.current} aria-labelledby="current-subscription">
      <div className={styles.sectionHead}>
        <h2 id="current-subscription">Current subscription</h2>
        {canManage && managedSubscription && <Button variant="ghost" loading={pendingPlan === 'portal'}
          disabled={pendingPlan != null} onClick={() => void manageBilling()}>Manage subscription and invoices</Button>}
      </div>
      <ReadStatus label="Subscription" resource={entitlementsRead} />
      {entitlementsRead.data && entitlements.length === 0 && <Muted>No subscription is recorded for this workspace.</Muted>}
      {entitlements.map((entry) => <div className={styles.entitlement} key={`${entry.plan_id}:${entry.stripe_subscription_id ?? 'none'}`}>
        <div><strong>{entry.plan_id}</strong> · {entry.status}</div>
        <Muted>{new Date(entry.period_start).toLocaleDateString()} through {new Date(entry.period_end).toLocaleDateString()}</Muted>
        {Object.keys(entry.monthly_credits ?? {}).length > 0 && <ul className={styles.credits} aria-label="Remaining included service allowances">
          {Object.entries(entry.monthly_credits).map(([service, allowance]) => {
            const used = entry.used_credits?.[service] ?? 0;
            return <li key={service}><span>{service}</span><span>{remaining(allowance, used)} remaining</span></li>;
          })}
        </ul>}
      </div>)}
    </section>}
    <h2>Plans</h2>
    <ReadStatus label="Plans" resource={plansRead} />
    {plansRead.data && plans.length === 0 && <Muted>No plans were returned by the catalog.</Muted>}
    <div className={styles.planGrid}>
      {plans.map((plan) => <div key={plan.plan_id} className={styles.planCard}>
        <h3>{plan.name}</h3>
        <p className={styles.price}>{plan.monthly_price_cents == null ? 'Price not reported' : `${amount(plan.monthly_price_cents, plan.currency)}/mo`}</p>
        <Muted>{plan.description}</Muted>
        <strong className={styles.allowanceTitle}>Included monthly service allowances</strong>
        <ul className={styles.credits}>
          {Object.entries(plan.monthly_credits ?? {}).map(([service, credits]) => <li key={service}>
            <span>{service}</span><span>{credits.toLocaleString()}</span>
          </li>)}
        </ul>
        <Muted>Renews monthly until canceled. Changes, invoices, payment methods, and cancellation are available in Stripe's customer portal.</Muted>
        {managedSubscription ? <Button disabled={pendingPlan != null || !canManage} loading={pendingPlan === 'portal'}
          onClick={() => void manageBilling()}>{managedSubscription.plan_id === plan.plan_id ? 'Current plan — manage' : 'Manage current plan'}</Button>
          : <Button disabled={!canManage || !checkoutEnabled || !plan.plan_id || pendingPlan != null} loading={pendingPlan === plan.plan_id}
            onClick={() => void subscribe(plan.plan_id ?? '')}>Subscribe — {amount(plan.monthly_price_cents, plan.currency)}/month</Button>}
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
      <p>Recorded total: {usageCurrency ? amount(usage.total_amount_cents, usageCurrency) : formatCents(usage.total_amount_cents)}</p>
      <TableScroll className={styles.catalogScroll}>
        <Table className={styles.catalog}>
          <thead><tr><Th>Service</Th><Th>Units</Th><Th>Recorded amount</Th><Th>Executed</Th></tr></thead>
          <tbody>{usage.entries.map((entry, index) => <tr key={index}>
            <Td>{entry.serviceId}</Td><Td>{entry.units}</Td>
            <Td>{amount(entry.amountCents, services.find((service) => service.service_id === entry.serviceId)?.currency ?? usageCurrency ?? 'usd')}</Td>
            <Td>{entry.executedAt ? new Date(entry.executedAt).toLocaleString() : 'Not reported'}</Td>
          </tr>)}</tbody>
        </Table>
      </TableScroll>
      <Muted>Laplace records admitted work and its quoted amount. Stripe invoices remain the authoritative paid total.</Muted>
    </> : <Muted>No usage recorded for this tenant.</Muted>)}
  </div>;
}

function remaining(allowance: number | string, used: number | string): string {
  try { return (BigInt(allowance) - BigInt(used)).toLocaleString(); }
  catch { return '—'; }
}
