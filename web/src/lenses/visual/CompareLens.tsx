import { MatchupView } from '../../explore/matchup/MatchupView';

/** This subject side by side with another. */
export default function CompareLens({ id }: { id: string }) {
  return <MatchupView selectedId={id} embedded />;
}
