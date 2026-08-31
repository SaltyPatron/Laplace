export type LabCategory = 'substrate' | 'diagnostics' | 'lichess';

export interface LabExperiment {
  kind: string;
  title: string;
  tagline: string;
  description: string;
  expect: string[];
  tips: string[];
  category: LabCategory;
  /** Games written to substrate during the run (LearnGameAsync path). */
  recordsLive: boolean;
  requires?: string[];
}

export const LAB_CATEGORIES: { id: LabCategory; label: string; blurb: string }[] = [
  {
    id: 'substrate',
    label: 'Substrate evaluation',
    blurb: 'Measure and extend what the consensus graph knows about chess.',
  },
  {
    id: 'diagnostics',
    label: 'Engine diagnostics',
    blurb: 'Quick sanity checks on search strength and game review.',
  },
  {
    id: 'lichess',
    label: 'Lichess & data',
    blurb: 'Fetch games for ingest, and run the bot against live opponents.',
  },
];

export const LAB_EXPERIMENTS: LabExperiment[] = [
  {
    kind: 'substrate-test',
    title: 'Substrate lift test',
    tagline: 'Does witnessed substrate experience beat classical search?',
    description:
      'Laplace fuses position transitions, reusable move physicality, and composed child-state structure while the control uses classical search. '
      + 'Completed games advance substrate evidence, so later games in the same run consume the new state.',
    expect: [
      'Live W-D-L score and Elo difference in the feed',
      'Final results table with Elo ± margin',
      'games_recorded metric — every game is witnessed to substrate during the run',
      'bounded trunk reads plus exact-transition, move-physicality, child-structure, and substrate-epoch metrics',
      'games.pgn artifact for archival',
    ],
    tips: [
      'Transition mode is the Laplace path; Off is the conventional sanity control.',
      'Use opening book when the corpus has ECO coverage — random starts need more games.',
      'Concurrency 0 uses all performance cores; scale games before depth for stable Elo.',
    ],
    category: 'substrate',
    recordsLive: true,
  },
  {
    kind: 'ladder',
    title: 'Eval overlay ladder',
    tagline: 'Which eval terms actually matter?',
    description:
      'For each classical overlay (material, PST, bishop pair, …), plays full eval vs eval-minus-that-term. '
      + 'Positive Elo on a row means removing that overlay weakens the engine — the overlay helps.',
    expect: [
      'Six-term ablation table with W-D-L and Elo per row',
      'Parallel progress across terms in the job summary',
      'All games recorded to substrate (not throwaway ablation)',
      'games.pgn combining every term\'s games',
    ],
    tips: [
      'This is in-process Search — not laplace-uci vs Stockfish.',
      'Core budget splits across six terms; 0 = all performance cores.',
      'Large game counts are fine — stop cancels in-flight parallel search.',
    ],
    category: 'substrate',
    recordsLive: true,
  },
  {
    kind: 'learned-pst',
    title: 'Learned PST grid',
    tagline: 'What the corpus learned about squares.',
    description:
      'Reads the data-driven piece-square table already folded into consensus — deviation from a draw baseline, '
      + 'witness-weighted per square. Instant read; no games played.',
    expect: [
      'Table of top squares by deviation for each piece type',
      'Coverage percentage per piece',
    ],
    tips: [
      'Run substrate-test or ladder first if the grid is sparse.',
      'Positive deviation = good for the side to move from that square.',
    ],
    category: 'substrate',
    recordsLive: false,
  },
  {
    kind: 'tactics',
    title: 'Tactics solve rate',
    tagline: 'Can the engine find mates?',
    description:
      'Runs the built-in mate-in-N EPD suite at your chosen depth. Reports solve rate and per-position hits/misses.',
    expect: [
      'solve_rate metric as a percentage',
      'Per-position table: id, ok/miss, engine move, expected move',
    ],
    tips: [
      'Depth 6+ for harder mates; depth 4 is a quick smoke test.',
      'Does not write to substrate — pure engine diagnostic.',
    ],
    category: 'diagnostics',
    recordsLive: false,
  },
  {
    kind: 'review',
    title: 'PGN review triage',
    tagline: 'Find blunders and crazy wins.',
    description:
      'Analyzes a server-side PGN file: centipawn loss per side, blunder counts, and flags wins where the winner was '
      + 'down significant material (eval blind-spot candidates).',
    expect: [
      'Per-game table: players, result, ACPL, crazy-win flag',
      'Worst-move details logged for flagged games',
    ],
    tips: [
      'Path must exist on the server (not your local machine).',
      'Use lichess-fetch first to pull games, then point review at the artifact path.',
    ],
    category: 'diagnostics',
    recordsLive: false,
  },
  {
    kind: 'lichess-fetch',
    title: 'Ingest player games',
    tagline: 'Import a player’s complete Lichess or Chess.com archive and identity.',
    description:
      'Streams the requested archive, records and analyzes novel games, attributes them to the provider username, and imports provider/FIDE profile links.',
    expect: [
      'games_fetched count',
      'games_ingested and profiles_ingested counts',
      'games.pgn artifact with download link',
    ],
    tips: [
      'Leave “Ingest all games” on for the complete available archive; turn it off to apply a cap.',
      'Add a FIDE ID to connect the online account to an official real-world identity.',
    ],
    category: 'lichess',
    recordsLive: false,
  },
  {
    kind: 'fide-search',
    title: 'Find FIDE identity',
    tagline: 'Resolve a real name to official FIDE candidates before associating an account.',
    description:
      'Searches the official FIDE ratings database and returns FIDE ID, name, title, federation, ratings, and birth year for disambiguation.',
    expect: ['Ranked candidate table with official FIDE IDs', 'Standard, rapid, and blitz ratings where published'],
    tips: ['Search either “Magnus Carlsen” or “Carlsen, Magnus”, then paste the selected FIDE ID into player ingest.'],
    category: 'lichess',
    recordsLive: false,
  },
  {
    kind: 'player-profile',
    title: 'Associate player identities',
    tagline: 'Acquire provider and FIDE profiles without redownloading games.',
    description:
      'Fetches the selected Chess.com or Lichess profile and an optional official FIDE profile, then writes their metadata and identity link as one substrate operation. If no FIDE ID is supplied, it shows official candidates from the provider real name.',
    expect: ['Provider and official profile table', 'Downloadable profile JSON', 'identity_links receipt or FIDE candidates'],
    tips: ['Use Find FIDE identity when several people share the same name; only an explicitly selected FIDE ID is associated.'],
    category: 'lichess',
    recordsLive: false,
  },
  {
    kind: 'fide-roster',
    title: 'Ingest FIDE top players',
    tagline: 'Acquire an official top-N cohort as player profiles.',
    description:
      'Reads FIDE’s official open, women, junior, or girls ranking for standard, rapid, or blitz; fetches each selected profile; and writes the cohort in one substrate operation.',
    expect: ['Official ranked player table', 'Profile acquisition progress', 'profiles_ingested receipt'],
    tips: ['Start with 25 to inspect the cohort; the official pages currently publish up to 100 per list.'],
    category: 'lichess',
    recordsLive: true,
  },
];

const byKind = new Map(LAB_EXPERIMENTS.map((e) => [e.kind, e]));

/**
 * Job records carry the server enum name ("SubstrateTest", "LearnedPst"); the catalog and
 * every start request use the kebab form. Without this the job list rendered raw enum names
 * because no lookup ever matched.
 */
export function normalizeKind(kind: string): string {
  return kind.includes('-') ? kind : kind.replace(/(?!^)([A-Z])/g, '-$1').toLowerCase();
}

export function experimentFor(kind: string): LabExperiment | undefined {
  return byKind.get(normalizeKind(kind));
}

export function experimentsInCategory(cat: LabCategory): LabExperiment[] {
  return LAB_EXPERIMENTS.filter((e) => e.category === cat);
}

export const ENGINE_LABELS: Record<string, string> = {
  cutechess: 'cutechess-cli',
  stockfish: 'Stockfish',
  qt: 'Qt runtime',
  laplaceUci: 'laplace-uci',
};
