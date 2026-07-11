import {
  historyFromPositions,
  initialPositions,
  type PositionSnapshot,
} from './gameHistory';

const STORAGE_KEY = 'laplace.chess.play.v1';

export interface SavedPlayGame {
  positions: PositionSnapshot[];
  recordToSubstrate: boolean;
  searchDepth: number;
  useSubstrate: boolean;
  autoReply: boolean;
  flip: boolean;
  evalMode: boolean;
  savedAt: number;
}

export function loadSavedPlayGame(): SavedPlayGame | null {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as SavedPlayGame;
    if (!Array.isArray(parsed.positions) || parsed.positions.length === 0) return null;
    return parsed;
  } catch {
    return null;
  }
}

export function savePlayGame(game: Omit<SavedPlayGame, 'savedAt'>): void {
  try {
    const payload: SavedPlayGame = { ...game, savedAt: Date.now() };
    localStorage.setItem(STORAGE_KEY, JSON.stringify(payload));
  } catch {
    /* quota / private mode — ignore */
  }
}

export function clearSavedPlayGame(): void {
  try {
    localStorage.removeItem(STORAGE_KEY);
  } catch {
    /* ignore */
  }
}

export function savedGameIsResumable(game: SavedPlayGame | null): boolean {
  if (!game) return false;
  const last = game.positions[game.positions.length - 1];
  return !!last && last.status === 'ongoing' && game.positions.length > 1;
}

export function movesFromSaved(game: SavedPlayGame): string[] {
  return historyFromPositions(game.positions);
}

export function freshPlayGame(
  overrides: Partial<Omit<SavedPlayGame, 'savedAt' | 'positions'>> = {},
): Omit<SavedPlayGame, 'savedAt'> {
  return {
    positions: initialPositions(),
    recordToSubstrate: true,
    searchDepth: 4,
    useSubstrate: true,
    autoReply: true,
    flip: false,
    evalMode: false,
    ...overrides,
  };
}
