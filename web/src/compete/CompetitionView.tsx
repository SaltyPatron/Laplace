import { useMemo, useState, type CSSProperties } from 'react';
import { Muted, SegmentedControl } from '@ui';
import { Bracket } from './Bracket';
import { indexCompetition } from './describe';
import { COL, HEAD, ROW, stageShape, type StageShape } from './layout';
import type { Follow } from './MatchCard';
import type { Competition } from './model';
import { CrossTable, Field, Schedule, StandingsTable } from './Tables';
import styles from './Compete.module.css';

const SHAPE_LABEL: Record<StageShape, string> = { tree: 'Bracket', pairs: 'Grid', field: 'Results' };
const metrics = { '--col': `${COL}rem`, '--head': `${HEAD}rem`, '--row': `${ROW}rem` } as CSSProperties;

/** A whole competition: its stages, each drawn in the shape its matches have, with a schedule and standings. */
export function CompetitionView({ competition }: { competition: Competition }) {
  const index = useMemo(() => indexCompetition(competition), [competition]);
  const [stageId, setStageId] = useState(competition.stages[0]?.id ?? '');
  const [view, setView] = useState<'shape' | 'schedule'>('shape');
  const [pinned, setPinned] = useState<string | null>(null);
  const [hovered, setHovered] = useState<string | null>(null);
  const stage = competition.stages.find((candidate) => candidate.id === stageId) ?? competition.stages[0];
  const matches = useMemo(() => competition.matches.filter((match) => match.stage === stage?.id), [competition, stage]);
  if (!stage) return <Muted>This competition has no stages yet.</Muted>;

  const shape = stageShape(matches);
  const standings = competition.standings?.find((table) => table.stage === stage.id);
  const follow: Follow = {
    entrant: hovered ?? pinned,
    pinned,
    hover: setHovered,
    pin: (entrant) => setPinned((current) => (current === entrant ? null : entrant)),
  };
  return (
    <div className={styles.compete} style={metrics}>
      <header className={styles.top}>
        <h2 className={styles.title}>{competition.title}</h2>
        <div className={styles.controls}>
          {competition.stages.length > 1 && (
            <SegmentedControl label="Stage" value={stage.id} onValueChange={setStageId}
              options={competition.stages.map((each) => each.id)}
              optionLabels={Object.fromEntries(competition.stages.map((each) => [each.id, each.title]))} />
          )}
          <SegmentedControl label="View" value={view} onValueChange={(next) => setView(next === 'schedule' ? 'schedule' : 'shape')}
            options={['shape', 'schedule']} optionLabels={{ shape: SHAPE_LABEL[shape], schedule: 'Schedule' }} />
        </div>
      </header>
      {matches.length === 0 ? <Muted>No matches are set for {stage.title} yet.</Muted>
        : view === 'schedule' ? <Schedule stage={stage} matches={matches} index={index} follow={follow} />
        : shape === 'tree' ? <Bracket stage={stage} matches={matches} index={index} follow={follow} />
        : shape === 'pairs' ? <CrossTable competition={competition} matches={matches} index={index} />
        : <Field matches={matches} index={index} />}
      {standings && (
        <section aria-label="Standings">
          <h3 className={styles.sectionTitle}>Standings</h3>
          <StandingsTable standings={standings} index={index} />
        </section>
      )}
    </div>
  );
}
