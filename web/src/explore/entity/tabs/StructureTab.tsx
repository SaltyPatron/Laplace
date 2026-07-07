import { Button, Muted, Panel, Stack, Table, Td, Th } from '@ui';
import { EntityLink } from '../../components/EntityLink';
import { GatePrompt } from '../../components/GatePrompt';
import type { ExploreEntityPreviewResponse, ExploreEntityResponse } from '../../types';
import styles from '../EntityDetail.module.css';

export function StructureTab({
  entity,
  preview,
  decomposeNodes,
  decomposeBusy,
  containersUnlocked,
  containers,
  onDecompose,
  onLoadContainers,
}: {
  entity: ExploreEntityResponse;
  preview: ExploreEntityPreviewResponse;
  decomposeNodes: Awaited<ReturnType<typeof import('../../api').exploreDecompose>>['nodes'] | null;
  decomposeBusy: boolean;
  containersUnlocked: boolean;
  containers: Awaited<ReturnType<typeof import('../../api').exploreContainers>>['containers'] | null;
  onDecompose: () => void;
  onLoadContainers: () => void;
}) {
  return (
    <Stack gap={4}>
      <Panel title="Constituents">
        {entity.constituents.length === 0 ? (
          <Muted>None</Muted>
        ) : (
          <ul className={styles.list}>
            {entity.constituents.map((c) => (
              <li key={c.ordinal}>
                <EntityLink idHex={c.child_id_hex} label={c.child_label} />
              </li>
            ))}
          </ul>
        )}
      </Panel>

      <Panel title="Decompose (free)">
        <Stack gap={3}>
          <Button disabled={decomposeBusy} loading={decomposeBusy} onClick={onDecompose}>
            {decomposeBusy ? 'Decomposing…' : `Decompose "${preview.label}"`}
          </Button>
          {decomposeNodes ? (
            <Table>
              <thead>
                <tr>
                  <Th>Ord</Th>
                  <Th>Tier</Th>
                  <Th>Label</Th>
                  <Th>Id</Th>
                </tr>
              </thead>
              <tbody>
                {decomposeNodes.map((n) => (
                  <tr key={n.ordinal}>
                    <Td>{n.ordinal}</Td>
                    <Td>{n.tier}</Td>
                    <Td>{n.label}</Td>
                    <Td>
                      <EntityLink idHex={n.id_hex} label={n.id_hex.slice(0, 8)} />
                    </Td>
                  </tr>
                ))}
              </tbody>
            </Table>
          ) : null}
        </Stack>
      </Panel>

      <Panel title="Containers">
        {containersUnlocked && containers ? (
          containers.containers.length === 0 ? (
            <Muted>None</Muted>
          ) : (
            <ul className={styles.list}>
              {containers.containers.map((c) => (
                <li key={c.entity_id_hex}>
                  <EntityLink idHex={c.entity_id_hex} label={c.entity_label} /> tier {c.tier} · {c.type} ·{' '}
                  {c.hops} hop{c.hops === 1 ? '' : 's'}
                </li>
              ))}
            </ul>
          )
        ) : (
          <GatePrompt
            serviceId="visualization.deep_export"
            label="Expand container documents (multi-hop fan-out)."
            onReady={onLoadContainers}
          />
        )}
      </Panel>
    </Stack>
  );
}
