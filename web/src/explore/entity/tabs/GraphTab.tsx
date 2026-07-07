import { Panel } from '@ui';
import { ConsensusGraph } from '../../graph/ConsensusGraph';
import type { ExploreConsensusRow } from '../../types';

export function GraphTab({
  centerId,
  centerLabel,
  edges,
  walkPath,
  onNodeClick,
}: {
  centerId: string;
  centerLabel: string;
  edges: ExploreConsensusRow[];
  walkPath: { idHex: string; label: string }[];
  onNodeClick: (id: string) => void;
}) {
  return (
    <Panel title="Consensus neighborhood">
      <ConsensusGraph
        centerId={centerId}
        centerLabel={centerLabel}
        edges={edges}
        walkPath={walkPath}
        onNodeClick={onNodeClick}
      />
    </Panel>
  );
}
