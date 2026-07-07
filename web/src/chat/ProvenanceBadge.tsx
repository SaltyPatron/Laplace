import type { ProvenanceEntry } from '../store';
import { ConsensusBadge } from '@ui';

export function ProvenanceBadge({ entry }: { entry: ProvenanceEntry }) {
  if (entry.ordUsed !== undefined) {
    return <ConsensusBadge ordUsed={entry.ordUsed} tone="chat" />;
  }
  if (entry.effMu === undefined && entry.witnesses === undefined) return null;
  return <ConsensusBadge mu={entry.effMu} witnesses={entry.witnesses} tone="chat" />;
}
