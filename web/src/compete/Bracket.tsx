import { useMemo, type CSSProperties } from 'react';
import type { CompetitionIndex } from './describe';
import { COL, GAP, layoutStage } from './layout';
import { MatchCard, type Follow } from './MatchCard';
import type { Match, Stage } from './model';
import styles from './Compete.module.css';

export interface BracketProps {
  stage: Stage;
  matches: readonly Match[];
  index: CompetitionIndex;
  follow: Follow;
}

/**
 * A stage whose matches feed one another, drawn as columns of rounds joined by connectors. In a
 * narrow container the same markup becomes one round per swipe and the connectors give way to the
 * "Winner of …" wording each open slot already carries.
 */
export function Bracket({ stage, matches, index, follow }: BracketProps) {
  const layout = useMemo(() => layoutStage(matches), [matches]);
  const byId = useMemo(() => new Map(matches.map((match) => [match.id, match])), [matches]);
  const size = { '--w': `${layout.width}rem`, '--h': `${layout.height}rem` } as CSSProperties;
  return (
    <div className={styles.bracket}>
      <div className={styles.canvas} style={size}>
        <svg className={styles.links} viewBox={`0 0 ${layout.width} ${layout.height}`} aria-hidden="true" focusable="false">
          {layout.links.map((link) => {
            const entrant = byId.get(link.to)?.slots[link.slot]?.entrant;
            const turn = link.x1 + GAP / 2;
            return (
              <path key={`${link.from}>${link.to}:${link.slot}`}
                d={`M${link.x1} ${link.y1}H${turn}V${link.y2}H${link.x2}`}
                data-open={entrant == null || undefined}
                data-on={(entrant != null && entrant === follow.entrant) || undefined} />
            );
          })}
        </svg>
        {layout.rounds.map((round, column) => {
          const title = index.roundTitle(stage, round, column);
          const at = { '--x': `${column * (COL + GAP)}rem` } as CSSProperties;
          return (
            <section key={round} className={styles.round} style={at} aria-label={title}>
              <h3 className={styles.roundTitle}>{title}</h3>
              <ol className={styles.roundList}>
                {layout.boxes.filter((box) => box.column === column).map((box) => {
                  const match = byId.get(box.id);
                  if (!match) return null;
                  const top = { '--y': `${box.y}rem` } as CSSProperties;
                  return <li key={box.id} className={styles.node} style={top}><MatchCard match={match} index={index} follow={follow} /></li>;
                })}
              </ol>
            </section>
          );
        })}
      </div>
    </div>
  );
}
