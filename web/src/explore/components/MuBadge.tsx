import { ConsensusBadge } from '@ui';

export function MuBadge({ mu, witnesses }: { mu?: number; witnesses?: number }) {
  return <ConsensusBadge mu={mu} witnesses={witnesses} tone="explore" />;
}
