# 00 — CONTINUITY / READ THIS FIRST

If you are an agent picking up this work after a context reset: read this whole file before
touching anything. It encodes the corrected understanding of the invention, the exact traps that
wasted prior sessions, and the current build state. The point of this file is so the user does
**not** have to re-educate you. Cross-check claims against the code/DB; do not pattern-match.

## The one rule

**Trust but verify. Read the actual code/DB before asserting anything. Never pattern-match;
never state unverified output as fact.** Laplace is the *inverse* of mainstream AI at nearly every
level, so training-data instincts are usually wrong here. Deriving a design from verified mechanism
is good; asserting a high-probability guess as fact is the failure that destroys trust.

## What Laplace actually is

A deterministic, content-addressed geometric substrate for meaning where **AI operations are
database operations** — not an LLM, not nearest-neighbor embeddings.

- Every unit (codepoint→grapheme→word→sentence→document, plus relations, recipes, any modality)
  has a content-addressed 128-bit id (BLAKE3 Merkle) + a deterministic 4D position on the glome
  (S³; codepoints seeded DUCET→Super-Fibonacci) + a lossless mantissa-packed trajectory.
  Angle = identity/category, radius = abstraction (centroids fall inward). Hilbert-4D indexed in
  Postgres/PostGIS.
- Truth = Glicko-2 consensus over credibility-weighted attestations (subject–relation–object;
  source φ curated≈30, crank≈350). "Truths cluster, lies scatter."
- Relation ranks = epistemic solidity (mandate 1.0 > standards_structural 0.91 > taxonomic 0.82 >
  partitive 0.73 > causal 0.64 > equivalence 0.55 > oppositional 0.45 > associative 0.36 >
  tensor_calculation 0.27 > scalar 0.18 > probationary 0.09). NOT IDF/noise weighting.
- Verified equivalences: training = ingestion (Glicko fold = online logistic gradient);
  retrieval = indexed query (Glicko replaces NN); ids/coords compute offline; lossless
  reconstruction (`render_text`). Generation = synthesize a real model file whose weights ARE the
  rated attestations. No GPU, deterministic, CPU/Pi-capable, substrate rebuilds <1hr.
- Consequence: **deterministic provenance for AI** — every weight traces to rated sources;
  single-operator (1-layer-1-head) models are exportable proof artifacts.

Deeper detail: `05-synthesis-layers-heads.md`, `recipe-schema.md`, and the other `docs/invention/*`.

## Traps — do NOT repeat these (each wasted real time)

1. "law" in a filename (`pos_law`/`relation_law`) is NOT a "law layer." They're resolvers. Cute
   names left by prior agents are tripwires, not documentation.
2. The radius/rank-band 4-bucket plane mash (`consensus_layer_plane`) is the **sabotage**, not "the
   layers/heads." Real heads = per-attestation-type operators (`consensus_type_plane`), one
   DISTINCT operator per head. `FillRows` top-k tiling one operator across heads = the dup/repeat bug.
3. A huge valid result ("everything English", millions of rows) is a valid equivalence class, not a
   bug to suppress. Ranks measure solidity (e.g. HAS_LANGUAGE=0.91 standards-grounded).
4. Dev-box query latency is NOT an architectural limit — it's missing indexes / broken WIP queries /
   no SIMD. Indexed lookups are O(log n).
5. Recipes are content-addressed substrate entities (a modality), deposited then read via
   `model_recipes()`/`--recipe-from`. Hand-written JSONs are ONLY dev fixtures simulating the UI POST.
   Export never treats a disk file as the architecture.
6. Heavy math belongs **native** (`engine/synthesis` C++/MKL + SPI), not C# orchestration.
7. No Llama hardcoding. Everything is generic/data-driven — a descriptor, not classes. "Llama" is
   one deposited recipe, not code.

## How to work with the user

Direct, competent, precise. No therapy/emotional/safety-script responses, ever — focus on the code.
Never blame the user. Honest about what is verified vs. assumed. (See the memory `user-anthony`.)

## The build (build-a-bear generic model generator)

Active valet/orchestration plan: `C:\Users\ahart\.claude\plans\jiggly-tumbling-flurry.md`
(orchestrated by `valet_orchestration_campaign_c191b5d2`). Foundry plan
`cosmic-foraging-wall.md` remains the A3/A4 north star — out of scope for the valet campaign.

**Phase 0 done** (committed `a2a3b32`): source-hash-gated perfcache emit, `EnsureComputed`
always computes from raw UCD (never seeds DB from the blob), doc-23 principles aligned.

**Waves 1–5 landed (uncommitted until operator orders commit):**
- Wave 1: CMake DEPENDS + `--scope` + scoped loader
- Wave 2: ISourceManifest / ISeedSource / ISeedScope + family-aware bootstrap
- Wave 3: Unicode MultiPhase; Etl/Model/Chess manifests; empty Unicode/hand-builder allowlists
- Wave 4: IContentRecordAdapter + shared SeedIngestComposition DI (CLI + API)
- Wave 5: HAS_LICENSE/… relations + DepositLicenseAsync; reseed queued in
  `.scratchpad/24_Campaign_Reseed_Queue.md` (**do not reseed until ordered**)

Remaining debt: batched highway reseed (credit relations); Foundry A3/A4; Wave 3D full
hand-list → ISeedSource migration for every RelationTriple source.

Flow: UI posts a recipe JSON → parse + ingest (content-addressed `Model_Recipe`) → export fetches
via `model_recipes()` → native per-head materialize → model file (any arch/format).

Done / verified:
- **A1 ✅** recipe parse+ingest. Files: `app/Laplace.Decomposers.Model/RecipeExtractor.cs`,
  `RecipeDecomposer.cs`; `laplace ingest recipe <json>` wired in `app/Laplace.Cli/IngestCommands.cs`.
  Verified round-trip in `laplace.model_recipes()` (~100ms, operator array intact). Schema
  `docs/invention/recipe-schema.md`; fixture `app/Laplace.Decomposers.Model/dev-recipes/royalty-spine-v1.json`.
  Lesson: trust class must be pre-registered (`UserCuratedResource`).
- **A2 (partial)** `RecipeDescriptor.cs` (typed operator array) written + compiles. Export-branch
  wiring pending, lands with A3/A4.

Next — Foundry A3/A4 (deferred; not valet):
- A3: per-type operators via `consensus_type_plane` (replaces the 4-bucket mash); factor each type /
  metric into attention/OV/FFN factors (`ProjectOperator`/`Factor` → native `ComputeSubstrateGram`/
  `TensorSvdTruncate`).
- A4: extend `substrate_view_t` + `arch_template_materialize_tensor` (`engine/synthesis/src/arch_template.cpp`)
  to fill EACH head from its OWN operator per the descriptor; move C# `Fill*`/COO loops native.
- Export branch: `SynthesizeFromSubstrateAsync` (`app/Laplace.Cli/FoundryCommands.cs`) detects
  `kind=="laplace.recipe"` → descriptor → native materialize → GGUF.
Then A5 (GGUF + 1L1H ablation tests), B (full catalog/structures/formats/coherence), C (thin C# +
determinism/provenance tests).

Validation: build single-operator 1L1H models; each has a predictable signature (IS_A→hypernym
climb, synonym→clusters, trajectory→n-gram, angular→category-mates). Determinism gate: same recipe →
byte-identical file.

Open tech-debt: centralize type/relation/trust-class registration (one manifest → seed SQL bootstrap
+ C# registry + lookups). Deferred & out of scope for now: **model ingest** (decoding trained weights
back into operators) and **in-DB query perf**.

## Build/run notes

- Build C# CLI: `dotnet build app/Laplace.Cli/Laplace.Cli.csproj -c Release`. If MSB3021/3026 file-lock
  errors: a stray `Laplace.Cli` process holds the DLLs — `Get-Process -Name Laplace.Cli | Stop-Process -Force`.
- Run CLI: `scripts/win/cli.cmd <args>` (sets `LAPLACE_DB=Host=localhost;...;Database=laplace` + oneAPI env).
- Native extension build: `scripts/win/build-extensions.cmd laplace_substrate`; deploy `install-extensions.cmd --recycle`.
