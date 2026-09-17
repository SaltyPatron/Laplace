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
              <RelationTable title="Outgoing" rows={entity.consensus_out} entityType={entity.type} />
            ) : null}
            {entity.consensus_in.length > 0 ? (
              <RelationTable title="Incoming" rows={entity.consensus_in} entityType={entity.type} />
            ) : null}
          </>
        )}
      </Panel>

      <Panel title="Salient facts">
        <ul className={styles.list}>
          {entity.salient_facts.map((f, i) => (
            <li key={i}>
              <RelationChip
                type={displayRelation(f.type, entity.type, 'out')}
                label={f.fact}
                mu={f.eff_mu}
                witnesses={f.witnesses}
              />
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

function isChessPlayer(type?: string | null) {
  const normalized = (type ?? '').replaceAll('_', ' ').trim().toLowerCase();
  return normalized === 'chess player';
}

function displayRelation(type: string, entityType: string | null | undefined, direction: string) {
  // Historical chess pairing cells were written as player --PLAYED_BY--> opponent even though
  // every writer/read interprets the object as the opponent. Until that relation id is migrated,
  // never turn the legacy storage name into the false English sentence "Spassky played by ...".
  if (direction === 'out' && isChessPlayer(entityType)
      && type.replaceAll('_', ' ').trim().toLowerCase() === 'played by') {
    return 'played against';
  }
  return type.replaceAll('_', ' ').toLowerCase();
}

function ambiguousLabels(rows: ExploreConsensusRow[]) {
  const firstId = new Map<string, string>();
  const ambiguous = new Set<string>();
  for (const row of rows) {
    const label = row.entity_label.trim();
    if (!label) continue;
    const existing = firstId.get(label);
    if (existing === undefined) firstId.set(label, row.entity_id_hex);
    else if (existing !== row.entity_id_hex) ambiguous.add(label);
  }
  return ambiguous;
}

function relationEntityLabel(row: ExploreConsensusRow, ambiguous: Set<string>) {
  const label = row.entity_label.trim();
  if (!label) return `Entity · ${row.entity_id_hex.slice(0, 12)}`;
  // A display projection can legitimately collapse distinct governed references to the
  // same descriptive type/source fallback (for example every CILI WordNet mapping becoming
  // "Source Reference · CILIDecomposer"). Never render those different graph objects as
  // indistinguishable rows: identity stays separate from display text, but a bounded hash
  // suffix makes the distinction visible until the source-specific reference label is retained.
  return ambiguous.has(label)
    ? `${label} · ${row.entity_id_hex.slice(0, 12)}`
    : label;
}

function RelationTable({
  title,
  rows,
  entityType,
}: {
  title: string;
  rows: ExploreConsensusRow[];
  entityType?: string | null;
}) {
  const ambiguous = ambiguousLabels(rows);
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
              <Td>{displayRelation(row.type, entityType, row.direction)}</Td>
              <Td>
                <EntityLink
                  idHex={row.entity_id_hex}
                  label={relationEntityLabel(row, ambiguous)}
                />
              </Td>
              <Td><ConsensusBadge mu={row.eff_mu} witnesses={row.witnesses} tone="explore" /></Td>
            </tr>
          ))}
        </tbody>
      </Table>
    </>
  );
}
