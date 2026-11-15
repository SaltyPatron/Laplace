import { Table, TableScroll, Td, Th } from '@ui';
import type { CompetitionIndex } from './describe';
import { MatchCard, type Follow } from './MatchCard';
import type { Competition, Match, Stage, Standings } from './model';
import styles from './Compete.module.css';

interface StageProps {
  matches: readonly Match[];
  index: CompetitionIndex;
}

/** Independent two-sided matches as a crosstable: each row is an entrant, each column an opponent. */
export function CrossTable({ competition, matches, index }: StageProps & { competition: Competition }) {
  const entrants = competition.entrants.filter((entrant) => matches.some((match) => match.slots.some((slot) => slot.entrant === entrant.id)));
  const meetings = (row: string, column: string) => matches.flatMap((match) => {
    const mine = match.slots.find((slot) => slot.entrant === row);
    return mine && match.slots.some((slot) => slot.entrant === column) ? [{ match, mine }] : [];
  });
  return (
    <TableScroll>
      <Table className={styles.cross}>
        <thead>
          <tr>
            <Th scope="col" className={styles.crossName}>Entrant</Th>
            {entrants.map((entrant, n) => <Th key={entrant.id} scope="col" className={styles.crossCell}><abbr title={entrant.name}>{n + 1}</abbr></Th>)}
          </tr>
        </thead>
        <tbody>
          {entrants.map((row, n) => (
            <tr key={row.id}>
              <Th scope="row" className={styles.crossName}><span className={styles.seed}>{n + 1}</span> {row.name}</Th>
              {entrants.map((column) => {
                if (column.id === row.id) return <Td key={column.id} className={styles.crossSelf} aria-hidden="true" />;
                const met = meetings(row.id, column.id);
                return (
                  <Td key={column.id} className={styles.crossCell}>
                    {met.length === 0 ? '' : met.map(({ match, mine }) => (
                      <span key={match.id} className={styles.crossResult} data-won={mine.place === 1 || undefined}
                        title={`${index.matchName(match.id)}: ${row.name} v ${column.name}`}>
                        {mine.score ?? (match.state === 'final' ? (mine.place === 1 ? 'W' : 'L') : '·')}
                      </span>
                    ))}
                  </Td>
                );
              })}
            </tr>
          ))}
        </tbody>
      </Table>
    </TableScroll>
  );
}

/** Matches everyone plays at once (a sweeper, a qualifying block): each one a finishing order. */
export function Field({ matches, index }: StageProps) {
  return (
    <div className={styles.fields}>
      {matches.map((match) => {
        const order = match.slots.map((slot, position) => ({ slot, position }))
          .sort((a, b) => (a.slot.place ?? Infinity) - (b.slot.place ?? Infinity) || a.position - b.position);
        return (
          <section key={match.id} aria-label={index.matchName(match.id)}>
            <h3 className={styles.roundTitle}>{index.matchName(match.id)}</h3>
            <TableScroll>
              <Table>
                <thead><tr><Th scope="col">Place</Th><Th scope="col">Entrant</Th><Th scope="col" className={styles.number}>Score</Th></tr></thead>
                <tbody>
                  {order.map(({ slot, position }) => (
                    <tr key={position}>
                      <Td>{slot.place ?? '—'}</Td>
                      <Td>{index.entrant(slot.entrant)?.name ?? index.openSlot(slot)}</Td>
                      <Td className={styles.number}>{slot.score ?? '—'}</Td>
                    </tr>
                  ))}
                </tbody>
              </Table>
            </TableScroll>
          </section>
        );
      })}
    </div>
  );
}

/** Every match of a stage round by round, whatever its shape. */
export function Schedule({ stage, matches, index, follow }: StageProps & { stage: Stage; follow: Follow }) {
  const rounds = [...new Set(matches.map((match) => match.round))].sort((a, b) => a - b);
  return (
    <div className={styles.fields}>
      {rounds.map((round, column) => (
        <section key={round} aria-label={index.roundTitle(stage, round, column)}>
          <h3 className={styles.roundTitle}>{index.roundTitle(stage, round, column)}</h3>
          <ol className={styles.schedule}>
            {matches.filter((match) => match.round === round).map((match) => (
              <li key={match.id}><MatchCard match={match} index={index} follow={follow} /></li>
            ))}
          </ol>
        </section>
      ))}
    </div>
  );
}

/** Standings exactly as given: the browser never ranks. */
export function StandingsTable({ standings, index }: { standings: Standings; index: CompetitionIndex }) {
  return (
    <TableScroll>
      <Table>
        <thead>
          <tr>
            <Th scope="col">#</Th><Th scope="col">Entrant</Th>
            {standings.columns.map((column) => <Th key={column} scope="col" className={styles.number}>{column}</Th>)}
          </tr>
        </thead>
        <tbody>
          {standings.rows.map((row, n) => (
            <tr key={row.entrant}>
              <Td>{n + 1}</Td>
              <Td>{index.entrant(row.entrant)?.name ?? row.entrant}</Td>
              {row.cells.map((cell, c) => <Td key={c} className={styles.number}>{cell}</Td>)}
            </tr>
          ))}
        </tbody>
      </Table>
    </TableScroll>
  );
}
