import { apiGet } from '../api/client';
import { useReadResource } from '@ui';
import { useAppStore } from '../store';

export interface PlatformRole { id: string; name: string; description: string; permissions: string[] }
export interface Access {
  authenticated: boolean; userId: string | null;
  workspace: { tenantId: string; role: string | null };
  platform: { role: string | null; permissions: string[] };
  roles: PlatformRole[];
}
export function useAccess() {
  const { tenant, authUser, authReady, devPrincipal } = useAppStore();
  return useReadResource({ key: JSON.stringify(['access', tenant, authUser?.id, devPrincipal]), enabled: authReady,
    read: (signal) => apiGet<Access>('/v1/access', { signal }) });
}
export const roleName = (role: string | null | undefined) => ({ owner: 'Owner', admin: 'Administrator', member: 'Member',
  administrator: 'Platform administrator', operator: 'Operations manager', observer: 'Observer',
}[role ?? ''] ?? 'No platform role');
