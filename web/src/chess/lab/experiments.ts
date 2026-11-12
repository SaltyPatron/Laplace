export type LabCategory = 'substrate' | 'diagnostics' | 'import';

export interface LabExperiment {
  kind: string;
  title: string;
  tagline: string;
  description: string;
  expect: string[];
  tips: string[];
  category: LabCategory;
  /** The run writes the games it plays into the substrate; false means a read-only run. */
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
    id: 'import',
    label: 'Profiles & games',
    blurb: 'Look up FIDE, Chess.com, and Lichess profiles and ingest player games.',
  },
];

export const LAB_EXPERIMENTS: LabExperiment[] = [
  {
    kind: 'substrate-test',
    title: 'Substrate lift test',
    tagline: 'Real move-selection test: does witnessed substrate experience beat the control?',
    description:
      'MOVE-SELECTION PARTICIPATION: YES in transition mode. The Laplace side runs Search with witnessed root transition/move evidence, substrate leaf evaluation, learned PST/tactical residuals when populated, and exact Syzygy closure; the Off side is the conventional control. '
      + 'LEARNING BOUNDARY: measured games do not train one another. All plies are buffered while the match uses one frozen evidence generation; only after the measured batch closes are completed games written to substrate, so they can affect later runs but never sibling games in the same measurement. The run metrics prove combined substrate reads/signals; the standalone UCI provider receipt gives per-search learned-PST/tactical/Syzygy attribution.',
    expect: [
      'Live W-D-L score and Elo difference in the feed',
      'Final results table with Elo ± margin',
      'games_recorded metric — completed games are committed after the measured batch closes',
      'root/transition and child-state evidence metrics; use the UCI provider receipt for learned-PST/tactical/Syzygy per-search attribution',
      'games.pgn artifact for archival',
    ],
    tips: [
      'Transition mode is the actual substrate-enabled playing path; Off is the conventional sanity control.',
      'A loaded provider with zero reads/non-zero contributions did not affect that search. Use receipts/metrics, not the label.',
      'The evidence epoch is frozen for the measured batch; newly recorded games participate only in later runs.',
      'Concurrency 0 uses all performance cores; scale games before depth for stable Elo.',
    ],
    category: 'substrate',
    recordsLive: true,
  },
  {
    kind: 'ladder',
    title: 'Eval overlay ladder',
    tagline: 'Classical ablation only — not proof that substrate learning participates.',
    description:
      'MOVE-SELECTION PARTICIPATION: CLASSICAL ONLY. For each deterministic eval term (material, PST, bishop pair, rook files, pawn structure, tempo), this job plays full classical eval vs classical eval-minus-that-term. It does NOT exercise the substrate provider stack, learned PST residual, learned tactic outcomes, player conditioning, or Syzygy as evidence that Laplace learned. '
      + 'LEARNING: NO in the normal Lab UI. The runner defaults record=false and the UI exposes no record control, so these matches are read-only diagnostics unless an external caller explicitly supplies record=true.',
    expect: [
      'Six-term classical ablation table with W-D-L and Elo per row',
      'Parallel progress across terms in the job summary',
      'games_recorded = 0 in the normal Lab UI; the completed summary reports read-only',
      'Optional games.pgn only when artifact persistence is explicitly requested',
    ],
    tips: [
      'Use Substrate lift test or the UCI gauntlet provider receipt to test whether learned/substrate providers actually affect play.',
      'This is in-process classical Search — not laplace-uci vs Stockfish and not the complete Chess Forward Pass.',
      'Core budget splits across six terms; 0 = all performance cores.',
    ],
    category: 'substrate',
    recordsLive: false,
  },
  {
    kind: 'tactics',
    title: 'Tactics solve rate',
    tagline: 'Classical mate-finding diagnostic — separate from learned tactical-pattern evidence.',
    description:
      'MOVE-SELECTION PARTICIPATION: this diagnostic constructs plain classical Search and checks whether it finds the built-in mate-in-N answers. It does NOT prove the learned fork/pin/skewer outcome provider participated. Learned tactical patterns are a separate substrate leaf plane in substrate-enabled play and are receipted there.',
    expect: [
      'solve_rate metric as a percentage',
      'Per-position table: id, ok/miss, engine move, expected move',
    ],
    tips: [
      'Depth 6+ for harder mates; depth 4 is a quick smoke test.',
      'Does not write to substrate and does not exercise the learned tactical provider — pure classical engine diagnostic.',
    ],
    category: 'diagnostics',
    recordsLive: false,
  },
  {
    kind: 'review',
    title: 'PGN review triage',
    tagline: 'Offline analysis; it does not select Laplace moves.',
    description:
      'MOVE-SELECTION PARTICIPATION: NO. This reads a server-side PGN after games exist and computes centipawn-loss/blunder review data. It can identify candidate failures for later ingestion or inspection, but the review job itself is not a provider in the live Search path.',
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
    tagline: 'Training/input acquisition — affects play only after evidence is folded into a selected provider.',
    description:
      'MOVE-SELECTION PARTICIPATION: INDIRECT. This job streams a player archive and records its novel games. The importer never picks a move. Its data can affect later play only through providers Search actually selects (for example global transitions, learned move/PST evidence, learned tactical outcomes, and future player-conditioned providers).',
    expect: [
      'games_fetched count',
      'games_ingested count',
      'games.pgn artifact with download link',
    ],
    tips: [
      'Leave “Ingest all games” on for the complete available archive; turn it off to apply a cap.',
      'Imported does not mean used: verify later move searches with provider receipts.',
    ],
    category: 'import',
    recordsLive: false,
  },
  {
    kind: 'fide-search',
    title: 'Find FIDE identity',
    tagline: 'Identity lookup only — never a move-selection provider.',
    description:
      'MOVE-SELECTION PARTICIPATION: NO. Searches the official FIDE ratings database and returns FIDE ID, name, title, federation, ratings, and birth year for disambiguation. This lookup does not alter Search.',
    expect: ['Ranked candidate table with official FIDE IDs'],
    tips: ['Search either “Magnus Carlsen” or “Carlsen, Magnus”.'],
    category: 'import',
    recordsLive: false,
  },
  {
    kind: 'fide-profile',
    title: 'Fetch one FIDE profile',
    tagline: 'Profile lookup only: never a move-selection provider.',
    description: 'MOVE-SELECTION PARTICIPATION: NO. Fetches one official FIDE identity and its published ratings. The profile is shown in the run; nothing is written to Laplace.',
    expect: ['Official profile table', 'Standard, rapid, and blitz source ratings where published'],
    tips: ['Use Find FIDE identity first when you do not know the FIDE ID.'],
    category: 'import',
    recordsLive: false,
  },
  {
    kind: 'player-profile',
    title: 'Fetch player profiles',
    tagline: 'Profile lookup only: not a live move selector.',
    description:
      'MOVE-SELECTION PARTICIPATION: NO. Fetches the selected Chess.com or Lichess profile and an optional official FIDE profile. The profiles are shown in the run; nothing is written to Laplace.',
    expect: ['Provider and official profile table', 'Downloadable profile JSON'],
    tips: ['Use Find FIDE identity when several people share the same name.'],
    category: 'import',
    recordsLive: false,
  },
  {
    kind: 'fide-roster',
    title: 'Fetch FIDE top players',
    tagline: 'Cohort lookup only: not part of live Search.',
    description:
      'MOVE-SELECTION PARTICIPATION: NO. Reads an official FIDE ranking cohort and fetches the selected profiles. The cohort is shown in the run; nothing is written to Laplace.',
    expect: ['Official ranked player table', 'Profile acquisition progress'],
    tips: ['Start with 25 to inspect the cohort; the official pages currently publish up to 100 per list.'],
    category: 'import',
    recordsLive: false,
  },
];

const byKind = new Map(LAB_EXPERIMENTS.map((e) => [e.kind, e]));

/**
 * Job records carry the server enum name ("SubstrateTest", "LichessFetch"); the catalog and
 * every start request use the kebab form. Maps either spelling to the kebab form.
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