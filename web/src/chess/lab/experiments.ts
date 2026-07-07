export type LabCategory = 'substrate' | 'diagnostics' | 'external' | 'lichess';

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
    id: 'external',
    label: 'External gauntlet',
    blurb: 'Pit Laplace UCI against Stockfish via cutechess-cli.',
  },
  {
    id: 'lichess',
    label: 'Lichess & data',
    blurb: 'Fetch games for ingest. Live play uses the Lichess connectivity panel above.',
  },
];

export const LAB_EXPERIMENTS: LabExperiment[] = [
  {
    kind: 'substrate-test',
    title: 'Substrate lift test',
    tagline: 'Does consensus make search smarter?',
    description:
      'Plays guided (substrate-biased root moves) against a pure classical engine at the same depth. '
      + 'Positive Elo means the substrate measurably raises the floor — the honest transfer test for recorded evidence.',
    expect: [
      'Live W-D-L score and Elo difference in the feed',
      'Final results table with Elo ± margin',
      'games_recorded metric — every game is witnessed to substrate during the run',
      'games.pgn artifact for archival',
    ],
    tips: [
      'Start with mode fold (substructure consensus); edge is raw move popularity.',
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
    kind: 'cutechess',
    title: 'cutechess vs Stockfish',
    tagline: 'External engine gauntlet.',
    description:
      'Runs cutechess-cli: laplace-uci (from install root) vs Stockfish at fixed depth. '
      + 'Progress streams from the CLI; PGN written when complete.',
    expect: [
      'Round-by-round log lines and progress',
      'games.pgn artifact — use Ingest to add witness evidence if not auto-recorded',
      'Requires cutechess, Stockfish, Qt, and laplace-uci on the server',
    ],
    tips: [
      'Check engine chips above — all four must be green.',
      'Rounds × 2 games (colors alternate). Depth is UCI go depth.',
    ],
    category: 'external',
    recordsLive: false,
    requires: ['cutechess', 'stockfish', 'qt', 'laplaceUci'],
  },
  {
    kind: 'lichess-fetch',
    title: 'Fetch player PGN',
    tagline: 'Download games from Lichess or Chess.com.',
    description:
      'Pulls recent games for a username into a server-side PGN file. Good precursor to review or manual ingest.',
    expect: [
      'games_fetched count',
      'games.pgn artifact with download link',
    ],
    tips: [
      'Set max to cap download size; leave empty for provider default.',
      'Chess.com usernames are case-sensitive.',
    ],
    category: 'lichess',
    recordsLive: false,
  },
];

const byKind = new Map(LAB_EXPERIMENTS.map((e) => [e.kind, e]));

export function experimentFor(kind: string): LabExperiment | undefined {
  return byKind.get(kind);
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
