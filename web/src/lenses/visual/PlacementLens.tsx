import { describeFailure } from '../../api/client';
import { readEntity } from '../../data/facets';
import { GlomeTab } from '../../explore/entity/tabs/GlomeTab';
import { useAppStore } from '../../store';
import { useReadResource } from '../../ui/hooks/useReadResource';
import { LoadingText } from '../../ui/primitives/Text';

/** Real 4-D placement of the subject and the realized path through its parts. Shape only; never meaning. */
export default function PlacementLens({ id }: { id: string }) {
  const { tenant, quoteId } = useAppStore();
  const read = useReadResource({ key: JSON.stringify(['entity', id, tenant, quoteId]), read: (signal) => readEntity(id, { tenant, quoteId, signal }) });
  if (read.error) return <p role="alert">{describeFailure(read.error).message}</p>;
  if (!read.data) return <LoadingText>Reading placement…</LoadingText>;
  return <GlomeTab entity={read.data} neighborMode="structural" neighborsUnlocked={false} glomeExtraNodes={[]}
    walkHighlight={[]} onNeighborModeChange={() => {}} onLoadNeighbors={() => {}} />;
}
