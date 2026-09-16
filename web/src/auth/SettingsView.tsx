import { useEffect, useRef, useState } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { Button, ErrorText, Input, Muted, Panel } from '@ui';
import { apiDelete, apiGet, apiPost, apiPut } from '../api/client';
import { useAppStore } from '../store';
import { AccountControls } from './AccountControls';
import styles from './SettingsView.module.css';

interface Workspace { tenantId: string; displayName: string; kind: string; role: string }
interface Subscription { planId: string; status: string; periodStart: string; periodEnd: string }
interface Account {
  userId: string; tenantId: string; displayName: string | null; email: string | null; provider: string;
  workspaces: Workspace[]; subscriptions: Subscription[];
  configuration: {
    authMode: string; billingEnforced: boolean; billingStore: string; stripeConfigured: boolean;
    publicBaseUrl: string | null; persistentSessionKeys: boolean; privateDataIsolation: boolean;
    providers: { id: string; name: string; callbackPath: string }[];
  };
}
interface Member { userId: string; displayName: string | null; email: string | null; role: string }
interface Invitation { invitationId: string; role: string; expiresAt: string }
interface WebSession { sessionId: string; createdAt: string; lastSeenAt: string; expiresAt: string; current: boolean }
interface ApiKey { key_prefix: string; label: string | null; created_at: string; revoked_at: string | null }

function useRemote<T>(path: string | null, revision: number) {
  const [state, setState] = useState<{ value: T | null; error: string; loading: boolean }>({ value: null, error: '', loading: false });
  useEffect(() => {
    if (!path) { setState({ value: null, error: '', loading: false }); return; }
    const controller = new AbortController();
    setState({ value: null, error: '', loading: true });
    void apiGet<T>(path, { signal: controller.signal }).then(
      (value) => { if (!controller.signal.aborted) setState({ value, error: '', loading: false }); },
      (error: unknown) => {
        if (!controller.signal.aborted) setState({ value: null, error: error instanceof Error ? error.message : 'Request failed.', loading: false });
      },
    );
    return () => controller.abort();
  }, [path, revision]);
  return state;
}

const date = (value: string) => new Date(value).toLocaleString();

export function SettingsView() {
  const { authReady, authUser, authProviders, tenant } = useAppStore();
  const location = useLocation();
  const [revision, refresh] = useState(0);
  const [busy, setBusy] = useState('');
  const busyRef = useRef(false);
  const [error, setError] = useState('');
  const [message, setMessage] = useState('');
  const [workspaceName, setWorkspaceName] = useState('');
  const [rename, setRename] = useState('');
  const [keyLabel, setKeyLabel] = useState('');
  const [secret, setSecret] = useState('');
  const [inviteLink, setInviteLink] = useState('');
  const [inviteRole, setInviteRole] = useState('member');
  const [joinToken, setJoinToken] = useState(() => new URLSearchParams(window.location.hash.slice(1)).get('join') ?? '');
  const account = useRemote<Account>(authReady && authUser ? '/v1/account' : null, revision);
  const active = account.value?.workspaces.find((w) => w.tenantId === account.value?.tenantId);
  const manages = active?.role === 'owner' || active?.role === 'admin';
  const members = useRemote<{ members: Member[] }>(manages ? '/v1/account/members' : null, revision);
  const invitations = useRemote<{ invitations: Invitation[] }>(manages ? '/v1/account/invitations' : null, revision);
  const keys = useRemote<{ keys: ApiKey[] }>(manages ? '/v1/billing/keys' : null, revision);
  const sessions = useRemote<{ sessions: WebSession[] }>(authReady && authUser ? '/v1/auth/sessions' : null, revision);

  useEffect(() => { setSecret(''); setInviteLink(''); }, [tenant, authUser?.id]);
  useEffect(() => { setRename(active?.displayName ?? ''); }, [active?.displayName]);

  async function run(name: string, operation: () => Promise<void>) {
    if (busyRef.current) return;
    busyRef.current = true;
    setBusy(name); setError(''); setMessage('');
    try { await operation(); }
    catch (failure) { setError(failure instanceof Error ? failure.message : 'The change failed.'); }
    finally { busyRef.current = false; setBusy(''); }
  }
  async function selectWorkspace(id: string) {
    await apiPost(`/v1/account/workspaces/${encodeURIComponent(id)}/select`, {});
    // Reload clears all old-workspace explorer, conversation and billing state.
    window.location.assign('/settings');
  }
  const returnUrl = `${location.pathname}${location.search}${location.hash}`;

  if (!authReady) return <div className={styles.root}><Muted>Loading account…</Muted></div>;
  if (!authUser) return (
    <div className={styles.root}>
      <h1>Account and company settings</h1>
      <p>Sign in to select a workspace, subscribe, manage members, and control access.</p>
      <AccountControls user={null} providers={authProviders} returnUrl={returnUrl} />
      {authProviders.length === 0 && <ErrorText>This host has no configured sign-in provider.</ErrorText>}
    </div>
  );

  return (
    <div className={styles.root}>
      <header className={styles.header}>
        <div><h1>Account and company settings</h1><Muted>{authUser.displayName || authUser.email}</Muted></div>
        <Button variant="ghost" disabled={!!busy} onClick={() => refresh((n) => n + 1)}>Refresh</Button>
      </header>
      {error && <ErrorText>{error}</ErrorText>}
      {message && <p role="status">{message}</p>}
      {account.loading && <Muted>Loading workspace and subscription…</Muted>}
      {account.error && <ErrorText>{account.error}</ErrorText>}

      {joinToken && <Panel title="Workspace invitation">
        <p>This one-use invitation grants access to the inviting workspace. Accept only an invitation from someone you trust.</p>
        <Button disabled={!!busy} onClick={() => void run('join', async () => {
          const result = await apiPost<{ tenantId: string }>('/v1/account/invitations/accept', { token: joinToken });
          setJoinToken(''); window.history.replaceState(null, '', '/settings');
          await selectWorkspace(result.tenantId);
        })}>Accept invitation</Button>
      </Panel>}

      {account.value && <>
        <Panel title="Workspaces">
          <label className={styles.field}>Current workspace
            <select value={account.value.tenantId} disabled={!!busy} onChange={(event) => {
              const value = event.target.value;
              void run('workspace', () => selectWorkspace(value));
            }}>
              {account.value.workspaces.map((workspace) => <option key={workspace.tenantId} value={workspace.tenantId}>
                {workspace.displayName} · {workspace.role}
              </option>)}
            </select>
          </label>
          <Muted>Workspace ID: {account.value.tenantId}. Your account ID: {account.value.userId}.</Muted>
          {manages && <form className={styles.form} onSubmit={(event) => { event.preventDefault(); void run('rename', async () => {
            await apiPut('/v1/account/workspace', { name: rename }); refresh((n) => n + 1); setMessage('Workspace name saved.');
          }); }}>
            <label className={styles.field}>Workspace name<Input value={rename} onChange={(e) => setRename(e.target.value)} required maxLength={160} /></label>
            <Button type="submit" disabled={!!busy}>Save name</Button>
          </form>}
          <form className={styles.form} onSubmit={(event) => { event.preventDefault(); void run('create', async () => {
            const created = await apiPost<Workspace>('/v1/account/workspaces', { name: workspaceName });
            await selectWorkspace(created.tenantId);
          }); }}>
            <label className={styles.field}>New company workspace<Input value={workspaceName} onChange={(e) => setWorkspaceName(e.target.value)} required maxLength={160} placeholder="Company name" /></label>
            <Button type="submit" disabled={!!busy}>Create workspace</Button>
          </form>
        </Panel>

        <Panel title="Subscription">
          {account.value.subscriptions.length === 0 ? <p>No subscription is recorded for this workspace.</p> :
            account.value.subscriptions.map((subscription) => <div className={styles.row} key={subscription.planId}>
              <div><strong>{subscription.planId}</strong> · {subscription.status}<br /><Muted>Current period ends {date(subscription.periodEnd)}</Muted></div>
            </div>)}
          <div className={styles.actions}>
            <Link to="/billing">View plans and usage</Link>
            {manages && <Button disabled={!!busy || !account.value.configuration.stripeConfigured} onClick={() => void run('portal', async () => {
              const result = await apiPost<{ url: string }>('/v1/billing/portal', {}); window.location.assign(result.url);
            })}>Manage subscription and invoices</Button>}
          </div>
          {!account.value.configuration.stripeConfigured && <ErrorText>Stripe is not configured on this host. Payment cannot be completed here yet.</ErrorText>}
        </Panel>

        {manages && <Panel title="Company members">
          {members.loading && <Muted>Loading members…</Muted>}
          {members.error && <ErrorText>{members.error}</ErrorText>}
          {members.value?.members.map((member) => <div className={styles.row} key={member.userId}>
            <div><strong>{member.displayName || member.email || member.userId}</strong><br /><Muted>{member.email} · {member.role}</Muted></div>
            {member.userId !== account.value?.userId && (active?.role === 'owner' || member.role === 'member') &&
              <Button variant="ghost" disabled={!!busy} onClick={() => {
                if (!window.confirm(`Remove ${member.displayName || member.email || member.userId} from this workspace?`)) return;
                void run('remove-member', async () => { await apiDelete(`/v1/account/members/${member.userId}`); refresh((n) => n + 1); });
              }}>Remove</Button>}
          </div>)}
          <p>Invitation links are single-use and expire in seven days. Anyone signed in who holds the link can join; send it privately.</p>
          <div className={styles.form}>
            <label className={styles.field}>Invitation role<select value={inviteRole} onChange={(e) => setInviteRole(e.target.value)}>
              <option value="member">Member</option>{active?.role === 'owner' && <option value="admin">Administrator</option>}
            </select></label>
            <Button disabled={!!busy} onClick={() => void run('invite', async () => {
              const result = await apiPost<{ invitationPath: string }>('/v1/account/invitations', { role: inviteRole });
              setInviteLink(new URL(result.invitationPath, window.location.origin).href); refresh((n) => n + 1);
            })}>Create invitation</Button>
          </div>
          {inviteLink && <label className={styles.field}>Copy this private invitation link<Input readOnly value={inviteLink} onFocus={(e) => e.currentTarget.select()} /></label>}
          {invitations.loading && <Muted>Loading invitations…</Muted>}
          {invitations.error && <ErrorText>{invitations.error}</ErrorText>}
          {invitations.value?.invitations.map((invitation) => <div className={styles.row} key={invitation.invitationId}>
            <span>{invitation.role} invitation · expires {date(invitation.expiresAt)}</span>
            <Button variant="ghost" disabled={!!busy} onClick={() => void run('revoke-invitation', async () => {
              await apiDelete(`/v1/account/invitations/${invitation.invitationId}`); setInviteLink(''); refresh((n) => n + 1);
            })}>Revoke</Button>
          </div>)}
          <Muted>Workspace API keys are separate service credentials. Rotate any key shared with a departing member.</Muted>
        </Panel>}

        {manages && <Panel title="API keys">
          <p>Use a workspace key with integrations that accept Laplace API-key authentication. Keys are shown only at creation.</p>
          <form className={styles.form} onSubmit={(event) => { event.preventDefault(); void run('key', async () => {
            const result = await apiPost<{ key: string }>('/v1/billing/keys', { label: keyLabel });
            setSecret(result.key); setKeyLabel(''); refresh((n) => n + 1);
          }); }}>
            <label className={styles.field}>Key label<Input value={keyLabel} onChange={(e) => setKeyLabel(e.target.value)} maxLength={160} placeholder="Integration or application" /></label>
            <Button type="submit" disabled={!!busy}>Create API key</Button>
          </form>
          {secret && <div className={styles.secret}>
            <label className={styles.field}>Save this key now<Input readOnly value={secret} autoComplete="off" onFocus={(e) => e.currentTarget.select()} /></label>
            <Button variant="ghost" onClick={() => setSecret('')}>Hide key</Button>
          </div>}
          {keys.loading && <Muted>Loading keys…</Muted>}
          {keys.error && <ErrorText>{keys.error}</ErrorText>}
          {keys.value?.keys.length === 0 && <Muted>No API keys have been created for this workspace.</Muted>}
          {keys.value?.keys.map((key) => <div className={styles.row} key={key.key_prefix}>
            <div><strong>{key.label || 'Unlabeled key'}</strong><br /><code>{key.key_prefix}</code> · {key.revoked_at ? 'revoked' : 'active'}</div>
            {!key.revoked_at && <Button variant="ghost" disabled={!!busy} onClick={() => {
              if (!window.confirm(`Revoke ${key.label || key.key_prefix}? Applications using it will lose access.`)) return;
              void run('revoke-key', async () => { await apiPost('/v1/billing/keys/revoke', { key_prefix: key.key_prefix }); setSecret(''); refresh((n) => n + 1); });
            }}>Revoke</Button>}
          </div>)}
        </Panel>}

        <Panel title="Signed-in sessions">
          {sessions.loading && <Muted>Loading sessions…</Muted>}
          {sessions.error && <ErrorText>{sessions.error}</ErrorText>}
          {sessions.value?.sessions.map((session) => <div className={styles.row} key={session.sessionId}>
            <div><strong>{session.current ? 'This session' : 'Other signed-in session'}</strong><br />
              <Muted>Created {date(session.createdAt)} · expires {date(session.expiresAt)}</Muted></div>
            <Button variant="ghost" disabled={!!busy} onClick={() => void run('session', async () => {
              await apiDelete(`/v1/auth/sessions/${encodeURIComponent(session.sessionId)}`);
              if (session.current) window.location.assign('/settings'); else refresh((n) => n + 1);
            })}>{session.current ? 'Sign out' : 'Revoke'}</Button>
          </div>)}
        </Panel>

        <Panel title="Privacy and deployment configuration">
          {!account.value.configuration.privateDataIsolation && <p className={styles.notice}>
            This legacy host still uses shared substrate readers. Workspace membership protects account access, but does not yet establish private isolation across Explore, geometry, and all other data paths. Do not upload confidential company data to this shared host.
          </p>}
          <dl className={styles.configuration}>
            <dt>Authentication</dt><dd>{account.value.configuration.authMode}</dd>
            <dt>Billing persistence</dt><dd>{account.value.configuration.billingStore}</dd>
            <dt>Billing enforcement</dt><dd>{account.value.configuration.billingEnforced ? 'Enabled' : 'Development bypass'}</dd>
            <dt>Persistent session keys</dt><dd>{account.value.configuration.persistentSessionKeys ? 'Configured' : 'Not configured'}</dd>
            <dt>Public address</dt><dd>{account.value.configuration.publicBaseUrl || 'Not configured'}</dd>
          </dl>
          {account.value.configuration.providers.map((provider) => <p key={provider.id}>
            {provider.name} sign-in callback: <code>{provider.callbackPath}</code>
          </p>)}
          <Muted>OAuth client secrets and Stripe credentials are configured on the server, never in this browser form.</Muted>
        </Panel>
      </>}
    </div>
  );
}

export function BillingReturnView() {
  const { authReady, authUser, authProviders } = useAppStore();
  const location = useLocation();
  const sessionId = new URLSearchParams(location.search).get('session_id');
  const canceled = location.pathname.endsWith('/cancel');
  const [revision, refresh] = useState(0);
  const [state, setState] = useState<{ status: string; active: boolean } | null>(null);
  const [error, setError] = useState('');
  useEffect(() => {
    if (!authUser || !sessionId || canceled) return;
    const controller = new AbortController(); setError(''); setState(null);
    void apiPost<{ status: string; active: boolean }>('/v1/billing/checkout/status', { sessionId }, { signal: controller.signal })
      .then((result) => { if (!controller.signal.aborted) setState(result); })
      .catch((failure: unknown) => { if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : 'Unable to read checkout status.'); });
    return () => controller.abort();
  }, [authUser, sessionId, canceled, revision]);
  return <div className={styles.root}>
    <h1>{canceled ? 'Checkout canceled' : 'Subscription checkout'}</h1>
    {canceled ? <p>Checkout was canceled. Your existing subscription has not been changed by this return page.</p> : <>
      {!authReady ? <Muted>Loading account…</Muted> : !authUser ?
        <AccountControls user={null} providers={authProviders} returnUrl={`${location.pathname}${location.search}`} /> : <>
          {!sessionId && <ErrorText>No checkout session was supplied.</ErrorText>}
          {error && <ErrorText>{error}</ErrorText>}
          {sessionId && !state && !error && <Muted>Checking your workspace subscription…</Muted>}
          {state && <p role="status">{state.active ? 'Your workspace subscription is active.' :
            state.status === 'awaiting_subscription_confirmation' ? 'Payment is recorded; subscription confirmation is still arriving from Stripe.' : 'Payment has not completed.'}</p>}
          {sessionId && !state?.active && <Button onClick={() => refresh((n) => n + 1)}>Refresh payment status</Button>}
        </>}
    </>}
    <p><Link to="/settings">Account and company settings</Link> · <Link to="/billing">Plans and usage</Link></p>
  </div>;
}
