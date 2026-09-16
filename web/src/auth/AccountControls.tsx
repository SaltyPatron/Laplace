import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { Button, ErrorText, Muted } from '@ui';
import { apiPost } from '../api/client';
import type { AuthProvider, AuthUser } from '../store';
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
          <Muted>{user.provider} · {user.tenantId}</Muted>
        </div>
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
