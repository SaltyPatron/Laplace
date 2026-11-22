import { useState } from 'react';
import { Checkbox, Muted, Panel, ReadStatus, Table, Td, Th, useReadResource } from '@ui';
import { queryHomeLeaders, queryLeaders } from '../../query/api';
import type { RelationBand } from '../../query/types';
import { exploreCatalog } from '../api';
import { EntityLink } from '../components/EntityLink';
import { StatCard } from '../components/StatCard';
import styles from './Browse.module.css';

type LeaderRowExtended = {
  subject_id: string;
  subject: string;
  relation: string;
  object_id: string;
  object: string;
  eff_mu: number;
  witnesses: number;
  salience?: number | null;
};

/**
 * 4-table substrate inventory (entity, physicality, attestation, consensus) alongside
 * indexed Glicko-2 consensus, witness, and salience leaderboards by relation band.
 */
export function LeaderboardsPanel({
  tenant,
  quoteId,
  bands,
}: {
  tenant: string;
  quoteId?: string;
  bands: RelationBand[];
}) {
  const [selectedBands, setSelectedBands] = useState<number[]>([]);

  const catalog = useReadResource({
    key: JSON.stringify(['browse-catalog-tables', tenant, quoteId]),
    read: (signal) => exploreCatalog({ tenant, quoteId, signal }),
  });

  const leaders = useReadResource({
    key: JSON.stringify(['browse-leaders-panel', tenant, quoteId, selectedBands]),
    read: (signal) => selectedBands.length > 0
      ? queryLeaders(selectedBands, 10, { tenant, quoteId, signal })
      : queryHomeLeaders({ tenant, quoteId, signal }),
  });

  const counts = catalog.data?.counts ?? [];
  const populatedBands = bands.filter((b) => b.consensus_rows > 0);
  const consensus = catalog.data?.consensus;

  const leaderRows = (leaders.data?.bands ?? []).flatMap((band) =>
    (band.rows as LeaderRowExtended[]).map((row, idx) => ({
      key: `${band.band}:${row.subject_id}:${row.relation}:${row.object_id}:${idx}`,
      bandName: band.name,
      ...row,
    })),
  );

  const claimRows = (leaders.data?.bands ?? []).flatMap((band) =>
    (band.claims ?? []).map((claim) => ({
      bandName: band.name,
      ...claim,
    })),
  );

  return (
    <section aria-label="4-table consensus and witness leaderboards" className={styles.leaderboardsSection}>
      <Panel title="4-table substrate & consensus leaderboards">
        <ReadStatus label="4-table substrate inventory" resource={catalog} />
        {catalog.data && (
          <div className={styles.leaderStats}>
            {counts.map((c) => (
              <StatCard
                key={c.metric}
                label={c.metric}
                value={c.value != null ? Number(c.value).toLocaleString() : '—'}
              />
            ))}
            {consensus && (
              <>
                <StatCard
                  label="consensus rows"
                  value={Number(consensus.consensusRows ?? 0).toLocaleString()}
                />
                {consensus.avgWitnesses != null && (
                  <StatCard
                    label="avg witnesses / claim"
                    value={Number(consensus.avgWitnesses).toFixed(2)}
                  />
                )}
                {consensus.maxWitnesses != null && (
                  <StatCard
                    label="max witnesses"
                    value={Number(consensus.maxWitnesses).toLocaleString()}
                  />
                )}
                {catalog.data.multi_source_entity_count != null && (
                  <StatCard
                    label="multi-source entities"
                    value={Number(catalog.data.multi_source_entity_count).toLocaleString()}
                  />
                )}
              </>
            )}
          </div>
        )}

        {populatedBands.length > 0 && (
          <fieldset className={styles.bandFilterBox}>
            <legend>Filter leaderboards by relation band</legend>
            <div className={styles.bandFilterGrid}>
              {populatedBands.map((band) => (
                <Checkbox
                  key={band.band}
                  checked={selectedBands.includes(band.band)}
                  label={`${band.name.replaceAll('_', ' ')} (${band.consensus_rows.toLocaleString()})`}
                  onChange={(event) => {
                    setSelectedBands((prev) =>
                      event.target.checked
                        ? [...prev, band.band].sort((a, b) => a - b)
                        : prev.filter((v) => v !== band.band),
                    );
                  }}
                />
              ))}
            </div>
          </fieldset>
        )}

        <ReadStatus label="Consensus leaderboards" resource={leaders} />

        {leaderRows.length === 0 && claimRows.length === 0 ? (
          <Muted>No consensus leaderboard entries returned for the active filter.</Muted>
        ) : (
          <div className={styles.leaderTables}>
            {leaderRows.length > 0 && (
              <Table>
                <thead>
                  <tr>
                    <Th>Band</Th>
                    <Th>Subject</Th>
                    <Th>Relation</Th>
                    <Th>Object</Th>
                    <Th>Conservative μ</Th>
                    <Th>Witnesses</Th>
                    <Th>Salience I(e)</Th>
                  </tr>
                </thead>
                <tbody>
                  {leaderRows.slice(0, 25).map((row) => (
                    <tr key={row.key}>
                      <Td>{row.bandName.replaceAll('_', ' ')}</Td>
                      <Td><EntityLink idHex={row.subject_id} label={row.subject} /></Td>
                      <Td><code>{row.relation}</code></Td>
                      <Td><EntityLink idHex={row.object_id} label={row.object} /></Td>
                      <Td>{Number(row.eff_mu).toFixed(1)}</Td>
                      <Td>{Number(row.witnesses).toLocaleString()}</Td>
                      <Td>{row.salience != null ? Number(row.salience).toFixed(2) : '—'}</Td>
                    </tr>
                  ))}
                </tbody>
              </Table>
            )}

            {claimRows.length > 0 && (
              <Table>
                <thead>
                  <tr>
                    <Th>Band</Th>
                    <Th>Claim sequence</Th>
                    <Th>Confidence</Th>
                    <Th>Witnesses</Th>
                  </tr>
                </thead>
                <tbody>
                  {claimRows.slice(0, 15).map((claim) => (
                    <tr key={`${claim.bandName}:${claim.claim_id}`}>
                      <Td>{claim.bandName.replaceAll('_', ' ')}</Td>
                      <Td>
                        {claim.parts.map((part, idx) => (
                          <span key={`${claim.claim_id}:${idx}`}>
                            {idx > 0 ? ' · ' : ''}
                            <EntityLink idHex={part.id} label={part.label || part.id} />
                            {part.run > 1 ? ` ×${part.run}` : ''}
                          </span>
                        ))}
                      </Td>
                      <Td>{Number(claim.confidence).toFixed(3)}</Td>
                      <Td>{Number(claim.witnesses).toLocaleString()}</Td>
                    </tr>
                  ))}
                </tbody>
              </Table>
            )}
          </div>
        )}
      </Panel>
    </section>
  );
}
