import { lazy, Suspense } from 'react';
import { Navigate, Route, Routes } from 'react-router-dom';
import styles from './ChessDb.module.css';
import { LoadingText } from '@ui';

const PlayersIndex = lazy(() => import('./PlayersIndex').then((m) => ({ default: m.PlayersIndex })));
const PlayerPage = lazy(() => import('./PlayerPage').then((m) => ({ default: m.PlayerPage })));
const GamePage = lazy(() => import('./GamePage').then((m) => ({ default: m.GamePage })));
const LaplaceGames = lazy(() => import('./LaplaceGames').then((m) => ({ default: m.LaplaceGames })));

/**
 * The chess database — the read half of the chess pillar. Play and Lab drive a
 * board; this browses what the substrate already witnessed, master into detail:
 * roster → career → game → the other player's career.
 */
export function ChessDbView() {
  return (
    <div className={styles.dbPage} data-chess-scroll-root>
      <Suspense fallback={<LoadingText>Loading chess database view…</LoadingText>}>
      <Routes>
        <Route index element={<PlayersIndex />} />
        <Route path="laplace" element={<LaplaceGames />} />
        <Route path="players/:idHex" element={<PlayerPage />} />
        <Route path="games/:idHex" element={<GamePage />} />
        <Route path="*" element={<Navigate to="/chess" replace />} />
      </Routes>
      </Suspense>
    </div>
  );
}
