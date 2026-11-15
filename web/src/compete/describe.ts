/** The words a competition is read in: entrant names, match names, and where an open slot's entrant will come from. */
import type { Competition, Entrant, Match, Slot, Stage } from './model';

export interface CompetitionIndex {
  entrant(id: string | undefined): Entrant | undefined;
  matchName(id: string): string;
  /** "Winner of Semifinal 1", "3rd of Qualifying", or "Bye" for a slot nobody fills. */
  openSlot(slot: Slot): string;
  roundTitle(stage: Stage, round: number, column: number): string;
}

const ordinal = (n: number) => {
  const tens = n % 100;
  const suffix = tens >= 11 && tens <= 13 ? 'th' : ['th', 'st', 'nd', 'rd'][n % 10] ?? 'th';
  return `${n}${suffix}`;
};

export function indexCompetition(competition: Competition): CompetitionIndex {
  const entrants = new Map(competition.entrants.map((entrant) => [entrant.id, entrant]));
  const matches = new Map<string, Match>(competition.matches.map((match) => [match.id, match]));
  const names = new Map(competition.matches.map((match, order) => [match.id, match.name ?? `Match ${order + 1}`]));
  const matchName = (id: string) => names.get(id) ?? id;
  return {
    entrant: (id) => (id == null ? undefined : entrants.get(id)),
    matchName,
    openSlot(slot) {
      if (!slot.from) return 'Bye';
      const source = matches.get(slot.from.match);
      const place = slot.from.place === 1 ? 'Winner'
        : slot.from.place === 2 && source?.slots.length === 2 ? 'Loser'
        : ordinal(slot.from.place);
      return `${place} of ${matchName(slot.from.match)}`;
    },
    roundTitle: (stage, round, column) => stage.rounds?.[column] ?? `Round ${round}`,
  };
}

const whenFormat = new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });

/** What the corner of a match says: its state once it has one, otherwise when it starts. */
export function matchStatus(match: Match): string {
  if (match.state === 'live') return 'Live';
  if (match.state === 'final') return 'Final';
  if (!match.when) return '';
  const time = new Date(match.when);
  return Number.isNaN(time.getTime()) ? match.when : whenFormat.format(time);
}
