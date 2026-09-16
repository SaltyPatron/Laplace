import { useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { Banner, Button, ErrorText, Muted, Table, TableScroll, Td, Th } from '@ui';
import {
  apiGet, apiPost, type BillingCatalogResponse, type BillingPlansResponse,
  type CatalogServiceView, type PlanSubscribeResponse, type PlanView, type UsageResponse,
} from '../api/client';
import { AccountControls } from '../auth/AccountControls';
import { useAppStore } from '../store';
import { formatCents, formatCurrencyCents } from './amounts';
import styles from './BillingView.module.css';

export function BillingView() {
  const { tenant, authUser, authProviders, authReady } = useAppStore();
  const [plans, setPlans] = useState<PlanView[] | null>(null);
  const [services, setServices] = useState<CatalogServiceView[] | null>(null);
  const [usage, setUsage] = useState<UsageResponse | null>(null);
  const [checkout, setCheckout] = useState<PlanSubscribeResponse | null>(null);
  const [catalogError, setCatalogError] = useState('');
  const [usageError, setUsageError] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState('');
  const submitting = useRef(false);
  const [refresh, setRefresh] = useState(0);
  const [canManage, setCanManage] = useState(false);
  const [accountError, setAccountError] = useState('');

  useEffect(() => {
    const controller = new AbortController();
    setCatalogError('');
    void Promise.all([
      apiGet<BillingPlansResponse>('/v1/billing/plans', { signal: controller.signal }),
      apiGet<BillingCatalogResponse>('/v1/billing/catalog', { signal: controller.signal }),
    ]).then(([planResult, serviceResult]) => {
      if (controller.signal.aborted) return;
      setPlans(planResult.data ?? []); setServices(serviceResult.data ?? []);
    }).catch((failure: unknown) => {
      if (!controller.signal.aborted) setCatalogError(failure instanceof Error ? failure.message : 'Unable to load the billing catalog.');
    });
    return () => controller.abort();
  }, [refresh]);

  useEffect(() => {
    setCheckout(null); setUsage(null); setUsageError(''); setCanManage(false); setAccountError('');
    if (!authReady || !authUser) return;
    const controller = new AbortController();
    void apiGet<UsageResponse>('/v1/billing/usage', { tenant, signal: controller.signal })
      .then((result) => { if (!controller.signal.aborted) setUsage(result); })
      .catch((failure: unknown) => {
        if (!controller.signal.aborted) setUsageError(failure instanceof Error ? failure.message : 'Unable to load usage.');
      });
    void apiGet<{ tenantId: string; workspaces: { tenantId: string; role: string }[] }>('/v1/account', { signal: controller.signal })
      .then((result) => {
        if (controller.signal.aborted) return;
        const current = result.workspaces.find((workspace) => workspace.tenantId === result.tenantId);
        setCanManage(current?.role === 'owner' || current?.role === 'admin');
      }).catch((failure: unknown) => {
        if (!controller.signal.aborted) setAccountError(failure instanceof Error ? failure.message : 'Unable to load workspace permissions.');
      });
    return () => controller.abort();
  }, [tenant, authUser, authReady, refresh]);

  async function subscribe(planId: string) {
    if (submitting.current || !canManage) return;
    submitting.current = true; setBusy(planId); setError(''); setCheckout(null);
    try {
      const result = await apiPost<PlanSubscribeResponse>(
        `/v1/billing/plans/${encodeURIComponent(planId)}/subscribe`, { tenant }, { tenant });
      setCheckout(result);
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : 'Subscription checkout failed.');
    } finally { submitting.current = false; setBusy(''); }
  }

  return (
    <div className={styles.root}>
      <h1>Workspace subscription and billing</h1>
      <p><Link to="/settings">Manage company settings, members, API keys, and invoices</Link></p>
      <Button variant="ghost" disabled={!!busy} onClick={() => setRefresh((n) => n + 1)}>Refresh billing</Button>
      {error && <ErrorText>{error}</ErrorText>}
      {catalogError && <ErrorText>{catalogError}</ErrorText>}
      {accountError && <ErrorText>{accountError}</ErrorText>}
      {!authReady ? <Muted>Loading account…</Muted> : !authUser ? <Banner>
        <p>Sign in to choose your company workspace and subscribe.</p>
        <AccountControls user={null} providers={authProviders} returnUrl="/billing" />
        {authProviders.length === 0 && <ErrorText>No browser sign-in provider is configured on this host.</ErrorText>}
      </Banner> : <p>Billing workspace: <strong>{tenant}</strong>. {!canManage && !accountError && 'Only workspace owners and administrators can purchase or change subscriptions.'}</p>}

      <h2>Plans</h2>
      {!plans && !catalogError && <Muted>Loading plans…</Muted>}
      <div className={styles.planGrid}>
        {plans?.filter((plan) => plan.active !== false).map((plan) => (
          <div key={plan.plan_id} className={styles.planCard}>
            <h3>{plan.name}</h3>
            <p className={styles.price}>{formatCurrencyCents(plan.monthly_price_cents, plan.currency)}/month</p>
            <Muted>{plan.description}</Muted>
            <ul className={styles.credits}>
              {Object.entries(plan.monthly_credits ?? {}).map(([service, credits]) => (
                <li key={service}><span>{service}</span><span>{credits.toLocaleString()}</span></li>
              ))}
            </ul>
            <Button disabled={!canManage || !!busy || !plan.plan_id}
              onClick={() => void subscribe(plan.plan_id ?? '')}>
              {busy === plan.plan_id ? 'Opening checkout…' : 'Subscribe'}
            </Button>
          </div>
        ))}
      </div>

      {checkout && <Banner>
        <strong>{checkout.plan_id}</strong> — {formatCurrencyCents(checkout.amount_cents, checkout.currency)}, status {checkout.status}.{' '}
        {checkout.stripe_checkout_url ? <a href={checkout.stripe_checkout_url}>Continue to secure checkout</a> :
          <span>Checkout is unavailable. No subscription has been activated by this request.</span>}
      </Banner>}

      <h2>Service catalog</h2>
      {!services && !catalogError && <Muted>Loading services…</Muted>}
      {services && <TableScroll className={styles.catalogScroll}>
        <Table className={styles.catalog}>
          <thead><tr><Th>Service</Th><Th>Unit</Th><Th>Unit price</Th><Th>Base fee</Th></tr></thead>
          <tbody>{services.filter((service) => service.active !== false).map((service) => <tr key={service.service_id}>
            <Td>{service.display_name}</Td><Td>{service.unit}</Td>
            <Td>{formatCurrencyCents(service.unit_price_cents, service.currency)}</Td>
            <Td>{formatCurrencyCents(service.base_fee_cents, service.currency)}</Td>
          </tr>)}</tbody>
        </Table>
      </TableScroll>}

      <h2>Recorded usage</h2>
      {usageError && <ErrorText>{usageError}</ErrorText>}
      {!authUser ? <Muted>Sign in to view workspace usage.</Muted> : !usage && !usageError ? <Muted>Loading usage…</Muted> : null}
      {usage && usage.entries && usage.entries.length > 0 ? <TableScroll className={styles.catalogScroll}>
        <Table className={styles.catalog}>
          <thead><tr><Th>Service</Th><Th>Units</Th><Th>Recorded amount</Th><Th>Executed</Th></tr></thead>
          <tbody>{usage.entries.map((entry, index) => <tr key={index}>
            <Td>{entry.serviceId}</Td><Td>{entry.units}</Td>
            <Td>{formatCents(entry.amountCents)}</Td>
            <Td>{entry.executedAt ? new Date(entry.executedAt).toLocaleString() : '—'}</Td>
          </tr>)}</tbody>
        </Table>
        <Muted>Historical usage rows do not include currency. Stripe invoices show the billed currency and final invoice totals.</Muted>
      </TableScroll> : usage ? <Muted>No usage is recorded for this workspace.</Muted> : null}
    </div>
  );
}
