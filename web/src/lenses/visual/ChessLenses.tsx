import { lazy, Suspense } from 'react';
import { LoadingText } from '../../ui/primitives/Text';

const GamePage = lazy(() => import('../../chess/db/GamePage').then((m) => ({ default: m.GamePage })));

/** A recorded chess game: headers and a replayable board. */
export function GameLens({ id }: { id: string }) {
  return <Suspense fallback={<LoadingText>Loading game…</LoadingText>}><GamePage selectedId={id} embedded /></Suspense>;
}
