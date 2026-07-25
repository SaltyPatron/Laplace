import { Navigate, Route, Routes } from 'react-router-dom';
import { PlayersIndex } from './PlayersIndex';
import { PlayerPage } from './PlayerPage';
import { GamePage } from './GamePage';

/**
 * The chess database — the read half of the chess pillar. Play and Lab drive a
 * board; this browses what the substrate already witnessed, master into detail:
 * roster → career → game → the other player's career.
 */
export function ChessDbView() {
  return (
    <Routes>
      <Route index element={<PlayersIndex />} />
      <Route path="players/:idHex" element={<PlayerPage />} />
      <Route path="games/:idHex" element={<GamePage />} />
      <Route path="*" element={<Navigate to="/chess" replace />} />
    </Routes>
  );
}
