import { Button, Muted } from '@ui';
import { apiPost } from '../api/client';
import type { AuthProvider, AuthUser } from '../store';
import styles from './AccountControls.module.css';

export interface AccountControlsProps {
  user: AuthUser | null;
  providers: AuthProvider[];
  returnUrl: string;
}

export function AccountControls({ user, providers, returnUrl }: AccountControlsProps) {
  if (user) {
    const label = user.displayName || user.email || 'Signed in';
    return (
      <div className={styles.account}>
        <div className={styles.identity}>
          <span>{label}</span>
          <Muted>{user.provider} · {user.tenantId}</Muted>
        </div>
        <Button
          variant="ghost"
          size="sm"
          onClick={async () => {
            await apiPost('/v1/auth/logout', {});
            window.location.assign('/');
          }}
        >
          Sign out
        </Button>
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
