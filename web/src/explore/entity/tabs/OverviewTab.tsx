import { ConsensusBadge, Muted, Panel, Table, Td, Th } from '@ui';
import { EntityLink } from '../../components/EntityLink';
import { RelationChip } from '../../components/RelationChip';
import type { ExploreConsensusRow, ExploreEntityResponse } from '../../types';
import styles from '../EntityDetail.module.css';

export function OverviewTab({ entity }: { entity: ExploreEntityResponse }) {
  const hasRelations = entity.consensus_out.length > 0 || entity.consensus_in.length > 0;

  return (
    <>
      <Panel title="Browse relations">
        {!hasRelations ? (
          <Muted>No consensus relations recorded.</Muted>
        ) : (
          <>
            {entity.consensus_out.length > 0 ? (
              <RelationTable title="Outgoing" rows={entity.consensus_out} />
            ) : null}
            {entity.consensus_in.length > 0 ? (
              <RelationTable title="Incoming" rows={entity.consensus_in} />
            ) : null}
          </>
        )}
      </Panel>

      <Panel title="Salient facts">
        <ul className={styles.list}>
          {entity.salient_facts.map((f, i) => (
            <li key={i}>
              <RelationChip type={f.type} label={f.fact} mu={f.eff_mu} witnesses={f.witnesses} />
            </li>
          ))}
        </ul>
        {entity.senses.length > 0 ? (
          <>
            <h3 className={styles.sectionTitle}>Senses</h3>
            <Table>
              <tbody>
                {entity.senses.map((s) => (
                  <tr key={s.sense_id_hex}>
                    <Td>
                      <EntityLink idHex={s.synset_id_hex} label={s.synset_label} />
                    </Td>
                    <Td>
                      <ConsensusBadge mu={s.eff_mu} witnesses={s.witnesses} tone="explore" />
                    </Td>
                  </tr>
                ))}
              </tbody>
            </Table>
          </>
        ) : null}
        {entity.salient_facts.length === 0 && entity.senses.length === 0 ? (
          <Muted>No salient facts recorded.</Muted>
        ) : null}
      </Panel>
    </>
  );
}

function RelationTable({ title, rows }: { title: string; rows: ExploreConsensusRow[] }) {
  return (
    <>
      <h3 className={styles.sectionTitle}>{title}</h3>
      <Table>
        <thead>
          <tr>
            <Th>Relation</Th>
            <Th>Entity</Th>
            <Th>Conservative</Th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr key={`${row.direction}:${row.type}:${row.entity_id_hex}`}>
              <Td>{row.type.replaceAll('_', ' ').toLowerCase()}</Td>
              <Td><EntityLink idHex={row.entity_id_hex} label={row.entity_label} /></Td>
              <Td><ConsensusBadge mu={row.eff_mu} witnesses={row.witnesses} tone="explore" /></Td>
            </tr>
          ))}
        </tbody>
      </Table>
    </>
  );
}
