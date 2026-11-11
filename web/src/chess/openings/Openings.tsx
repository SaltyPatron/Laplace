import { Muted, ReadStatus, useReadResource } from '@ui';
import { mostSpecific, positionOpenings, type OpeningLine } from './api';
import styles from './Openings.module.css';

/** One named opening: ECO, name and its length in plies. */
export function OpeningName({ opening }: { opening: OpeningLine }) {
  return (
    <span className={styles.name}>
      <abbr className={styles.eco} title="Encyclopaedia of Chess Openings code">{opening.eco}</abbr>
      <span>{opening.name}</span>
    </span>
  );
}

/**
 * The named openings a position belongs to, most specific first. Broader lines that also
 * hold it follow, so a transposition shows every line the position sits on.
 */
export function OpeningList({ openings, emptyText }: { openings: OpeningLine[]; emptyText: string }) {
  if (openings.length === 0) return <Muted>{emptyText}</Muted>;
  const specific = mostSpecific(openings)!;
  const broader = openings.slice(0, -1).reverse();
  return (
    <div className={styles.list}>
      <p className={styles.primary}><OpeningName opening={specific} /> <Muted>{specific.plies} plies</Muted></p>
      {broader.length > 0 && (
        <ul className={styles.broader} aria-label="Broader openings containing this position">
          {broader.map((opening) => (
            <li key={opening.line}><OpeningName opening={opening} /> <Muted>{opening.plies} plies</Muted></li>
          ))}
        </ul>
      )}
    </div>
  );
}

/** The openings of the position on a board, read by FEN from the substrate. */
export function PositionOpenings({ fen }: { fen: string }) {
  const read = useReadResource({ key: JSON.stringify(['chess-position-openings', fen]), read: (signal) => positionOpenings(fen, signal) });
  return (
    <section className={styles.panel} aria-label="Openings of this position">
      <h3 className={styles.heading}>Opening</h3>
      <ReadStatus label="Openings" resource={read} />
      {read.data && (
        <>
          <OpeningList openings={read.data.openings} emptyText="No named opening contains this position." />
          <Muted className={styles.games}>
            {read.data.games === 0 ? 'No recorded game reached it.' : `Reached in ${read.data.games.toLocaleString()} recorded ${read.data.games === 1 ? 'game' : 'games'}.`}
          </Muted>
        </>
      )}
    </section>
  );
}
