import { useEffect, useRef, useState } from 'react';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import { Button, ErrorText, Input, Modal, Muted, Panel, Select, useReadResource } from '@ui';
import { apiDelete, apiGet, apiPost, apiPut } from '../api/client';
import { useAppStore } from '../store';
import { AccountControls } from './AccountControls';
import { useSectionParam } from '../layout/useSectionParam';
import { roleName } from './access';
import { WorkspaceInvitation } from './WorkspaceInvitation';
import { CopyField } from './CopyField';
import styles from './SettingsView.module.css';

interface Workspace { tenantId: string; displayName: string; kind: string; role: string }
interface Subscription { planId: string; status: string; periodStart: string; periodEnd: string }
interface Account {
  userId: string; tenantId: string; displayName: string | null; email: string | null; provider: string;
  workspaces: Workspace[]; subscriptions: Subscription[];
  workspaceRoles?: { id: string; name: string; permissions: string[] }[];
  configuration: {
    authMode: string; billingEnforced: boolean; billingStore: string; stripeConfigured: boolean; stripeMode: string;
    commercialCatalogApproved: boolean;
    publicBaseUrl: string | null; persistentSessionKeys: boolean; privateDataIsolation: boolean;
    providers: { id: string; name: string; callbackPath: string }[];
  };
}
interface Member { userId: string; displayName: string | null; email: string | null; role: string }
interface Invitation { invitationId: string; role: string; expiresAt: string }
interface WebSession { sessionId: string; createdAt: string; lastSeenAt: string; expiresAt: string; current: boolean }
interface ApiKey { key_prefix: string; label: string | null; created_at: string; revoked_at: string | null }

function useRemote<T>(path: string | null, revision: number) {
  const { tenant, authUser } = useAppStore();
  const read = useReadResource<T>({ key: JSON.stringify(['account', authUser?.id, tenant, path]), enabled: !!path,
    read: (signal) => apiGet<T>(path!, { signal }) });
  const previous = useRef(revision);
  useEffect(() => {
    if (previous.current === revision) return;
    previous.current = revision;
    if (path) void read.reload();
  }, [path, revision, read.reload]);
  return { value: path ? read.data : null, error: path ? read.error?.message ?? '' : '', loading: !!path && read.busy, refresh: read.refresh };
}

const date = (value: string) => new Date(value).toLocaleString();
const SECTIONS = ['workspace', 'members', 'clients', 'billing', 'security', 'permissions'] as const;
const SECTION_NAMES = ['Workspace', 'Members & invitations', 'API clients', 'Plans & billing', 'Sign-in sessions', 'Roles & permissions'];

export function SettingsView() {
  const { authUser, tenant } = useAppStore();
  return <WorkspaceSettings key={JSON.stringify([authUser?.id, tenant])} />;
}

type PendingAction = { kind: 'member'; value: Member } | { kind: 'key'; value: ApiKey }
  | { kind: 'invitation'; value: Invitation } | { kind: 'session'; value: WebSession };

function WorkspaceSettings() {
  const [section, setSection] = useSectionParam('section', [...SECTIONS], 'workspace');
  const { authReady, authUser, authProviders, tenant } = useAppStore();
  const location = useLocation();
  const navigate = useNavigate();
  const [revision, refresh] = useState(0);
  const [busy, setBusy] = useState('');
  const busyRef = useRef(false);
  const [error, setError] = useState('');
  const [message, setMessage] = useState('');
  const [workspaceName, setWorkspaceName] = useState('');
  const [createdWorkspace, setCreatedWorkspace] = useState<Workspace | null>(null);
  const [rename, setRename] = useState('');
  const [keyLabel, setKeyLabel] = useState('');
  const [secret, setSecret] = useState<{ key: string; keyPrefix: string } | null>(null);
  const [inviteLink, setInviteLink] = useState('');
  const [inviteRole, setInviteRole] = useState('member');
  const [roleMember, setRoleMember] = useState<Member | null>(null);
  const [memberRole, setMemberRole] = useState('member');
  const [pendingAction, setPendingAction] = useState<PendingAction | null>(null);
  const [memberSearch, setMemberSearch] = useState('');
  const [clientSearch, setClientSearch] = useState('');
  const [showRevoked, setShowRevoked] = useState(false);
  const joinToken = new URLSearchParams(location.hash.slice(1)).get('join') ?? '';
  const account = useRemote<Account>(authReady && authUser ? '/v1/account' : null, revision);
  const active = account.value?.workspaces.find((w) => w.tenantId === account.value?.tenantId);
  const manages = !account.error && (active?.role === 'owner' || active?.role === 'admin');
  const members = useRemote<{ members: Member[] }>(manages && section === 'members' ? '/v1/account/members' : null, revision);
  const invitations = useRemote<{ invitations: Invitation[] }>(manages && section === 'members' ? '/v1/account/invitations' : null, revision);
  const roleEvents = useRemote<{ events: { eventId: number; person: string; actor: string; previousRole: string; role: string; occurredAt: string }[] }>(manages && section === 'members' ? '/v1/account/role-events' : null, revision);
  const keys = useRemote<{ keys: ApiKey[] }>(manages && section === 'clients' ? '/v1/billing/keys' : null, revision);
  const sessions = useRemote<{ sessions: WebSession[] }>(authReady && authUser && section === 'security' ? '/v1/auth/sessions' : null, revision);
  const visibleMembers = members.value?.members.filter((member) => `${member.email ?? ''} ${member.displayName ?? ''}`.toLocaleLowerCase().includes(memberSearch.trim().toLocaleLowerCase()));
  const visibleKeys = keys.value?.keys.filter((key) => (showRevoked || !key.revoked_at)
    && `${key.label ?? ''} ${key.key_prefix}`.toLocaleLowerCase().includes(clientSearch.trim().toLocaleLowerCase()));

  useEffect(() => { setSecret(null); setInviteLink(''); }, [tenant, authUser?.id]);
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
  function reviewAction(action: PendingAction) { setError(''); setMessage(''); setPendingAction(action); }
  async function confirmAction() {
    if (!pendingAction) return;
    const action = pendingAction;
    await run(action.kind, async () => {
      if (action.kind === 'member') {
        await apiDelete(`/v1/account/members/${action.value.userId}`);
        setMessage(`${action.value.email || action.value.displayName || 'Member'} removed from this workspace.`);
      } else if (action.kind === 'key') {
        await apiPost('/v1/billing/keys/revoke', { key_prefix: action.value.key_prefix });
        if (secret?.keyPrefix === action.value.key_prefix) setSecret(null);
        setMessage('API key revoked. Applications using it no longer have access.');
      } else if (action.kind === 'invitation') {
        await apiDelete(`/v1/account/invitations/${action.value.invitationId}`);
        setInviteLink(''); setMessage('Invitation revoked. Its link can no longer be accepted.');
      } else {
        await apiDelete(`/v1/auth/sessions/${encodeURIComponent(action.value.sessionId)}`);
        if (action.value.current) { window.location.assign('/settings'); return; }
        setMessage('Session revoked. That session must sign in again.');
      }
      setPendingAction(null); refresh((value) => value + 1);
    });
  }
  const returnUrl = `${location.pathname}${location.search}${location.hash}`;

  if (!authReady) return <div className={styles.root}><Muted>Loading account…</Muted></div>;
  if (!authUser) return (
    <div className={styles.root}>
      <h1>Workspace settings</h1>
      <p>Sign in to select a workspace, subscribe, manage members, and control access.</p>
      <AccountControls user={null} providers={authProviders} returnUrl={returnUrl} />
      {authProviders.length === 0 && <ErrorText>This host has no configured sign-in provider.</ErrorText>}
    </div>
  );

  return (
    <div className={styles.root}>
      <header className={styles.header}>
        <div><h1>{active?.displayName || 'Workspace settings'}</h1><Muted>{authUser.email || authUser.displayName} · {active ? roleName(active.role) : account.value ? 'Choose a workspace' : 'Loading access…'}</Muted></div>
        <Button variant="ghost" disabled={!!busy} onClick={() => refresh((n) => n + 1)}>Refresh</Button>
      </header>
      <label className={styles.mobileSections}>Settings page<Select value={section} onChange={(event) => setSection(event.target.value as typeof SECTIONS[number])}>
        {SECTIONS.map((item, index) => <option key={item} value={item}>{SECTION_NAMES[index]}</option>)}</Select></label>
      <nav className={styles.sections} aria-label="Workspace settings sections">{SECTIONS.map((item, index) =>
        <button key={item} aria-current={section === item ? 'page' : undefined} onClick={() => setSection(item)}>{SECTION_NAMES[index]}</button>)}</nav>
      {error && <ErrorText>{error}</ErrorText>}
      {message && <p role="status">{message}</p>}
      {account.loading && <Muted>{account.value ? 'Refreshing workspace…' : 'Loading workspace and subscription…'}</Muted>}
      {account.error && <ErrorText>{account.error}</ErrorText>}
      {account.value && !active && <p role="status">You no longer belong to the selected workspace. Choose another workspace from the account menu, or create one in Workspace settings.</p>}

      {joinToken && <WorkspaceInvitation key={joinToken} token={joinToken} selectWorkspace={selectWorkspace}
        onAccepted={() => refresh((n) => n + 1)}
        onDismiss={() => navigate({ pathname: location.pathname, search: location.search, hash: '' }, { replace: true })} />}

      {account.value && <>
        {section === 'workspace' && <Panel title="Workspace settings">
          <label className={styles.field}>Current workspace
            <select value={account.value.tenantId} disabled={!!busy} onChange={(event) => {
              const value = event.target.value;
              void run('workspace', () => selectWorkspace(value));
            }}>
              {!active && <option value={account.value.tenantId} disabled>Choose a workspace…</option>}
              {account.value.workspaces.map((workspace) => <option key={workspace.tenantId} value={workspace.tenantId}>
                {workspace.displayName} · {workspace.role}
              </option>)}
            </select>
          </label>
          <details><summary>Technical identifiers</summary><Muted>Workspace ID: {account.value.tenantId}. Account ID: {account.value.userId}.</Muted></details>
          {manages && <form className={styles.form} onSubmit={(event) => { event.preventDefault(); void run('rename', async () => {
            await apiPut('/v1/account/workspace', { name: rename }); refresh((n) => n + 1); setMessage('Workspace name saved.');
          }); }}>
            <label className={styles.field}>Workspace name<Input value={rename} onChange={(e) => setRename(e.target.value)} required maxLength={160} /></label>
            <Button type="submit" loading={busy === 'rename'} disabled={!!busy || !rename.trim() || rename.trim() === active?.displayName}>Save name</Button>
          </form>}
          <form className={styles.form} onSubmit={(event) => { event.preventDefault(); void run('create', async () => {
            const created = createdWorkspace ?? await apiPost<Workspace>('/v1/account/workspaces', { name: workspaceName });
            setCreatedWorkspace(created);
            refresh((n) => n + 1);
            try { await selectWorkspace(created.tenantId); }
            catch (failure) {
              const detail = failure instanceof Error ? failure.message : 'The request failed.';
              throw new Error(`Workspace "${created.displayName}" was created, but switching to it failed. ${detail}`);
            }
          }); }}>
            <label className={styles.field}>New company workspace<Input value={workspaceName} disabled={!!busy || !!createdWorkspace} onChange={(e) => setWorkspaceName(e.target.value)} required maxLength={160} placeholder="Company name" /></label>
            <Button type="submit" loading={busy === 'create'} disabled={!!busy || (!createdWorkspace && !workspaceName.trim())}>{createdWorkspace ? 'Open created workspace' : 'Create workspace'}</Button>
          </form>
          {createdWorkspace && <div role="status"><p><strong>{createdWorkspace.displayName}</strong> is created. Open it using the button above or the workspace picker.</p>
            <Button variant="ghost" disabled={!!busy} onClick={() => { setCreatedWorkspace(null); setWorkspaceName(''); setError(''); }}>Create another workspace</Button></div>}
        </Panel>}

        {section === 'billing' && <Panel title="Subscription">
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
        </Panel>}

        {section === 'members' && manages && <Panel title="Members & invitations">
          <label className={styles.field}>Find a member<Input type="search" value={memberSearch} onChange={(event) => setMemberSearch(event.target.value)} placeholder="Email address or name" /></label>
          {members.loading && <Muted>Loading members…</Muted>}
          {members.error && <ErrorText>{members.error}</ErrorText>}
          {visibleMembers?.length === 0 && <Muted>{memberSearch ? 'No members match this search.' : 'No members were returned.'}</Muted>}
          {visibleMembers?.map((member) => <div className={styles.row} key={member.userId}>
            <div><strong>{member.email || member.displayName || member.userId}</strong><br /><Muted>{member.displayName} · {roleName(member.role)}</Muted></div>
            {active?.role === 'owner' && <Button variant="ghost" disabled={!!busy} onClick={() => { setError(''); setRoleMember(member); setMemberRole(member.role); }}>Change role</Button>}
            {member.userId !== account.value?.userId && (active?.role === 'owner' || member.role === 'member') &&
              <Button variant="ghost" disabled={!!busy} onClick={() => reviewAction({ kind: 'member', value: member })}>Remove</Button>}
          </div>)}
          <p>Invitation links are single-use and expire in seven days. Anyone signed in who holds the link can join; send it privately.</p>
          <div className={styles.form}>
            <label className={styles.field}>Invitation role<select value={inviteRole} onChange={(e) => setInviteRole(e.target.value)}>
              <option value="member">Member</option>{active?.role === 'owner' && <option value="admin">Administrator</option>}
            </select></label>
            <Button disabled={!!busy} loading={busy === 'invite'} onClick={() => void run('invite', async () => {
              const result = await apiPost<{ invitationPath: string }>('/v1/account/invitations', { role: inviteRole });
              setInviteLink(new URL(result.invitationPath, window.location.origin).href); refresh((n) => n + 1);
            })}>Create invitation</Button>
          </div>
          {inviteLink && <CopyField key={inviteLink} label="Private invitation link" value={inviteLink} />}
          {invitations.loading && <Muted>Loading invitations…</Muted>}
          {invitations.error && <ErrorText>{invitations.error}</ErrorText>}
          {invitations.value?.invitations.map((invitation) => <div className={styles.row} key={invitation.invitationId}>
            <span>{roleName(invitation.role)} invitation · expires {date(invitation.expiresAt)}</span>
            <Button variant="ghost" disabled={!!busy} onClick={() => reviewAction({ kind: 'invitation', value: invitation })}>Revoke invitation</Button>
          </div>)}
          {invitations.value?.invitations.length === 0 && <Muted>No pending invitations.</Muted>}
          <Muted>Workspace API keys are separate service credentials. Rotate any key shared with a departing member.</Muted>
          <h3>Recent role changes</h3>
          {roleEvents.loading && <Muted>Loading role history…</Muted>}{roleEvents.error && <ErrorText>{roleEvents.error}</ErrorText>}
          {roleEvents.value?.events.length === 0 && <Muted>No workspace role changes have been recorded.</Muted>}
          {roleEvents.value?.events.map((event) => <div className={styles.row} key={event.eventId}><div><strong>{event.person}</strong><Muted>{roleName(event.previousRole)} → {roleName(event.role)} · By {event.actor}</Muted></div><time dateTime={event.occurredAt}>{date(event.occurredAt)}</time></div>)}
        </Panel>}

        {section === 'clients' && manages && <Panel title="API clients">
          <p>Give each application its own labeled API key. Keys act for this workspace and are shown only once. They do not grant platform administration.</p>
          <form className={styles.form} onSubmit={(event) => { event.preventDefault(); void run('key', async () => {
            if (secret) return;
            const result = await apiPost<{ key: string; keyPrefix: string }>('/v1/billing/keys', { label: keyLabel });
            setSecret(result); setKeyLabel(''); refresh((n) => n + 1);
          }); }}>
            <label className={styles.field}>Application name<Input required value={keyLabel} onChange={(e) => setKeyLabel(e.target.value)} maxLength={160} placeholder="Analytics pipeline, desktop client…" /></label>
            <Button type="submit" loading={busy === 'key'} disabled={!!busy || !!secret || !keyLabel.trim()}>Create API key</Button>
          </form>
          {secret && <div className={styles.secret}>
            <CopyField key={secret.keyPrefix} label="Save this key now" value={secret.key} secret />
            <Muted>Save this key before creating another. It cannot be retrieved after leaving this page.</Muted>
            <Button variant="ghost" onClick={() => setSecret(null)}>I have saved this key</Button>
          </div>}
          <div className={styles.form}>
            <label className={styles.field}>Find an API client<Input type="search" value={clientSearch} onChange={(event) => setClientSearch(event.target.value)} placeholder="Application name or key prefix" /></label>
            <label><input type="checkbox" checked={showRevoked} onChange={(event) => setShowRevoked(event.target.checked)} /> Include revoked keys</label>
          </div>
          {keys.loading && <Muted>Loading keys…</Muted>}
          {keys.error && <ErrorText>{keys.error}</ErrorText>}
          {keys.value?.keys.length === 0 && <Muted>No API keys have been created for this workspace.</Muted>}
          {keys.value && keys.value.keys.length > 0 && visibleKeys?.length === 0 && <Muted>No API clients match these filters.</Muted>}
          {visibleKeys?.map((key) => <div className={styles.row} key={key.key_prefix}>
            <div><strong>{key.label || 'Unlabeled key'}</strong><br /><code>{key.key_prefix}</code> · {key.revoked_at ? 'revoked' : 'active'}<br /><Muted>Created {date(key.created_at)}{key.revoked_at ? ` · Revoked ${date(key.revoked_at)}` : ''}</Muted></div>
            {!key.revoked_at && <Button variant="ghost" disabled={!!busy} onClick={() => reviewAction({ kind: 'key', value: key })}>Revoke key</Button>}
          </div>)}
          <details><summary>Connect your application</summary><p>Base URL: <code>{window.location.origin}/v1</code></p><p>Authentication: <code>Authorization: Bearer YOUR_API_KEY</code></p><p>Use a separate key for each application so it can be revoked independently. Keep secrets in your application's secret store.</p></details>
        </Panel>}

        {(section === 'clients' || section === 'members') && !manages && !account.error && <Panel title="Workspace administrator access needed"><p>{active ? `Your workspace role is ${roleName(active.role)}.` : 'Choose a workspace you belong to.'} An owner or administrator manages workspace members and application credentials.</p><Link to="/settings?section=permissions">View role permissions</Link></Panel>}

        {section === 'security' && <Panel title="Signed-in sessions">
          {sessions.loading && <Muted>Loading sessions…</Muted>}
          {sessions.error && <ErrorText>{sessions.error}</ErrorText>}
          {sessions.value?.sessions.map((session) => <div className={styles.row} key={session.sessionId}>
            <div><strong>{session.current ? 'This session' : 'Other signed-in session'}</strong><br />
              <Muted>Last active {date(session.lastSeenAt)} · expires {date(session.expiresAt)}</Muted></div>
            <Button variant="ghost" disabled={!!busy} onClick={() => reviewAction({ kind: 'session', value: session })}>{session.current ? 'Sign out' : 'Revoke session'}</Button>
          </div>)}
          {sessions.value?.sessions.length === 0 && <Muted>No active sessions were returned. Refresh to check again.</Muted>}
        </Panel>}

        {section === 'permissions' && <><Panel title="Workspace roles">
          <p>{active ? <>Your role: <strong>{roleName(active.role)}</strong>. These permissions apply to <strong>{active.displayName}</strong>.</> : 'Choose a workspace to see your membership role.'}</p>
          <div className={styles.roleCards}>{account.value.workspaceRoles?.map((role) => <section key={role.id}><h3>{role.name}</h3><ul>{role.permissions.map((permission) => <li key={permission}>{permission}</li>)}</ul></section>)}</div>
          <p>Platform access is assigned separately for shared ingestion and system administration. <Link to="/operator">Open administration</Link>.</p>
        </Panel><Panel title="Data scope and configuration">
          {!account.value.configuration.privateDataIsolation && <p className={styles.notice}>
            Content belongs to one shared substrate. Workspace membership controls account access and billing; it does not provide private content isolation. Only upload content intended for this shared environment.
          </p>}
          <details><summary>Deployment details</summary><dl className={styles.configuration}>
            <dt>Authentication</dt><dd>{account.value.configuration.authMode}</dd>
            <dt>Billing persistence</dt><dd>{account.value.configuration.billingStore}</dd>
            <dt>Billing enforcement</dt><dd>{account.value.configuration.billingEnforced ? 'Enabled' : 'Development bypass'}</dd>
            <dt>Stripe mode</dt><dd>{account.value.configuration.stripeMode}</dd>
            <dt>Commercial catalog</dt><dd>{account.value.configuration.commercialCatalogApproved ? 'Explicitly approved' : 'Live charging disabled'}</dd>
            <dt>Persistent session keys</dt><dd>{account.value.configuration.persistentSessionKeys ? 'Configured' : 'Not configured'}</dd>
            <dt>Public address</dt><dd>{account.value.configuration.publicBaseUrl || 'Not configured'}</dd>
          </dl>
          {account.value.configuration.providers.map((provider) => <p key={provider.id}>
            {provider.name} sign-in callback: <code>{provider.callbackPath}</code>
          </p>)}
          <Muted>OAuth client secrets and Stripe credentials are configured on the server, never in this browser form.</Muted></details>
        </Panel></>}
      </>}
      <Modal open={roleMember !== null} onClose={() => { if (!busy) setRoleMember(null); }} title="Change workspace role" actions={<>
        <Button variant="ghost" disabled={!!busy} onClick={() => setRoleMember(null)}>Cancel</Button>
        <Button loading={busy === 'member-role'} disabled={!!busy || memberRole === roleMember?.role} onClick={() => void run('member-role', async () => {
          if (!roleMember) return;
          await apiPut(`/v1/account/members/${roleMember.userId}/role`, { role: memberRole });
          setRoleMember(null); refresh((n) => n + 1); setMessage('Workspace role updated.');
        })}>Save role</Button></>}>
        <p>{roleMember?.email || roleMember?.displayName}</p><label className={styles.field}>Workspace role<Select disabled={!!busy} value={memberRole} onChange={(event) => setMemberRole(event.target.value)}>
          <option value="member">Member</option><option value="admin">Administrator</option><option value="owner">Owner</option>
        </Select></label><p>Owners can assign roles. At least one owner must remain in the workspace.</p>{error && <ErrorText role="alert">{error}</ErrorText>}
      </Modal>
      <Modal open={pendingAction !== null} onClose={() => { if (!busy) setPendingAction(null); }}
        title={pendingAction?.kind === 'member' ? 'Remove workspace member?' : pendingAction?.kind === 'key' ? 'Revoke API key?' : pendingAction?.kind === 'invitation' ? 'Revoke invitation?' : 'End signed-in session?'}
        actions={<><Button variant="ghost" disabled={!!busy} onClick={() => setPendingAction(null)}>Cancel</Button>
          <Button disabled={!!busy} loading={!!busy} onClick={() => void confirmAction()}>{pendingAction?.kind === 'member' ? 'Remove member' : pendingAction?.kind === 'key' ? 'Revoke key' : pendingAction?.kind === 'invitation' ? 'Revoke invitation' : 'End session'}</Button></>}>
        {pendingAction?.kind === 'member' && <p>Remove <strong>{pendingAction.value.email || pendingAction.value.displayName}</strong> from <strong>{active?.displayName}</strong>. They will lose this workspace's access. A workspace administrator can invite them again.</p>}
        {pendingAction?.kind === 'key' && <p>Revoke <strong>{pendingAction.value.label || pendingAction.value.key_prefix}</strong>. Applications using this key will stop authenticating. A replacement key must be created and installed in each affected application.</p>}
        {pendingAction?.kind === 'invitation' && <p>This {roleName(pendingAction.value.role).toLowerCase()} invitation to <strong>{active?.displayName}</strong> will stop working. Existing memberships remain active.</p>}
        {pendingAction?.kind === 'session' && <p>{pendingAction.value.current ? 'Sign out of this browser session.' : `End the session last active ${date(pendingAction.value.lastSeenAt)}.`} A new sign-in will be required to use that session again.</p>}
        {error && <ErrorText role="alert">{error}</ErrorText>}
      </Modal>
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
