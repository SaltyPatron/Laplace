import { Panel, Table, Td, Th } from '@ui';
import type { ExploreEntityResponse } from '../../types';

export function ProvenanceTab({ entity }: { entity: ExploreEntityResponse }) {
  return (
    <Panel title="Evidence provenance">
      <Table>
        <thead>
          <tr>
            <Th>Type</Th>
            <Th>Object</Th>
            <Th>Sources</Th>
            <Th>μ</Th>
            <Th>Witnesses</Th>
          </tr>
        </thead>
        <tbody>
          {entity.evidence.map((e, i) => (
            <tr key={i}>
              <Td>{e.type_label}</Td>
              <Td>{e.object_label}</Td>
              <Td>{e.source_label || '—'}</Td>
              <Td>{e.eff_mu != null ? Number(e.eff_mu).toFixed(1) : '—'}</Td>
              <Td>{e.observation_count}</Td>
            </tr>
          ))}
        </tbody>
      </Table>
    </Panel>
  );
}
