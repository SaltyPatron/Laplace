# Open Problems

Audit date: 2026-06-07. Each item is file-grounded and states what the vision requires, what the code does today, and the candidate resolutions.

## 1. Geometry consensus fold — INSTRUMENT-TIER, not core

**Status: reclassified by design decision (2026-06-07): has value, but not for the core invention.** The invention's truth machinery is relational (Glicko over arenas). The fireflies are the AUDIT INSTRUMENT: each witness's placement of the same entity is a distinct, comparable specimen in one Procrustes-aligned frame. The species — not any blend of them — are the product, and evidence rows (`geometry_placement_evidence`) are fully functional today. Audit capabilities this supports directly: per-entity cross-model belief distance (angular), whole-cloud model signatures and lineage/distillation forensics (Hausdorff between clouds), per-layer/head concept flight paths, checkpoint-drift diffs, bias measurement in exact geodesics; Voronoi tessellation of placements into conceptual territories (membership-by-geometry, boundary-proximity as ambiguity, empty cells as visible lexical gaps, cross-model territory comparison, geometric cross-validation of relational taxonomy) — and, because placements are stock PostGIS geometry, standard GIS tooling (e.g. QGIS) can render the jar without custom viewers. Collapsing species into one blended coordinate adds no epistemic strength and can destroy the comparative signal.

**If a derived consensus view is ever wanted** (single default coordinate for fast structural serving; precomputed `dispersion` as a polysemy/contest scalar): the math already exists — `math4d_karcher_mean` with `math4d_log_s3`/`math4d_exp_s3` (engine/core/math4d.c) implements weighted iterative tangent-space Karcher averaging on S³, covered by the engine test suite. Interior entities use the weighted Euclidean mean. Only a thin SQL wrapper/aggregate plus a fold cadence would be needed. Build only if a profiled read path demands it.

## 2. Structural reads pick an arbitrary witness — FIXED 2026-06-10

`structural_neighbors` / `structural_locale` (the two surviving sites after the native migration) now anchor with `ORDER BY s.ord, p.source_id` before `LIMIT 1`. Reads serve a deterministically chosen specimen, not a blended coordinate (per problem 1). Trust-class-aware preference can supersede source-id ordering when problem 7 lands.

## 3. Unlearning cannot reach consensus state

**Vision.** Per-source eviction is real unlearning: a witness's influence is removable on demand.

**Today.** Attestations are provenance-only by design (no magnitudes; no replay fold exists). Glicko accumulation is sequential and non-decomposable: deleting a source's attestations removes provenance but every rating that source ever influenced retains that influence permanently.

**Candidate resolutions.**
- **Replay-from-sources (preferred given measured speed).** Re-fold the world from retained sources minus the evicted one. WordNet folded in 96 s on this machine; full-ladder replay is minutes-scale. Honest, exact, already idempotent.
- **Revocation witnesses.** Counter-testimony at matched weight; epistemically truthful ("retracted" is itself an event) but leaves dispersion widened rather than influence removed.
- **Retained per-source period partials.** True algebraic reversal at meaningful storage cost; contradicts the current "no value channel" law and needs explicit reconciliation if chosen.

## 4. Prompts are not yet witnesses — RESOLVED 2026-06-11 (endpoint path live)

**Vision.** Every prompt is ingested testimony under `substrate/source/UserPrompt/v1` (trust class `UserPromptContent`): conversation history becomes substrate content, addressable at every tier, forever — context as biography, not buffer.

**Resolution.** `TurnWitness` (Laplace.Endpoints.OpenAICompat) deposits every served turn — the user's prompt and the reply — as Document-tier content under the UserPrompt source, off the request path (bounded channel + background apply). Wired into chat (converse + generation branches) and /v1/completions. converse_turns remains the fast session cursor; the substrate holds the durable record. Laws encoded:
- **Replay is not testimony.** Stateless OpenAI clients resend the full history every call; only the final user turn is enqueued, and content the UserPrompt source already witnessed (physicalities probe on the root) is skipped, so replays never double-count games. Genuine cross-session repetition is currently also skipped — strengthening-by-repetition needs explicit per-session bookkeeping if ever wanted.
- **Perfcache is process law.** TextDecomposer hosts must call `CodepointPerfcache.LoadDefault()` (Engine.Core; resolves LAPLACE_PERFCACHE_BIN, falls back to ancestor build trees). The builder otherwise swallows the failure into a silent no-op — that cost a debugging round.
- Floor-gated: no Codepoint entities ⇒ turns are not witnessed (warned once).

Proven live 2026-06-11: unique prompt deposited (prompt+reply roots in laplace.physicalities), identical replay skipped.

## 5. The text→tensor-arena bridge (keystone lemma)

**Vision.** Ingestion is training: text attests into the same arena space a transformer's weights testify into, so `synthesize substrate` can render a no-ancestor model from literature alone.

**Today.** Text-side attestation (`TextEntityBuilder`) writes sequence arenas: `FOLLOWS`, `PRECEDES`, `CO_OCCURS_WITH`, `OCCURS_IN_CONTEXT`, `COMPLETES_TO`. Synthesis renders tensors from the ten tensor-role arenas (`EMBEDS`, `Q_PROJECTS`, …) — see `ConsensusReExport`/synthesis kernels. A pure-text compile today produces empty projection tensors.

**Needs (one of, or a blend).**
- A synthesis-side estimator that derives tensor-role renders from sequence arenas: co-occurrence consensus → Q/K affinity; completion consensus → output projection; tier composition/decomposition → up/down; gating from contextual-selection statistics.
- Or text-side attestation directly into tensor-role types under a defined estimator at ingest.
The mapping must be written down as law before implementation; it is the load-bearing step for the "model trained by reading" claim.

## 6. No first-class document/corpus ingest route

**Today.** CLI ingest dispatch is a fixed source list; `TextDecomposer.Run` is wired into generate/roundtrip paths only.

**Needs.** `ingest text <path>` (file or directory) through IngestRunner with Document source identity, trust class, and idempotent content addressing — the Moby Dick on-ramp.

## 7. Trust policy is code, not data

**Today.** Trust-class → φ/weight numerics live as constants in C# (`WitnessPhi`-style). Trust classes exist as entities but their numeric force is neither queryable nor versioned.

**Needs.** Policy table(s) in the extension (trust_class entity → φ scaling, geometry weight), read by writers at ingest; changes to policy become auditable substrate events.

## 8. Placement consolidation — RULED (2026-06-07): physicalities is the one geometric home

**Ruling.** `physicalities` was designed for this: per-source 4D views with `alignment_residual` + `source_dim` (the LE+GSO+PA outputs) and seeded PROJECTION/PROJECTION_OUTPUT types. Firefly placements are physicalities rows:

- `type` = extended physicality-type vocabulary per tensor role (which tensor the placement was stripped from);
- `source_id` = the **circuit entity** (model+layer+head/expert) rather than the bare model — the same context-as-entity pattern attestations use via `context_id`; per-specimen granularity with zero schema widening; species reassemble by joining circuit→model;
- weight moves to trust policy (§7); re-observation idempotency via the existing `UNIQUE(entity_id, source_id, type)` upsert + `observed_at`.

**Consequences.** `geometry_placement_evidence` and `geometry_consensus` (19_geometry_consensus.sql.in) retire; the optional instrument-tier consensus view (§1), if ever built, derives from physicalities. Structural reads see firefly specimens with zero new read paths; one GIST + one Hilbert key + one table for GIS tooling; source eviction cascades clean.

**Implementation (pending, with next model-ingest work).** Extend the physicality-type registry with per-role types; repoint TokenS3Morph/ModelDecomposer writes; drop 19_* tables; regress pins.

## 9. Modality annexes unwritten

**Vision.** Per-modality segmentation law (the UAX#29 analog for images/audio/video): deterministic, versioned, conformance-tested, perfcache-compiled — after which the entire identity/attestation/consensus machinery applies unchanged.

**Today.** Image/Audio decomposer projects are scaffolds; the relation-type vocabulary (`IS_PIXEL_OF`, `IS_AT_SAMPLE`, `DEPICTS`, `CAPTIONS`, `TRANSCRIBES_AS`) is seeded; no annexes exist.

**Sequencing.** Deliberately after text lock-down, per project direction.

## 10. Frayed-edge surfaces are partial

**Today.** `gaps()` and `epistemic_status()` exist (relational fray: high RD, single-witness, refuted-adjacent). The geometric–relational mismatch query (structurally near, relationally silent → hypothesis candidates) and the dispersion surface (`ORDER BY dispersion` — polysemy/contest detection) await problems 1–2.

## 11. Performance headroom (the slow-tier disclosure)

**2026-06-11 addendum — the fold lane is the proven write ceiling.** The consensus merge fold
measured 25–70k rel/s on a 1.1B-relation behavioral deposit (2 random PK probes per relation,
62 GB working set vs 48 GB RAM, 3–4× re-touch across Glicko periods; sorted probes deployed
live and measured ineffective — the heap is history-random). Full evidence + the PK-less
C bulk-fold design (`prepare/finish_consensus_bulk`): `docs/HANDOFF-fold-lane.md`. Build that
lane before any fleet deposit; everything else on the write path (ETL kernels, COPY staging)
already runs at 1–4M rel/s.

Every 2026-06-07 query number was taken on the slow tier. Levers, each independent, multiplicative, and either built or planned:
- **SPI offload of walks** — the compiled cascade (`astar_path_raw`, generate_tree/greedy) exists in the installed DLLs; new query surfaces (collocate walks, hypothesis scans) should route through C+SPI like it does (10–100× on traversal).
- **Batched kernel entry points** — Fréchet/angular/hilbert calls cross the fmgr boundary per pair; array-in/array-out variants amortize thousands of comparisons per call and unlock SIMD lanes (engine is /arch:AVX2; AVX-512 available on this CPU) (10–50×).
- **Threaded MKL** — extensions currently link sequential static MKL by deployment choice; TBB-threaded path is a relink.
- **The custom PG build** — stock MSVC EDB today; the roadmap icx+LTO PG/PostGIS with Eigen/Spectra in-tree is the whole reason the submodules exist.
- **Prepared/pooled access** — all measurements were cold one-shot psql (parse+plan per query; 5–20× on small queries).
- **SP-GiST trajectory prefix opclass** (planned) — sequence-prefix search on packed IDs.
- **KNN planner tuning** — type-filtered `<<->>` fell off the index path (27 s naive / 5.9 s pooled); needs KNN-first-filter-later shaping or a words-only coord index.
- **GPU phase** — AFTER ingest/export proofing: batch ingest math (LE/Procrustes/cell ETL) only; never the query path. 4060 Ti + 1080 Ti on board; driver pinned for Pascal.
Composite conservative estimate: 2–3 orders of magnitude above RECEIPTS.md numbers before any new algorithm.

## 12. Witnessed stopwords — DIAGNOSED 2026-06-11 (missing witness, not a binding bug)

The idea (ruled good): function-word-ness derives from UD's HAS_POS consensus, not hardcoded lists.

**Diagnosis.** The binding is correct and was never the problem: UD attests HAS_POS on the surface
form's content root (UDDecomposer.cs `NativeAttestation.PosUpos(form, …)`) — the exact identity
`laplace.word_id()` resolves. The lexical sources (WordNet/FrameNet/Wiktionary) attest POS on lemma
content roots, which coincide where surface = lemma. `is_function_word()` returned false because
**UD has zero evidence rows in the working DB** (`evidence_count(NULL, source_id('UDDecomposer'),
NULL) = 0`): UD sits in the deferred lexical-bulk phase (`seed-deferred-lexical.cmd`; ordering law
"… → [deferred] conceptnet → atomic2020 → ud → wiktionary") which has not been run. The 158k
existing HAS_POS consensus rows are content-lemma testimony — closed-class words have no synsets,
so no other witness can cover "the"/DET.

**Remediation.** Run the UD seed (deferred phase) when the cluster is free, then promote
`is_function_word()` as a consensus probe: UPOS ∈ {DET, ADP, AUX, CCONJ, SCONJ, PART, PRON} above a
witness threshold. Nuance: UD attests "The" and "the" as distinct content roots — the probe should
case-fold or accept either form's consensus.

## 13. Document context stamping (text bigrams) — FIXED 2026-06-11 (prospective)

`TextEntityBuilder.BuildDistributionalAttestations` now stamps the containing natural-unit
entity (the document root) as `context_id` on every PRECEDES row — the exact context-as-entity
pattern model witnesses use for layer/head. Consensus identity excludes context, so folds are
unchanged; the attestation-side scope filters (16_inspect `p_context_id`) become meaningful for
text. Unlocks per-author conditioning ("speak as Melville"), per-document provenance, and
witness-restricted generation. Every TryBuildContentWitness caller inherits it (DocumentDecomposer,
db-roundtrip record, TurnWitness conversation turns). Pinned in TextEntityBuilderEmissionTests.

Pre-existing library rows (340,707 PRECEDES) remain `context_id NULL` until their sources are
replayed — replay-from-sources (§3) is the lawful backfill; do not UPDATE rows in place
(attestation identity includes context, so a stamp is a new row, not a mutation).

## 14. 23_structural_surface module — registration + pins

`word_curve` / `word_shape_distance` / `anagrams_of` / `collocates` were proven live (RECEIPTS.md) and added as `extension/laplace_substrate/sql/23_structural_surface.sql.in` (registered in the entry script + CMake module list). Needs: pg_regress pins (incl. the whale anagram set and the Fréchet ordering whale~while < whale~whole < whale~ship), and a `realized_trajectory(entity)` generalization.

## 15. ingest-text sidecar rebuild guard — FIXED 2026-06-10

`scripts\win\cli-sidecar.cmd` (shared by ingest-text/ingest-repo) builds only when the sidecar is missing or any `app\` source is newer than the sidecar dll — both the per-invocation rebuild overhead and the stale-sidecar-after-C#-changes gotcha are gone.

---

### Non-problems recorded to prevent re-litigation

- **Trajectory stores identity, not coordinates — by law.** Consensus coordinates move (problem 1 makes this routine); identity is the only stable cargo. Mantissa-packed IDs give O(1) exact constituents, placement-proof sequences, and the SP-GiST prefix-match index target. T0 constituents are additionally self-describing inline via the flags word (`vertex_atom`). Coordinate-payload trajectories were considered and rejected: they rot on re-placement and require per-vertex spatial joins for identity at tier ≥ 1.
- **Windows is the working platform; Linux scripts may go stale** until the custom Intel-toolchain PG/PostGIS build resumes. GH Actions runner is disabled by intent.
