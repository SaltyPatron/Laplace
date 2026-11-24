import { useState } from 'react';
import { Button, Checkbox, Muted, Panel, ReadStatus, Select, Table, Td, Th, useReadResource } from '@ui';
import { queryHomeLeaders, queryLeaders } from '../../query/api';
import type { RelationBand } from '../../query/types';
import { exploreCatalog } from '../api';
import { EntityLink } from '../components/EntityLink';
import { StatCard } from '../components/StatCard';
import { exploreLeaderboard } from './api';
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
  const [sort, setSort] = useState<'standing' | 'rating' | 'matches'>('standing');
  const [boardOffset, setBoardOffset] = useState(0);
  const boardLimit = 25;

  const catalog = useReadResource({
    key: JSON.stringify(['browse-catalog-tables', tenant, quoteId]),
    read: (signal) => exploreCatalog({ tenant, quoteId, signal }),
  });

  const consensusBoard = useReadResource({
    key: JSON.stringify(['browse-consensus-board', tenant, quoteId, sort, boardOffset]),
    read: (signal) => exploreLeaderboard({ offset: boardOffset, limit: boardLimit, sort }, { tenant, quoteId, signal }),
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

        <ReadStatus label="Indexed 4-table consensus leaderboard" resource={consensusBoard} />
        <div className={styles.boardControls}>
          <label className={styles.boardSortLabel}>
            <span>Global rank order</span>
            <Select
              aria-label="Global consensus leaderboard sort"
              value={sort}
              onChange={(event) => {
                setSort(event.target.value as 'standing' | 'rating' | 'matches');
                setBoardOffset(0);
              }}
            >
              <option value="standing">Glicko-2 conservative standing (idx)</option>
              <option value="rating">Raw rating (mu)</option>
              <option value="matches">Witness count</option>
            </Select>
          </label>
          <div className={styles.boardPager}>
            <Button
              variant="ghost"
              disabled={boardOffset === 0}
              onClick={() => setBoardOffset((prev) => Math.max(0, prev - boardLimit))}
            >
              Previous
            </Button>
            <Muted>{`offset ${boardOffset.toLocaleString()}`}</Muted>
            <Button
              variant="ghost"
              disabled={(consensusBoard.data?.hits.length ?? 0) < boardLimit}
              onClick={() => setBoardOffset((prev) => prev + boardLimit)}
            >
              Next
            </Button>
            {consensusBoard.data && (
              <Muted>{`${consensusBoard.data.elapsed_us.toLocaleString()} us`}</Muted>
            )}
          </div>
        </div>

        {consensusBoard.data && consensusBoard.data.hits.length > 0 && (
          <div className={styles.leaderTables}>
            <Table>
              <thead>
                <tr>
                  <Th>#</Th>
                  <Th>Concept / claim</Th>
                  <Th>Tier</Th>
                  <Th>Confidence</Th>
                  <Th>Rating ± RD</Th>
                  <Th>Witnesses</Th>
                </tr>
              </thead>
              <tbody>
                {consensusBoard.data.hits.map((hit, idx) => (
                  <tr key={hit.id_hex}>
                    <Td>{boardOffset + idx + 1}</Td>
                    <Td><EntityLink idHex={hit.id_hex} label={hit.label || hit.id_hex} /></Td>
                    <Td>{hit.tier}</Td>
                    <Td>{Number(hit.confidence).toFixed(3)}</Td>
                    <Td>{`${Number(hit.rating).toFixed(1)} ± ${Number(hit.deviation).toFixed(1)}`}</Td>
                    <Td>{Number(hit.matches).toLocaleString()}</Td>
                  </tr>
                ))}
              </tbody>
            </Table>
          </div>
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
