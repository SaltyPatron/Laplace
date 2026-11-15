import { UnstyledButton } from '@ui';
import type { CompetitionIndex } from './describe';
import { matchStatus } from './describe';
import type { Match } from './model';
import styles from './Compete.module.css';

/** Following one entrant through a competition: hovering shows the route, pressing keeps it. */
export interface Follow {
  entrant: string | null;
  hover(entrant: string | null): void;
  pin(entrant: string): void;
  pinned: string | null;
}

export interface MatchCardProps {
  match: Match;
  index: CompetitionIndex;
  follow: Follow;
}

/** One match: its name, its state, and a row per slot. Its height is exactly HEAD + slots × ROW. */
export function MatchCard({ match, index, follow }: MatchCardProps) {
  const name = index.matchName(match.id);
  return (
    <article className={styles.card} data-state={match.state} aria-label={name}>
      <header className={styles.cardHead}>
        <span className={styles.cardName}>{name}</span>
        <span className={styles.cardStatus}>{matchStatus(match)}</span>
      </header>
      <ol className={styles.slots}>
        {match.slots.map((slot, position) => {
          const entrant = index.entrant(slot.entrant);
          if (!entrant) {
            return <li key={position} className={styles.slot} data-open><span className={styles.slotName}>{index.openSlot(slot)}</span></li>;
          }
          const won = slot.place === 1;
          return (
            <li key={position} className={styles.slot} data-won={won || undefined}
              data-out={(match.state === 'final' && !won) || undefined}
              data-on={follow.entrant === entrant.id || undefined}>
              <UnstyledButton className={styles.slotButton} aria-pressed={follow.pinned === entrant.id}
                aria-label={`Follow ${entrant.name}`}
                onClick={() => follow.pin(entrant.id)}
                onPointerEnter={(event) => { if (event.pointerType === 'mouse') follow.hover(entrant.id); }}
                onPointerLeave={(event) => { if (event.pointerType === 'mouse') follow.hover(null); }}>
                {entrant.seed != null && <span className={styles.seed}>{entrant.seed}</span>}
                <span className={styles.slotName}>{entrant.name}</span>
              </UnstyledButton>
              {slot.score != null && <span className={styles.score}>{slot.score}</span>}
            </li>
          );
        })}
      </ol>
    </article>
  );
}
