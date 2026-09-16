import { useState } from 'react';
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

export function AccountControls({ user, providers, returnUrl }: AccountControlsProps) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
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
  return (
    <div className={styles.account} aria-label="Sign in">
      {providers.map((provider) => (
        <Button key={provider.id} variant="ghost" size="sm" asChild>
          <a href={`${provider.loginUrl}?returnUrl=${encodeURIComponent(returnUrl)}`}>
            Sign in with {provider.displayName}
          </a>
        </Button>
      ))}
    </div>
  );
}
