import { describeFailure } from '../../api/client';
import { readEntity } from '../../data/facets';
import { GraphTab } from '../../explore/entity/tabs/GraphTab';
import { useInspector } from '../../shell/inspector';
import { useAppStore } from '../../store';
import { useReadResource } from '../../ui/hooks/useReadResource';
import { LoadingText } from '../../ui/primitives/Text';

/** The consensus web around the subject. Clicking a node opens it in the inspector without leaving the page. */
export default function GraphLens({ id }: { id: string }) {
  const { tenant, quoteId } = useAppStore();
  const inspector = useInspector();
  const read = useReadResource({ key: JSON.stringify(['entity', id, tenant, quoteId]), read: (signal) => readEntity(id, { tenant, quoteId, signal }) });
  if (read.error) return <p role="alert">{describeFailure(read.error).message}</p>;
  if (!read.data) return <LoadingText>Reading the web…</LoadingText>;
  const e = read.data;
  return <GraphTab centerId={e.id_hex} centerLabel={e.label} edgesOut={e.consensus_out} edgesIn={e.consensus_in}
    walkPath={[]} onNodeClick={(node) => inspector.open(node)} />;
}
