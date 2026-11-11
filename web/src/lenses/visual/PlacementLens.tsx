import { useMemo, useState } from 'react';
import { describeFailure } from '../../api/client';
import { readEntity } from '../../data/facets';
import { exploreNeighbors } from '../../explore/api';
import { GlomeTab } from '../../explore/entity/tabs/GlomeTab';
import type { NeighborMode } from '../../explore/entity/tabs/types';
import type { GlomeNode } from '../../explore/glome/GlomeCanvas';
import { useAppStore } from '../../store';
import { useReadResource } from '../../ui/hooks/useReadResource';
import { ErrorText, LoadingText } from '../../ui/primitives/Text';

type Neighbors = Awaited<ReturnType<typeof exploreNeighbors>>['neighbors'];

/** Real 4-D placement of the subject and the realized path through its parts. Shape only; never meaning. */
export default function PlacementLens({ id }: { id: string }) {
  const { tenant, quoteId } = useAppStore();
  const read = useReadResource({ key: JSON.stringify(['entity', id, tenant, quoteId]), read: (signal) => readEntity(id, { tenant, quoteId, signal }) });
  const [mode, setMode] = useState<NeighborMode>('structural');
  const [neighbors, setNeighbors] = useState<{ id: string; list: Neighbors } | null>(null);
  const [failure, setFailure] = useState<unknown>(null);
  const loaded = neighbors?.id === id ? neighbors.list : null;

  async function loadNeighbors() {
    setFailure(null);
    try {
      const n = await exploreNeighbors(id, 12, { tenant, quoteId });
      setNeighbors({ id, list: n.neighbors });
    } catch (e) {
      setFailure(e);
    }
  }

  // Structural neighbours are shape neighbours with real S³ coordinates; nothing is placed without one.
  const extra = useMemo((): GlomeNode[] => (loaded?.structural ?? [])
    .filter((n) => n.x != null && n.y != null && n.z != null)
    .map((n) => ({ id: n.neighbor_id_hex ?? `nn-${n.neighbor}`, label: n.neighbor, x: n.x!, y: n.y!, z: n.z!, radius: n.radius ?? 0.8, kind: 'neighbor' as const })), [loaded]);

  if (read.error) return <ErrorText>{describeFailure(read.error).message}</ErrorText>;
  if (!read.data) return <LoadingText>Reading placement…</LoadingText>;
  return <>
    {failure != null && <ErrorText>{describeFailure(failure).message}</ErrorText>}
    <GlomeTab entity={read.data} neighborMode={mode} neighborsUnlocked={loaded != null} glomeExtraNodes={extra}
      walkHighlight={[]} onNeighborModeChange={setMode} onLoadNeighbors={() => void loadNeighbors()} />
  </>;
}
