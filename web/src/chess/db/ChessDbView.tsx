import { lazy, Suspense } from 'react';
import { Navigate, Route, Routes } from 'react-router-dom';
import styles from './ChessDb.module.css';
import { LoadingText } from '@ui';

const PlayersIndex = lazy(() => import('./PlayersIndex').then((m) => ({ default: m.PlayersIndex })));
const PlayerPage = lazy(() => import('./PlayerPage').then((m) => ({ default: m.PlayerPage })));
const GamePage = lazy(() => import('./GamePage').then((m) => ({ default: m.GamePage })));
const LaplaceGames = lazy(() => import('./LaplaceGames').then((m) => ({ default: m.LaplaceGames })));

/**
 * Routes the recorded chess reads, master into detail: player search → player → game →
 * the opponent. A player is addressed by name as written, a game by its record id.
 */
export function ChessDbView() {
  return (
    <div className={styles.dbPage} data-chess-scroll-root>
      <Suspense fallback={<LoadingText>Loading chess database view…</LoadingText>}>
      <Routes>
        <Route index element={<PlayersIndex />} />
        <Route path="laplace" element={<LaplaceGames />} />
        <Route path="players/:name" element={<PlayerPage />} />
        <Route path="games/:idHex" element={<GamePage />} />
        <Route path="*" element={<Navigate to="/chess" replace />} />
      </Routes>
      </Suspense>
    </div>
  );
}
