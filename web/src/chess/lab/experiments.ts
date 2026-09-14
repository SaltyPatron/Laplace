export type LabCategory = 'substrate' | 'diagnostics' | 'import';

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
    id: 'import',
    label: 'Import profiles & games',
    blurb: 'Find and import FIDE, Chess.com, and Lichess identities; games are optional.',
  },
];

export const LAB_EXPERIMENTS: LabExperiment[] = [
  {
    kind: 'substrate-test',
    title: 'Substrate lift test',
    tagline: 'Real move-selection test: does witnessed substrate experience beat the control?',
    description:
      'MOVE-SELECTION PARTICIPATION: YES in transition mode. The Laplace side runs Search with witnessed root transition/move evidence, substrate leaf evaluation, learned PST/tactical residuals when populated, and exact Syzygy closure; the Off side is the conventional control. '
      + 'LEARNING: YES. Completed games are witnessed back to substrate, so later games can consume newly folded state. The emitted provider metrics are the proof of actual reads/contributions — provider availability alone is not counted as use.',
    expect: [
      'Live W-D-L score and Elo difference in the feed',
      'Final results table with Elo ± margin',
      'games_recorded metric — every game is witnessed to substrate during the run',
      'provider-use metrics: root/transition reads, non-zero child-state contributions, learned/tactical coverage where present, Syzygy coverage, and substrate epoch',
      'games.pgn artifact for archival',
    ],
    tips: [
      'Transition mode is the actual substrate-enabled playing path; Off is the conventional sanity control.',
      'A loaded provider with zero reads/non-zero contributions did not affect that search. Use the receipt metrics, not the label.',
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
      + 'LEARNING: the optional recorded games become substrate evidence, but that is a recording side effect; it does not make this ablation a learned-policy test.',
    expect: [
      'Six-term classical ablation table with W-D-L and Elo per row',
      'Parallel progress across terms in the job summary',
      'Recorded games can extend the corpus, but provider participation is intentionally absent from these matches',
      'games.pgn combining every term\'s games',
    ],
    tips: [
      'Use Substrate lift test or the UCI gauntlet provider receipt to test whether learned/substrate providers actually affect play.',
      'This is in-process classical Search — not laplace-uci vs Stockfish and not the complete Chess Forward Pass.',
      'Core budget splits across six terms; 0 = all performance cores.',
    ],
    category: 'substrate',
    recordsLive: true,
  },
  {
    kind: 'learned-pst',
    title: 'Learned PST grid',
    tagline: 'Provider inspection: the same learned residual consumed by substrate-enabled Search.',
    description:
      'MOVE-SELECTION PARTICIPATION: YES when the learned table has non-zero cells and substrate play is enabled. This job itself is read-only: it displays the data-driven piece-square residual folded from move OUTCOME consensus. Search consumes that residual as a distinct leaf-evaluation plane; the UCI provider receipt reports actual reads and non-zero contributions.',
    expect: [
      'Table of top squares by deviation for each piece type',
      'Coverage percentage per piece',
      'This view performs no training; it inspects a provider that the playing path consumes separately',
    ],
    tips: [
      'A populated grid proves learned state exists; only search receipts prove that a particular move search actually consumed and was changed by it.',
      'Positive deviation = good for the side to move from that square.',
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
      'MOVE-SELECTION PARTICIPATION: INDIRECT. This job streams a player archive, records/analyzes novel games, attributes them to the provider username, and imports identity/profile links. The importer never picks a move. Its data can affect later play only through providers Search actually selects (for example global transitions, learned move/PST evidence, learned tactical outcomes, and future player-conditioned providers).',
    expect: [
      'games_fetched count',
      'games_ingested and profiles_ingested counts',
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
      'MOVE-SELECTION PARTICIPATION: NO. Searches the official FIDE ratings database and returns FIDE ID, name, title, federation, ratings, and birth year for disambiguation. Selecting/importing an identity can support future player-conditioned evidence, but this lookup does not alter Search.',
    expect: ['Ranked candidate table with official FIDE IDs', 'One-click profile import without downloading games'],
    tips: ['Search either “Magnus Carlsen” or “Carlsen, Magnus”, then import the selected official profile.'],
    category: 'import',
    recordsLive: false,
  },
  {
    kind: 'fide-profile',
    title: 'Import one FIDE profile',
    tagline: 'Profile evidence input — not automatically a playing-policy input.',
    description: 'MOVE-SELECTION PARTICIPATION: INDIRECT. Loads one official identity and published rating planes into Laplace. The profile is durable evidence, but it affects move choice only when a selected player/opponent-conditioned provider consumes it; import success alone is not proof of use.',
    expect: ['One durable Chess_Player profile', 'Standard, rapid, and blitz source ratings where published'],
    tips: ['Usually use the Import profile button on a FIDE search result.'],
    category: 'import',
    recordsLive: true,
  },
  {
    kind: 'player-profile',
    title: 'Associate player identities',
    tagline: 'Identity/provenance input — not a live move selector.',
    description:
      'MOVE-SELECTION PARTICIPATION: INDIRECT. Fetches the selected Chess.com or Lichess profile and an optional official FIDE profile, then writes metadata and the explicit identity link. This makes player-conditioned evidence possible; it does not itself change candidate scores.',
    expect: ['Provider and official profile table', 'Downloadable profile JSON', 'identity_links receipt or FIDE candidates'],
    tips: ['Use Find FIDE identity when several people share the same name; only an explicitly selected FIDE ID is associated.'],
    category: 'import',
    recordsLive: false,
  },
  {
    kind: 'fide-roster',
    title: 'Ingest FIDE top players',
    tagline: 'Cohort/profile input — not automatically part of live Search.',
    description:
      'MOVE-SELECTION PARTICIPATION: INDIRECT. Reads an official FIDE ranking cohort, fetches selected profiles, and writes them as substrate evidence. The job expands player/rating knowledge; move selection changes only when a selected context provider actually consumes that evidence.',
    expect: ['Official ranked player table', 'Profile acquisition progress', 'profiles_ingested receipt'],
    tips: ['Start with 25 to inspect the cohort; the official pages currently publish up to 100 per list.'],
    category: 'import',
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