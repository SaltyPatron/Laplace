import { ConsensusBadge, Muted, Panel, Table, Td } from '@ui';
import { EntityLink } from '../../components/EntityLink';
import { RelationChip } from '../../components/RelationChip';
import type { ExploreEntityResponse } from '../../types';
import styles from '../EntityDetail.module.css';

export function OverviewTab({ entity }: { entity: ExploreEntityResponse }) {
  return (
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
  );
}
