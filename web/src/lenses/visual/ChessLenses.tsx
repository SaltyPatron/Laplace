import { lazy, Suspense } from 'react';
import { LoadingText } from '../../ui/primitives/Text';

const PlayerPage = lazy(() => import('../../chess/db/PlayerPage').then((m) => ({ default: m.PlayerPage })));
const GamePage = lazy(() => import('../../chess/db/GamePage').then((m) => ({ default: m.GamePage })));

/** A chess player's profiles, record and games. */
export function PlayerLens({ id }: { id: string }) {
  return <Suspense fallback={<LoadingText>Loading player…</LoadingText>}><PlayerPage selectedId={id} embedded /></Suspense>;
}

/** A recorded chess game: headers and a replayable board. */
export function GameLens({ id }: { id: string }) {
  return <Suspense fallback={<LoadingText>Loading game…</LoadingText>}><GamePage selectedId={id} embedded /></Suspense>;
}
