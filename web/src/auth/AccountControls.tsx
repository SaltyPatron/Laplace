import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { Button, ErrorText, Muted, Select, useReadResource } from '@ui';
import { apiGet, apiPost } from '../api/client';
import { roleName } from './access';
import { useAppStore, type AuthProvider, type AuthUser } from '../store';
import styles from './AccountControls.module.css';

export interface AccountControlsProps {
  user: AuthUser | null;
  providers: AuthProvider[];
  returnUrl: string;
}

const invitationStorageKey = 'laplace.pending-workspace-invitation';

export function AccountControls({ user, providers, returnUrl }: AccountControlsProps) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const tenant = useAppStore((state) => state.tenant);
  const workspaces = useReadResource({ key: JSON.stringify(['account', user?.id, tenant, '/v1/account']), enabled: !!user,
    read: (signal) => apiGet<{ tenantId: string; workspaces: { tenantId: string; displayName: string; role: string }[] }>('/v1/account', { signal }) });

  useEffect(() => {
    if (!user) return;
    try {
      const invitation = sessionStorage.getItem(invitationStorageKey);
      if (!invitation) return;
      sessionStorage.removeItem(invitationStorageKey);
      if (!/^[0-9a-f]{64}$/.test(invitation)) return;
      // Restore only in a fragment, after OAuth has finished. It never becomes
      // part of the request URL, the identity provider's state, or a referrer.
      window.history.replaceState(null, '', `/settings#join=${invitation}`);
      window.location.reload();
    } catch {
      setError('Unable to restore the invitation. Open the original private invitation link after signing in.');
    }
  }, [user]);

  if (user) {
    const label = user.displayName || user.email || 'Signed in';
    return (
      <div className={styles.account}>
        <div className={styles.identity}>
          <Link to="/settings">{label}</Link>
          <Muted>{user.email || user.provider}</Muted>
        </div>
        {workspaces.data && <label className={styles.workspace}>Workspace<Select aria-label="Current workspace" value={workspaces.data.tenantId} disabled={busy} onChange={async (event) => {
          setBusy(true); setError('');
          try {
            await apiPost(`/v1/account/workspaces/${encodeURIComponent(event.target.value)}/select`, {});
            window.location.assign(returnUrl.split('#', 1)[0]);
          } catch (failure) { setError(failure instanceof Error ? failure.message : 'Workspace switch failed.'); setBusy(false); }
        }}>{!workspaces.data.workspaces.some((workspace) => workspace.tenantId === workspaces.data?.tenantId) && <option value={workspaces.data.tenantId} disabled>Choose a workspace…</option>}
          {workspaces.data.workspaces.map((workspace) => <option key={workspace.tenantId} value={workspace.tenantId}>{workspace.displayName} · {roleName(workspace.role)}</option>)}</Select></label>}
        {workspaces.busy && !workspaces.data && <Muted>Loading workspaces…</Muted>}
        {workspaces.error && <div role="alert"><ErrorText>Workspaces could not be loaded: {workspaces.error.message}</ErrorText><Button variant="ghost" size="sm" disabled={workspaces.busy} onClick={() => void workspaces.refresh()}>Retry workspaces</Button></div>}
        <Button variant="ghost" size="sm" disabled={busy} onClick={async () => {
          setBusy(true); setError('');
          try {
            await apiPost('/v1/auth/logout', {});
            window.location.assign('/');
          } catch (failure) {
            setError(failure instanceof Error ? failure.message : 'Sign out failed.');
            setBusy(false);
          }
        }}>Sign out</Button>
        {error && <ErrorText>{error}</ErrorText>}
      </div>
    );
  }

  if (providers.length === 0) return null;
  const [destination, fragment = ''] = returnUrl.split('#', 2);
  const invitation = new URLSearchParams(fragment).get('join');
  return (
    <div className={styles.account} aria-label="Sign in">
      {providers.map((provider) => (
        <Button key={provider.id} variant="ghost" size="sm" asChild>
          <a href={`${provider.loginUrl}?returnUrl=${encodeURIComponent(destination)}`}
            referrerPolicy="no-referrer"
            onClick={(event) => {
              if (!invitation || !/^[0-9a-f]{64}$/.test(invitation)) return;
              try { sessionStorage.setItem(invitationStorageKey, invitation); }
              catch {
                event.preventDefault();
                setError('Enable session storage to keep this invitation through sign-in, or sign in separately and reopen the invitation link.');
              }
            }}>
            Sign in with {provider.displayName}
          </a>
        </Button>
      ))}
      {error && <ErrorText>{error}</ErrorText>}
    </div>
  );
}
