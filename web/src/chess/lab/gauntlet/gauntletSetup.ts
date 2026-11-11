/** Gauntlet match setup types, defaults and required-binary table. */

export const ENGINES: { key: string; label: string; fix: string }[] = [
  { key: 'stockfish', label: 'Stockfish', fix: 'LAPLACE_STOCKFISH' },
  { key: 'laplaceUci', label: 'laplace-uci', fix: 'ships beside the API host — publish it' },
];

export interface Preview {
  fileName: string;
  arguments: string[];
  commandLine: string;
  workingDirectory: string;
  games: number;
  ready: boolean;
  missing: { name: string; hint: string; looked: string | null; source: string }[];
}

export interface Setup {
  laplaceProfile: string;
  stockfishProfile: string;
  conductor: 'fastchess' | 'cutechess';
  clock: 'seconds' | 'depth';
  st: string;
  depth: string;
  rounds: string;
  elo: string;
  limitStrength: boolean;
  concurrency: string;
  stockfishThreads: string;
  stockfishHashMb: string;
  stockfishNumaPolicy: string;
  stockfishSyzygyPath: string;
  ingest: boolean;
}

export const DEFAULT_SETUP: Setup = {
  laplaceProfile: '',
  stockfishProfile: '',
  conductor: 'fastchess',
  clock: 'seconds',
  st: '1',
  depth: '8',
  rounds: '10',
  elo: '2000',
  limitStrength: true,
  concurrency: '1',
  stockfishThreads: '',
  stockfishHashMb: '',
  stockfishNumaPolicy: '',
  stockfishSyzygyPath: '',
  ingest: true,
};

export interface GameRow {
  index: number;
  white: string;
  black: string;
  result: string;
}

export const RUNNING = new Set(['Running', 'Pending']);
