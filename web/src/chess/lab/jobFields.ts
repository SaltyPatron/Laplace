/** Parameter field definitions and defaults for each Chess Lab job kind. */
import type { LabExperiment } from './experiments';
import type { LabJobSpec } from './sse';

export type FieldDef =
  | { key: string; label: string; type: 'number'; min: number; max?: number; step?: number; unit?: string; help?: string; placeholder?: string }
  | { key: string; label: string; type: 'text' | 'password'; help?: string; placeholder?: string }
  | { key: string; label: string; type: 'select'; options: string[]; help?: string; optionLabels?: Record<string, string> }
  | { key: string; label: string; type: 'bool'; help?: string };

export const JOB_FIELDS: Record<string, FieldDef[]> = {
  'substrate-test': [
    {
      key: 'mode',
      label: 'Decision mode',
      type: 'select',
      options: ['transition', 'off'],
      optionLabels: {
        transition: 'Laplace — substrate forward pass',
        off: 'Off — sanity (pure vs pure)',
      },
      help: 'Laplace fuses position, move-physicality, and child-structure evidence; Off runs the conventional control.',
    },
    { key: 'games', label: 'Games', type: 'number', min: 1, help: 'More games = tighter Elo estimate. No upper limit.', placeholder: '100' },
    { key: 'depth', label: 'Control depth', type: 'number', min: 1, max: 12, help: 'Depth of the conventional control. Laplace independently fuses transition, move-physicality, and child-structure evidence.' },
    { key: 'maxPlies', label: 'Max plies', type: 'number', min: 10, max: 400, help: 'Declare a draw if the game exceeds this length.' },
    { key: 'concurrency', label: 'Parallel games', type: 'number', min: 0, help: '0 = use all performance CPU cores.', placeholder: '0' },
    { key: 'openings', label: 'Opening book', type: 'bool', help: 'Seed from ingested ECO positions instead of random starts.' },
  ],
  ladder: [
    { key: 'games', label: 'Games per term', type: 'number', min: 1, help: 'Each of 6 eval terms runs this many games. Total = ×6.', placeholder: '100' },
    { key: 'depth', label: 'Search depth', type: 'number', min: 1, max: 12 },
    { key: 'maxPlies', label: 'Max plies', type: 'number', min: 10, max: 400, help: 'Draw adjudication cutoff.' },
    { key: 'concurrency', label: 'Core budget', type: 'number', min: 0, help: 'Shared across all 6 terms in parallel. 0 = all performance cores.', placeholder: '0' },
  ],
  tactics: [
    { key: 'depth', label: 'Search depth', type: 'number', min: 1, max: 16, help: 'Depth for each mate puzzle.' },
  ],
  review: [
    { key: 'path', label: 'PGN path (server)', type: 'text', help: 'Absolute path on the API host.', placeholder: 'D:\\Data\\…\\games.pgn' },
    { key: 'depth', label: 'Analysis depth', type: 'number', min: 1, max: 12 },
    { key: 'maxGames', label: 'Max games', type: 'number', min: 1, max: 100 },
  ],
  'lichess-fetch': [
    { key: 'user', label: 'Username', type: 'text', placeholder: 'DrNykterstein' },
    { key: 'site', label: 'Source', type: 'select', options: ['chesscom', 'lichess'], optionLabels: { chesscom: 'Chess.com', lichess: 'Lichess' } },
    { key: 'all', label: 'Ingest all games', type: 'bool', help: 'Omit the provider cap and stream the complete available archive.' },
    { key: 'max', label: 'Game limit', type: 'number', min: 1, step: 100, help: 'Used only when “Ingest all games” is off.', placeholder: '1000' },
    { key: 'ingest', label: 'Write to Laplace', type: 'bool', help: 'Record the downloaded games immediately.' },
  ],
  'player-profile': [
    { key: 'user', label: 'Username', type: 'text', placeholder: 'MagnusCarlsen' },
    { key: 'site', label: 'Source', type: 'select', options: ['chesscom', 'lichess'], optionLabels: { chesscom: 'Chess.com', lichess: 'Lichess' } },
    { key: 'fideId', label: 'FIDE ID', type: 'text', help: 'Optional official FIDE profile to fetch alongside. Leave blank for the provider profile only; use Find FIDE identity to discover candidates.', placeholder: '1503014' },
  ],
  'fide-search': [
    { key: 'query', label: 'Player name', type: 'text', help: 'Search the official FIDE ratings database by real name.', placeholder: 'Carlsen, Magnus' },
    { key: 'limit', label: 'Candidate limit', type: 'number', min: 1, max: 100 },
  ],
  'fide-profile': [
    { key: 'fideId', label: 'FIDE ID', type: 'text', help: 'Fetch one official profile.', placeholder: '1503014' },
  ],
  'fide-roster': [
    {
      key: 'cohort', label: 'Official ranking', type: 'select',
      options: ['open', 'women', 'juniors', 'girls', 'men_rapid', 'women_rapid', 'juniors_rapid', 'girls_rapid', 'men_blitz', 'women_blitz', 'juniors_blitz', 'girls_blitz'],
      optionLabels: {
        open: 'Standard open', women: 'Standard women', juniors: 'Standard juniors', girls: 'Standard girls',
        men_rapid: 'Rapid open', women_rapid: 'Rapid women', juniors_rapid: 'Rapid juniors', girls_rapid: 'Rapid girls',
        men_blitz: 'Blitz open', women_blitz: 'Blitz women', juniors_blitz: 'Blitz juniors', girls_blitz: 'Blitz girls',
      },
    },
    { key: 'limit', label: 'Top players', type: 'number', min: 1, max: 100 },
  ],
};

export const FALLBACK_JOBS: LabJobSpec[] = [{ kind: 'substrate-test', label: 'Substrate test', default: { games: '20', depth: '4', mode: 'transition' } }];

function fieldDefault(f: FieldDef): string {
  if (f.type === 'bool') return 'false';
  if (f.type === 'select') return f.options[0];
  return f.type === 'number' ? String(f.min) : '';
}

export function paramsFor(kind: string, spec: LabJobSpec | undefined): Record<string, string> {
  const out: Record<string, string> = {};
  for (const f of JOB_FIELDS[kind] ?? []) out[f.key] = spec?.default?.[f.key] ?? fieldDefault(f);
  return out;
}

export function requiresFor(exp: LabExperiment | undefined): string[] {
  return exp?.requires ?? [];
}
