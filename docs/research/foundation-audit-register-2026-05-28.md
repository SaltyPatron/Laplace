# Foundation Audit Correction Register — 2026-05-28

Anchor: [`docs/SUBSTRATE-FOUNDATION.md`](../SUBSTRATE-FOUNDATION.md) — the ratified conceptual core. Where any doc, ADR, issue, or comment contradicted the anchor's ten truths or its OPEN-QUESTIONS list, this audit corrected the prose and replaced false certainty with explicit OPEN markers.

This is the reviewable record of the whole audit: what was corrected, what was left intact and why, what remains OPEN, and which GitHub issues were flagged.

## The ten ratified truths (reference)

The recurring corruption families found across the corpus map to these anchor truths:

- **Truth 1** — Model ingestion is a streaming O(params) ETL of weight cells; **GEMM-at-ingest (`E·W·Wᵀ·Eᵀ` over vocab²) is the disease, not a tuning knob**; flat top-k that discards most of the model is forbidden.
- **Truth 2** — A weight cell is a Glicko-2 **matchup outcome** (weight = outcome, source-trust = opponent strength); store only the emergent consensus rating, never the weight as a rating.
- **Truth 3** — The S³ glome **IS the canonical shared embedding frame**; geometry carries meaning. Forbidden framing: "geometry is just an index" / "physicalities aren't knowledge" / orthogonal-per-model axes. Retrieval is **NOT** nearest-neighbor — geometry seeds candidates; Glicko-2 effective-μ across typed arenas decides what pulls back.
- **Truth 4** — Inference is indexed A* over the attestation DAG ranked by Glicko-2 effective-μ; not distance, not recursive CTE/RBAR.
- **Truth 5** — Trust is a **self-tuning Glicko-2 value** emergent from cross-source agreement; **never a tier or fixed class**. The word **"tier" is reserved exclusively for the Merkle stratum** (T0 = Unicode codepoints).
- **Truth 6** — **Bit-perfect / round-trip / source-faithful preservation is worthless**; ingest dissolves to facts and discards the blob; behavioral fidelity only. Seed sources are OPTIONAL enrichment; semantic model ingest is the mandatory spine.
- **Truth 8** — A recipe is a **fillable mold**; synthesis pours/materializes consensus facts into the mold's tokens — not a copy, weight-average, or bolting-together of parts.
- **Truth 10** — Cute names are a tell, not a concept. **"codec" is banned** (implies round-trip preservation), as are "Vampire mode" / "Food principle" / "Zero Calories" used as load-bearing concepts.

### OPEN questions (anchor)

1. **Interior d×d tensor-axis (q/k/v/o/gate/up/down) → token-entity resolution WITHOUT re-running the GEMM** — genuinely unsolved. `embed_tokens` / `lm_head` are directly token-anchored; the interior tensors are not.
2. **Exact arena/kind assignment per interior tensor role.**
3. **The synthesis "pour facts into the mold" distribution algorithm at frontier scale.**

These three must be pinned with Anthony. The audit marked them OPEN wherever a doc asserted them as settled; it did **not** invent substitute answers.

---

## 1. Summary counts

| Metric | Count |
|---|---|
| Documents corrected | 49 |
| — of which user-authored core docs (CLAUDE/RULES/STANDARDS/DESIGN/GLOSSARY/README/OPERATIONS/CONTRIBUTING/AGENTS/FLOWS) | 11 |
| — of which agent definitions (`.claude/agents/`) | 7 |
| — of which ADRs (`docs/adr/`) | 28 |
| — of which research docs (`docs/research/`) | 4 |
| — of which GitHub templates (`.github/`) | 2 |
| — of which ADR index (`docs/adr/README.md`) | 1 |
| Issues reopened | 0 |
| Issues flagged (non-destructive audit comment, body left intact) | 7 |

### Corruption-family tally (across all corrected docs)

| Family | Anchor truth | Approx. occurrences |
|---|---|---|
| "codec" / round-trip / bit-perfect / lossless-as-goal | 6, 10 | ~30 docs |
| TrustClass / trust-tier / kind-value-tier ladder | 5 | ~20 docs |
| Interior d×d tensor → token resolution asserted as settled | OPEN #1 | ~18 docs |
| GEMM-at-ingest / vocab² bilinear / flat top-k | 1 | ~10 docs |
| Geometry-as-mere-index / physicalities-aren't-knowledge / NN retrieval | 3 | ~12 docs |
| Cute names ("Vampire mode" / "Food principle") as concepts | 10 | ~7 docs |
| Misuse of "tier" outside the Merkle stratum | 5 | ~9 docs |
| Lottery-ticket / magnitude-pruning reflex | 1, 2 | ~6 docs |
| Synthesis-as-settled / pour-facts algorithm presumed | OPEN #3 | ~9 docs |

---

## 2. Documents corrected

Each entry lists the corruptions removed and the OPEN flags now carried by the doc. Paths are relative to repo root unless absolute.

### Agent definitions (`.claude/agents/`)

#### `.claude/agents/conventional-ai-skeptic.md`
**Corruptions corrected:**
- "Model ingest is a codec" — banned cute-name label (truth 10); the round-trip/missingness framing also conflicted with truth 1 (streaming O(params) ETL) and truth 6 (bit-perfect storage worthless).
- GEMM section only caught runtime/hot-path GEMM; it omitted the anchor's primary forbidden case — **GEMM-at-ingest** (`E·W·Wᵀ·Eᵀ` vocab² recompute) per truth 1, the exact disease the skeptic must catch.
- Vector-DB answer framed S³/Hilbert/GIST purely as "projection/access layers," flirting with the banned "geometry is just an index" framing (truth 3).

**OPEN flags:** None added. No interior-tensor false-certainty claim present. The flat-threshold section ("lottery-ticket-aware multi-pass: per-tensor relative top-k + per-row top-k + probe-validated retention") was left intact — the anchor forbids only a *flat* top-k that discards most of the model, not relative/probe-validated retention.

#### `.claude/agents/cpp-performance.md`
**Corruptions corrected:**
- Hard rule 5 pointed at stale, non-existent header path `engine/include/laplace/*.h`; actual layout is per-library `engine/{core,dynamics,synthesis}/include/laplace/<lib>/*.h`.
- Hard rule 9 used the bare word "tier" ("tier transitions") in the cascade loop — truth 5 reserves "tier" for the Merkle stratum. Also stated "effective-mu ordering" without grounding it as Glicko-2-across-arenas vs distance.
- Procrustes/Laplacian/Gram-Schmidt section described aligning an ingested model onto the frame without guarding the OPEN interior d×d → token resolution.

**OPEN flags:** Interior d×d tensor axis → token-entity resolution explicitly marked OPEN in the Procrustes pipeline section, citing the anchor.

#### `.claude/agents/ingestion-pipeline.md`
**Corruptions corrected:**
- Hard rule 10 framed model ingest as a "codec" with "source-scoped round-trip" fidelity and "accepted loss" debugging — contradicts truths 1/6/10.
- "trust class" used as a fixed classifier in the source-order paragraph, hard rule 9, and the ADR 0044 reference — contradicts truth 5.
- Model sources described purely as "probe-based (run model → observe → lottery-ticket filter)" with no streaming weight-cell ETL — contradicts truth 1 (probe-recompute risks GEMM-at-ingest).
- "Model-codec fidelity" section asserted full model capture via probe observations + "codec instance" round-trip with no acknowledgment that interior d×d → token resolution is unsolved.

**OPEN flags:** Interior d×d → token resolution marked OPEN in the model-source bullet and fidelity section. Exact arena/kind per interior tensor role marked OPEN. Note: ADR 0044 itself likely still carries the corrupt ladder framing (out of scope to edit here).

#### `.claude/agents/postgres-extension.md`
**Corruptions corrected:**
- `<<->>` centroid distance listed bare as "for KNN" — risks pattern-matching to nearest-neighbor retrieval, forbidden by truth 3.
- Required-reading list omitted `docs/SUBSTRATE-FOUNDATION.md` — the ratified core an agent owning the compiled cascade / A* traversal (truth 4) must read first.

**OPEN flags:** None.

#### `.claude/agents/substrate-architect.md`
**Corruptions corrected:**
- Opening line + "Geometric projection/access layer" bullet asserted the forbidden orthogonal-axes framing ("physicalities are projection/access lenses; typed attestations are the knowledge layer"; "it is not the semantic decision layer") — contradicts truth 3.
- "Physicalities" bullet ("proximity … does not define truth or semantic nearest neighbor") diminished geometry-carries-meaning — corrected to geometry-carries-meaning + Glicko-2 effective-μ adjudicates seeded candidates.
- "Source trust" bullet enumerated "distinct source classes" — a fixed TrustClass ladder (truth 5).
- Document was silent on model ingestion, leaving "Substrate Synthesis" exposed to codec rot — added the streaming O(params) ETL / matchup-outcome framing and the forbidden GEMM-at-ingest / vocab² / top-k / bit-perfect / "codec" items.

**OPEN flags:** Interior d×d → token resolution marked OPEN/UNSOLVED in a new section. Exact arena/kind per interior tensor role OPEN. Synthesis "pour facts into the mold" at frontier scale OPEN.

#### `.claude/agents/type-taxonomy.md`
**Corruptions corrected:**
- Transformer-family kinds (EMBEDS, Q/K/V/O_PROJECTS, GATES, UP/DOWN_PROJECTS, NORMALIZES, OUTPUT_PROJECTS) named mechanical roles but silently presumed interior tensor-cell → token-entity resolution as settled.
- HAS_SOURCE_TRUST_POLICY described "source classes admitted, preferred, discounted, or isolated" — a fixed trust-class ladder (truth 5).
- Required-reading list omitted the anchor.

**OPEN flags:** Interior d×d → token resolution for Q/K/V/O/gate/up/down/norm marked OPEN; per-interior-tensor-role arena/kind flagged OPEN. Both must be pinned before any curatorial kind design commits.

#### `.claude/agents/verification.md`
**Corruptions corrected:**
- "Lottery-ticket-aware sparsity validation" framed model ingest as per-tensor relative top-k / per-row top-k filter — contradicts truth 1 (flat top-k discarding most of the model is the disease).
- Headline round-trip milestone asserted "the source-model codec works" — "codec" banned (truth 10); weights are dissolved to rated attestations and the blob discarded.
- Sparsity section asserted a settled multi-pass lottery-ticket retention mechanism without distinguishing token-anchored embed_tokens/lm_head from interior d×d tensors.

**OPEN flags:** Interior d×d cell → token-entity resolution and the corresponding retention/significance criterion marked OPEN.

### GitHub templates (`.github/`)

#### `.github/PULL_REQUEST_TEMPLATE.md`
**Corruptions corrected:**
- Line 32 "model ingest as codec" — "codec" banned (truth 10); reframed around the ratified streaming O(params) ETL (truths 1, 2).
- Model-ingest checklist contained no OPEN-question marker — could be ticked for interior-tensor work without flagging d×d → token resolution as unsolved.

**OPEN flags:** Interior d×d → token resolution explicitly marked OPEN in the template.

#### `.github/copilot-instructions.md`
**Corruptions corrected:**
- Line 11 "AI model ingest is a codec" — banned; omitted streaming O(params) ETL of weight cells as Glicko-2 matchups (truth 1) and that bit-perfect is worthless (truth 6).
- Line 15 "Physicalities are projection/access lenses, not the knowledge layer" — contradicts truth 3; failed to state the S³ glome IS the canonical embedding frame and consensus moat.
- Line 15 "semantic nearest-neighbor behavior is arena-conditioned … not spatial closeness" — understated; replaced with explicit "retrieval is NOT nearest-neighbor; geometry seeds candidates, Glicko-2 effective-μ across typed arenas decides."

**OPEN flags:** None.

### User-authored core docs

> NOTE: CLAUDE.md hard rule 3 lists DESIGN/GLOSSARY/RULES/STANDARDS/OPERATIONS/README as user-authored documentation normally requiring explicit authorization. The audit task directed editing them in place to mirror the documented 2026-05-28 substrate-foundation grounding; the rule-3 tension is flagged for the user's awareness. Direction is consistent with prior user corrections.

#### `AGENTS.md`
**Corruptions corrected:**
- Line 22 "AI model ingest is a codec" — banned (truth 10); framed ingest as round-trip, contradicting truths 1, 2, 6.
- Line 19 "Physicalities are projection/access lenses, not the knowledge layer" — forbidden framing (truth 3).

**OPEN flags:** Interior d×d → token resolution marked OPEN in the model-ingest bullet; only embed_tokens/lm_head noted as directly token-anchored.

#### `CLAUDE.md`
**Corruptions corrected:**
- Hard rule 9 "Model ingest is a codec" + "one faithful source-model round-trip … is decisive" — contradicts truths 1, 6, 10.
- "What Laplace IS" geometry framing "The S³ / 4-ball layer is an embedding-like projection/access layer, not the knowledge layer" — contradicts truth 3.
- Model-ingestion description in the IS paragraph was silent on the streaming-ETL/matchup-observation mechanism and the no-GEMM-at-ingest / no-bit-perfect prohibitions (truths 1, 2, 6).

**OPEN flags:** Interior d×d → token resolution OPEN; flagged in rule 9. Note: ADR 0037 is titled "…model-codec-fidelity" — still carries corrupt framing, a separate audit target. Memory files (`project_laplace_commercial_strategy` etc.) still use "codec"/"build-a-bear" — out of scope, contradict truth 10.

#### `CONTRIBUTING.md`
**Corruptions corrected:**
- Architectural invariant 8 asserted "AI model ingest is a codec" and "Source-scoped round-trip fidelity is a verification target, not optional behavior" — contradicts truths 1, 2, 6, 10.

**OPEN flags:** Inline marker added in invariant 8: interior d×d cells → token-entity resolution without re-running the GEMM is OPEN.

#### `DESIGN.md`
**Corruptions corrected:**
- §VII lottery-ticket block prescribed "top-k on the bilinear `E·W·Wᵀ·Eᵀ` token×token scores" — the exact GEMM-at-ingest / vocab² disease (truth 1).
- §VII Model-codec fidelity note asserted the OPEN interior-tensor question as settled ("Every interior tensor … maps to token↔token attestations of its kind, uniformly — there are no hidden-dim entities").
- §II trust-class ladder (foundational constants → … → AI-model static-ETL observations → prompt-local) — forbidden by truth 5.
- §II effective-score inputs listed "trust class" as an input — same corruption.
- §I + §I.A geometry-as-mere-index framing ("They are not the knowledge layer"; "value-additive enrichment, not the engine"; "semantically separated from knowledge") — forbidden by truth 3.
- Cute-name "codec" usage ("Model-codec fidelity" heading, "codec bug", "first codec target") — truth 10.

**OPEN flags:** Interior d×d → token resolution marked OPEN in §VII (×2) and the ingest bullet list (embed_tokens/lm_head remain token-anchored). Exact arena/kind per interior tensor role OPEN (the Q_PROJECTS/…/NORMALIZES list now labeled "candidate"). Verified plumbing left intact: 3 core tables exist in `extension/laplace_substrate/sql/{02_entities,03_physicalities,04_attestations}.sql.in`; referenced research file exists; ADR filename links left unchanged; §XII serde verification and §XI "Qwen3 round-trip" left intact (round-trip = filling the source's own mold, truth 6).

#### `FLOWS.md`
**Corruptions corrected:**
- "Model codec ingest" (FLOW-INGEST-MODEL-001 name + steps + flow index + layer-10 table + ADR 0044 trace row) — "codec" banned (truth 10); omitted the streaming O(params) weight-table ETL → Glicko-2 matchup-observation framing (truths 1-2).
- Kind-value "tiers" T1..T11 + HAS_KIND_VALUE_TIER (FLOW-BOOT-001 stage 3) — truth 5. Verified as real bootstrap rows in `10_bootstrap.sql.in` L191-214.
- TrustClass_* ladder of 10 named classes (FLOW-BOOT-001 stage 3.5, FLOW-INGEST-PROMPT-001 step 6, FLOW-INGEST-MODEL-001 step 5, ADR 0044 trace) — truth 5. Verified as real rows in `10_bootstrap.sql.in` L166-175.
- FLOW-SYNTH-001 lacked the "recipe is a fillable mold" framing (truths 6, 8).

**OPEN flags:** Interior d×d → token resolution OPEN (embed_tokens/lm_head token-anchored). Exact arena/kind per interior tensor role OPEN. Synthesis "pour facts into the mold" at frontier scale OPEN.

#### `GLOSSARY.md`
**Corruptions corrected:**
- Source Trust Class: a 10-tier `TrustClass_*` hierarchy with hard-coded effective-μ multipliers / arena-admittance bands (~L136-153) — direct contradiction of truth 5.
- Per-decomposer "Trust class: X" field across all 11 decomposers + App data + ModelDecomposer.
- "value tier" in Attestation Kind (L85) — misuse of "tier" (truth 5).
- Emergent Sparsity asserted interior token-pair selection as settled via top-k on the bilinear `E·W·Wᵀ·Eᵀ` — forbidden GEMM-at-ingest (truth 1) AND states an OPEN question as solved.
- Attestation Kind (L95) "every interior tensor maps to token↔token attestations uniformly" — OPEN question as solved.
- Substrate Synthesis (input #2/#3) presented interior-tensor population + frontier-scale distribution as settled — both OPEN.
- "Codec" label as section header and in "AI⇄DB codec" / "Model-Codec Fidelity" — truth 10.
- "lottery-ticket" framing in Model-Codec Fidelity, `liblaplace_dynamics.so`, ModelDecomposer — truths 1/6.
- Glome section ("geometric layer is value-additive enrichment, not load-bearing … strip S³ and the substrate still functions … not where meaning lives") — contradicts truth 3.
- Physicality section ("a projection/access layer, not belief or knowledge") — truth 3.

**OPEN flags:** How a NEW source acquires its initial Glicko-2 credibility, and how prompt-local vs. adversarial content is admitted/excluded WITHOUT a fixed TrustClass band — OPEN, marked in "Source Trust." Interior d×d → token resolution OPEN, marked in Attestation Kind, Emergent Sparsity, ModelDecomposer, Substrate Synthesis (recorded that the `E·W·Wᵀ·Eᵀ` vocab² bilinear is FORBIDDEN, not the answer). Synthesis "pour facts into the mold" at frontier scale OPEN. NOTE: rule-3 tension flagged. The fixed `TrustClass_AIModelProbe` entity name still appears in ADR 0044 / older docs and possibly in shipped code/seed; GLOSSARY now flags it as a retired-ladder rung but the substrate-entity-name migration is unresolved.

#### `OPERATIONS.md`
**Corruptions corrected:**
- Line 168 (`just roundtrip`): "codec"-adjacent "Ingest model → synthesize" with no mechanism, plus retired "Chunk 8 milestone" reference (ADR 0060).
- Ingest procedure step 4 framed model ingest only as "Procrustes alignment → physicalities", omitting the ratified streaming O(params) weight-cell ETL emitting Glicko-2 matchup observations, store-consensus-not-weight, and the vocab²/GEMM/top-k forbidden list (truths 1+2).
- Round-trip section: "Chunk 8" reference and twice-used banned "codec" ("model-ingest codec"); round-trip not distinguished from banned bit-perfect preservation.
- Consensus-divergence sentence implied a "synthesis writer" over weight space without naming cross-source Glicko-2 adjudication vs weight-average.
- Lines 310/314: "chunk progress" / "chunk/story progress" referencing the retired chunk sequence (ADR 0060).

**OPEN flags:** Interior d×d cells → token-entity resolution OPEN, inline in ingest step 4. Arena/kind per interior tensor role OPEN. `recipes/` directory documented in FS-layout does not yet exist on disk; left intact as forward-looking plumbing (not fabricated as present). Lines 232/234 "tier transitions" / "tiered entity DAG" left intact — permitted Merkle-stratum sense of "tier" (truth 5).

#### `README.md`
**Corruptions corrected:**
- L19/45 "The S³ / 4-ball layer is an embedding-like access layer, not the knowledge layer" — contradicts truth 3.
- L19 "compiled cascading-tier A*" misused "tier" — truth 5. Inference is indexed A*.
- L23/64 "codec" framing and "A source-scoped round-trip proves the codec" — truths 10 and 6. Round-trip is a behavioral proof, not a preservation guarantee.
- L64 omitted that semantic model ingest is the mandatory spine and seed sources are OPTIONAL enrichment (truth 6); did not state ingestion is streaming O(params) ETL with blob discarded / no GEMM recompute / no bit-perfect (truths 1, 2, 6).
- L23 Synthesis described without "fillable mold" / pour-facts framing (truths 6/8).
- L188 "just status # current chunk + open blockers" — chunk cadence retired by ADR 0060.

**OPEN flags:** Interior d×d → token resolution remains OPEN; README does not assert it as settled, so no false-certainty edit was needed — flagged that any future model-ingest detail here must carry the OPEN marker.

#### `RULES.md`
**Corruptions corrected:**
- R3 (L79) asserted interior tensor-cell → token-pair selection as settled via the `E·W·Wᵀ·Eᵀ` bilinear over vocab² "bounded for tractability" — exactly the GEMM-at-ingest/vocab² mechanism forbidden by truth 1; the resolution is an OPEN question.
- R2 (L68) listed "trust class" as a stored source property — contradicts truth 5.
- R20 (L289) defined an explicit source-trust-tier ladder — forbidden by truth 5.
- R21 (L297-303) framed model ingestion as "a codec" with "model-codec fidelity" and "codec bug" — truths 10 and 6.
- R21 (L301) listed "lottery-ticket sparse load-bearing structure" as a recorded ingest channel — magnitude-pruning reflex; truths 1/2.
- R21 (L299) implied the full seed stack is a needed training corpus — truth 6 makes seed-source attestations OPTIONAL enrichment.
- R1 (L60) framed the S³/physicality layer as "not the knowledge layer" — truth 3.
- R16 (L235) listed "codec" as legitimate C/C++ engine work — truth 10.

**OPEN flags:** Interior d×d cell → token-entity resolution marked OPEN in R3 L79 (embed_tokens/lm_head token-anchored; q/k/v/o/gate/up/down resolution without re-running GEMM unsolved). ADR 0037's filename retains "model-codec-fidelity" (real file path; renaming is a separate user-authorized action). The R3 L81 FORBIDDEN block already rejects lottery-ticket/magnitude pruning — left intact.

#### `STANDARDS.md`
**Corruptions corrected:**
- "Kind value tiers + Glicko-2 priors (ADR 0044)" defined an 11-rung T1–T11 "value tier" ladder with fixed cascade-weight multipliers and a HAS_KIND_VALUE_TIER meta-attestation — fixed-class ladder + illegitimate "tier" use (truth 5).
- "Source trust class discipline (ADR 0044)" mandated HAS_TRUST_CLASS pointing at one of 10 bootstrapped trust-class entities with fixed per-class weights (truth 5).
- The former "T9 Tensor-Calculation" row asserted a confident rating/weight (≈1400, 0.4×) and arena/kind treatment for interior tensor roles — false certainty over an OPEN question.

**OPEN flags:** Exact Glicko-2 prior values per attestation kind OPEN. Cascade weighting of kind-typed walks OPEN. Arena/kind assignment per interior tensor role OPEN.

### ADRs (`docs/adr/`)

#### `0001-extend-postgis-via-z-plus-m.md`
- **Corruptions:** Stale plumbing — indexing "via PostGIS's existing `gist_geometry_ops_nd`" was superseded by ADR 0029 (custom `laplace_gist_s3_ops` on the same standard geometry type). Verified against `05_indexes.sql.in` and `07_s3_opclass.sql.in`. Latent lean toward "geometry is just an index" (truth 3); added note that the GIST step only seeds candidates while Glicko-2 effective-μ pulls back.
- **OPEN flags:** None.

#### `0002-three-tables-no-event-log.md`
- **Corruptions:** Decision section listed "trust class" as a Glicko-2 dynamics input — corrupt TrustClass-ladder framing (truth 5).
- **OPEN flags:** None.

#### `0005-hilbert-over-hyperbox.md`
- **Corruptions:** Used "tier" for the Merkle composition ladder ("entities at higher tiers") — changed to "higher Merkle strata" (truth 5). Framed the Hilbert curve as plain "1D locality-preserving indexing" with no caveat, leaving room to read it as endorsing NN retrieval (truths 3, 4); added an access-vs-retrieval clarification.
- **OPEN flags:** None.

#### `0007-lottery-ticket-aware-sparsity.md`
- **Corruptions:** Title + Decision rested on "lottery-ticket magnitude pruning" — the conventional NN pruning reflex; forbidden by truths 1-2 + RULES R3 + ADR 0056. Step 1 "per-tensor relative top-k%" and step 2 "per-row top-k" are magnitude filters. Context cited "+ activation patterns" — requires a forward pass / GEMM-at-ingest (truth 1). Step 3 "probe-validated retention" = behavior-preservation framing (truth 6) + requires executing the model at ingest (truth 1). Interior-tensor handling silently presumed a solved d×d cell→token resolution.
- **OPEN flags:** Interior d×d → token resolution OPEN; arena/kind per role OPEN; synthesis at frontier scale OPEN. Note: RULES R3 has materially superseded this ADR's original 2026-05-21 decision; reconciled to R3/ADR 0056.

#### `0008-sparse-by-construction-emission.md`
- **Corruptions:** Context framed "materializing a target tensor at a position" as if interior tensor-cell → entity/position mapping were settled (OPEN-Q #1, #3). Consequences asserted a WordNet-only substrate as a synthesis path (truth 6 — seed sources are OPTIONAL enrichment, model ingest is the spine).
- **OPEN flags:** Interior d×d → token resolution OPEN; synthesis at frontier scale OPEN.

#### `0009-recipe-extraction-and-overrides.md`
- **Corruptions:** Context "round-trip emission" vs "custom emission" without the fillable-mold concept (truth 6). Decision "Default round-trip" / "Custom variant" implied blob preservation. Consequences "Round-trip is the default mode — proves the codec" — "codec" banned (truth 10), bit-perfect worthless (truth 6). Silent on OPEN status of interior d×d materialization.
- **OPEN flags:** Synthesis at frontier scale OPEN; interior d×d → token resolution OPEN.

#### `0010-substrate-synthesis-naming.md`
- **Corruptions:** "assembling from parts" undersells truths 6+8 (recipe is a fillable mold). Plumbing: cited CLI verb "laplace-cli synthesize"; actual is "synthesize substrate <recipe.json>" / "synthesize passthrough <model-dir>" (verified in `app/Laplace.Cli/Program.cs` and Justfile). Let "Substrate Synthesis" stand as settled without flagging the OPEN algorithm.
- **OPEN flags:** Synthesis "pour facts into the mold" at frontier scale OPEN.

#### `0011-polymorphic-plugin-architecture.md`
- **Corruptions:** IFeatureExtractor glossed as "adding a new embedding dimension feature" — treats the embedding space as orthogonal per-model feature axes (truth 3).
- **OPEN flags:** None.

#### `0012-mantissa-packing-format.md`
- **Corruptions:** Context framed the trajectory walk as a path that "emits the original canonical bytes" — bit-perfect/round-trip reconstruction (truth 6). Conflated model ingestion into the content-trajectory reconstruction path citing model BPE-token CONTENT physicalities (truths 1-2). Determinism bullet "Round-trip is byte-identical across runs and machines" un-scoped.
- **OPEN flags:** None.

#### `0013-two-tier-cicd.md`
- **Corruptions:** Title/body used "tier" ("Two-tier CI/CD") — truth 5 reserves "tier" for the Merkle stratum; this concerns CI/CD pipelines. Stale plumbing: cited "RULES.md R8" as the runner-security rule, but R8 is now "No GPU at runtime or ingest"; the live CI-governance rule is R23.
- **OPEN flags:** None.

#### `0023-extension-owns-schema-dbup-orchestrates.md`
- **Corruptions:** L33 "cascade-tier functions" — "tier" reserved for the Merkle stratum (truth 5).
- **OPEN flags:** None (no OPEN-question content).

#### `0026-csharp-project-structure.md`
- **Corruptions:** L9 "All math, hashing, geometry, linalg, sparsity, and codec work lives in the C/C++ engine" — "codec" banned (truth 10).
- **OPEN flags:** None.

#### `0027-separation-of-concerns-invariants.md`
- **Corruptions:** L11 listed the engine as doing "codecs" — banned (truth 10), contradicts truth 6. L28 invariant matrix listed "codec" alongside "file-format read/write" — same banned framing, redundant with the legitimate "file-format read/write."
- **OPEN flags:** None.

#### `0029-custom-indexing-strategy.md`
- **Corruptions:** opclass #2 + Consequences described the S³/4-ball access pattern as "KNN traversal" / "KNN benchmark" — contradicts truth 3 (retrieval is NOT nearest-neighbor). Risk of reducing geometry to "just an index."
- **OPEN flags:** Retired "Chunk 1-5 / Story X.Y" cadence language left intact (cadence staleness, not a conceptual contradiction; flagged for the user to reword to issue references). Glicko-2 edge-weight / A* descriptions consistent with truth 4 — left intact.

#### `0035-prompt-ingestion-and-compiled-cascade.md`
- **Corruptions:** Cascade ranking described as generic "effective-score ranking" without naming the mechanism (truth 4: Glicko-2 effective-μ across typed arenas, indexed-A*, not NN). Prompt-local claims called "low-trust … evidence" — readable as a trust class (truth 5).
- **OPEN flags:** None.

#### `0036-arena-semantics-and-source-trust.md`
- **Corruptions:** Numbered 1-7 "Source classes are ordered by trust and purpose" ladder — forbidden by truth 5. "source credibility tracked per source per kind" framed as a fixed ordered ranking rather than emergent. "which source classes are allowed, preferred, discounted" — class-ladder admission. Context framed sources as inherently "not equal" / prompt claims "low-trust" by assigned class. References cited a retired glossary term "Source Trust Class."
- **OPEN flags:** None.

#### `0037-layered-seed-ingestion-and-model-codec-fidelity.md`
- **Corruptions:** Title "model-codec fidelity" and pervasive "codec" framing — truths 10 and 6. Context "AI models … whose computations are recorded as physicalities and attestations" — truths 1-2 require streaming O(params) ETL of weight cells as Glicko-2 matchup outcomes; truth 3 says physicalities are the S³ access frame. Decision "AI model ingestion is a codec" + "captures the source model faithfully" + "lottery-ticket sparse load-bearing structure" (truth 1). Fidelity tied to "captures the model's load-bearing computation" — glossed over unsolved interior d×d resolution. Reference misnamed RULES R21 as "model-codec fidelity" (actual: "model dissolve/synthesize fidelity"). "DESIGN.md — model codec fidelity" carried the banned label.
- **OPEN flags:** Interior d×d → token resolution OPEN; arena/kind per role OPEN; frontier-scale synthesis OPEN.

#### `0039-schema-reorganization-entity-identity-vs-physicality-representation.md`
- **Corruptions:** §2 "Geometric structure … is value-additive enrichment … Strip the geometric layer entirely and the substrate still functions" — contradicts truth 3. Consequences "Inference is unblocked from geometry … not load-bearing." ADR 0005 implication "Hilbert is value-additive enrichment, not load-bearing for inference." Alternatives "Geometry is enrichment, not load-bearing." All demote geometry that the anchor places on the A* hot path (truth 3).
- **OPEN flags:** None.

#### `0040-multi-modal-entity-types-universal-t0.md`
- **Corruptions:** Title + canonicalization sections framed canonicalization as "lossless" in a way that read like artifact preservation (truth 6). "Vampire mode" cute name used twice (L72, L88) (truth 10). L88 asserted interior d×d → token resolution as settled (every transformer kind "a Glicko-2 matchup between token entities"). Model-ingest line (L72) described only "recipe + tokenizer + typed-tensor-calculation attestations remain" without the streaming ETL framing (truths 1-2).
- **OPEN flags:** Interior d×d → token resolution for q/k/v/o/gate/up/down marked OPEN in L88; per-role arena/kind OPEN. Left intact as legitimate plumbing: "lossless WebP"/"FLAC" (lossless-encoding-format examples), "Lossless dedup at corpus scale," ADR 0037 reference filename — correct technical senses, not banned framing.

#### `0041-decomposer-scope-full-domain-ecosystem.md`
- **Corruptions:** Consequences invoked a fixed "Source Trust Class" / "trust band" / "trust class" ladder — truth 5. "class" here was the banned TrustClass framing.
- **OPEN flags:** None (ADR never asserts interior d×d resolution).

#### `0042-bootstrap-order-and-substrate-canonical-seeding.md`
- **Corruptions:** Stage 3 "…+ kind-value tiers" seeded "11 kind-value-tier entities (T1…T11)" with fixed Glicko-2 priors + cascade-weight multipliers (truth 5). T9 "Tensor-Calculation" tier name implied per-cell weight calculation at ingest (truth 1). Stage 3.5 "source-trust-class taxonomy (10-tier hierarchy)" seeding TrustClass_SubstrateMandate…TrustClass_AdversarialUntrusted with HAS_PRIOR_WEIGHT/HAS_EFF_MU_MULTIPLIER + HAS_TRUST_CLASS (truth 5). Canonical kind "IS_LOSSY_ENCODING_OF" — round-trip preservation framing (truths 6, 10). Canonical kinds "HAS_TRUST_CLASS"/"HAS_KIND_VALUE_TIER" — exist only to wire the ladders.
- **OPEN flags:** Whether any attestation-kind-value priors are seeded at bootstrap (and what they are) OPEN; the fixed tier ladder rejected. Whether/how source-trust priors are bootstrapped vs. emergent OPEN. ADR 0044 itself still asserts these as settled and must be audited; ADR 0037 filename retains banned "codec" framing.

#### `0043-composite-decomposer-architecture.md`
- **Corruptions:** Choreographer step described model ingest as "emit typed-tensor-calculation attestations" with no constraint — room for the banned GEMM-at-ingest / vocab² / top-k disease, implying interior cell-to-token emission is solved (truths 1, 2, 6). ADR predates and never cited ADR 0056 or the foundation doc; ingest framing carried no anchor and no bit-perfect-is-non-goal statement.
- **OPEN flags:** Interior d×d cell → token-entity resolution OPEN. Left intact: L52 dtype decode target ("canonical FP32 … TBD per attestation arena policy") — appropriately uncertain; "orthogonal axes" on context/decision lines (refers to container/dtype/architecture/modality plugin factorization, a legitimate engineering claim, NOT the banned geometry framing); "codec" on the AudioModality line (literal DSP codec) and the ADR-0037 filename reference.

#### `0044-attestation-kind-priors-and-source-trust-taxonomy.md`
- **Corruptions:** Part B "Source trust class taxonomy (10 tiers)" — fixed TrustClass ladder (Substrate Mandate=1.0 … Adversarial=0.0) with hardcoded priors + per-class effective-μ multipliers (truth 5). "Tier" used as a column label + a T1–T11 kind-importance ladder (truth 5). Context item 2 "hierarchical, pick which tier." Consequences asserted bootstrap seeds "10 trust-class entities + 11 kind-value-tier entities", HAS_TRUST_CLASS/HAS_KIND_VALUE_TIER, default-to-Tier-8 fallback. T9 row (Part A) asserted confident priors/cascade weights/kind↔arena mapping for interior tensor roles (OPEN). Per-kind override example treated the Glicko-2 rating as read off a tier ladder.
- **OPEN flags:** Interior d×d → token resolution + per-interior-role arena/kind OPEN in Part A. References (L109) still cite GLOSSARY term "Source Trust Class" — left as a cross-doc pointer (GLOSSARY is a separate target). Struck-through ladder tables preserved (struck through, marked REMOVED) to keep the historical decision record per ADR-as-decision-format convention; full deletion is a follow-up.

#### `0047-text-decomposer-pure-primitive.md`
- **Corruptions:** L16 "Bit-perfect corpus round-trip requires preserving the observed scalar sequence" (truths 6, 10). L75 "Bit-perfect export: reconstructing CONTENT trajectories emits the observed UTF-8" (truth 6). L79 "merges forms that must remain distinct for codec fidelity" (truth 10). Stale plumbing: C# path listed as `Laplace.Engine.Core/TextDecomposer.cs`; actual is `app/Laplace.Engine.Core/TextDecomposer.cs`.
- **OPEN flags:** None.

#### `0049-substrate-change-intent-type.md`
- **Corruptions:** L117 listed "round-trip count" as a per-intent INGEST observability metric — leaks codec/round-trip framing into ingest (truths 6, 10). Round-trip vs. retarget is a synthesis-shape choice, not an ingest-tracked quantity.
- **OPEN flags:** Engine header `engine/core/include/laplace/core/substrate_change.h` (cited L109) does NOT yet exist — acceptable as design-intent, left intact, but the plumbing claim is aspirational. AttestationRow carrying initial Glicko-2 Rating/Rd/Volatility/ObservationCount consistent with truth 2 — left intact. byte `Tier` field + "entities by tier ascending" use "tier" correctly for the Merkle stratum — left intact.

#### `0051-idecomposer-csharp-plugin-contract.md`
- **Corruptions:** `TrustClassId` interface field, `HAS_TRUST_CLASS` meta-attestation, and the `AcademicCurated` trust class treated trust as a fixed class (truth 5). Verified the shipped code (`Laplace.Decomposers.Abstractions/AttestationFactory.cs`) carries the literal corruption: a `TrustClass` enum ladder SubstrateMandateTier1..AdversarialTier10 with fixed weights 1.00..0.00, plus `KindValueTier`. References described ADR 0044 as a "source-trust-class taxonomy" supplying `TrustClassId`.
- **OPEN flags:** Shipped code still hard-codes the `TrustClass` enum ladder + fixed weights and the interface still ships `TrustClassId` (`IDecomposer.cs:60`); ADR now states the corrected contract but the C# code and ADR 0044 remain to be corrected toward a Glicko-2-prior shape. ADR 0044 is the upstream source and needs its own audit.

#### `0054-selective-deployment-profiles.md`
- **Corruptions:** EMBEDDED cascade behavior (L59) framed degraded-mode inference as "walks T0 codepoint relationships … co-membership; UCA-collation neighborhood; Hilbert-locality" with "no DB-stored attestations" — edges toward NN framing forbidden by truths 3-4.
- **OPEN flags:** None.

#### `0055-static-structural-parse-exploded-view.md`
- **Corruptions:** L133 "The substrate then extracts tensor-calculation attestations per the architecture template" — presents the OPEN d×d resolution as solved. Context (L14) leaned on "Food principle" / "Vampire mode" / "food" as load-bearing concepts (truth 10) without stating the mechanism (truth 6). Consequences (L162) "Generalizes Vampire mode." Alternatives (L176) rejected an option by invoking "Food principle"/"Vampire mode" rather than the mechanism.
- **OPEN flags:** Interior d×d → token resolution marked OPEN in the worked example (embed_tokens/lm_head token-anchored). Arena/kind per role + frontier-scale synthesis OPEN per anchor but out of this ADR's scope (container parsing only).

#### `0056-weight-tensor-etl-as-arena-matchup-observation.md`
- **Corruptions:** GEMM-at-ingest asserted as the mechanism: `q_proj[i,:]·k_proj[j,:]ᵀ` over the vocab (Phase-1 pseudocode, family table, multimodal example, `~50K×50K×60×32` FP-MAC sweep, Alternatives `Q_PROJECTS = q_proj·k_proj`) — the forbidden `E·W·Wᵀ·Eᵀ` vocab² bilinear (truth 1). Interior d×d → token resolution asserted SETTLED ("nothing unsettled") — the #1 OPEN question. Non-entity object axes (`hidden_dim`/`intermediate_dim`/`embed_dim`/`latent_dim`/`state_dim`/`feature`) used as matchup_space objects — there are no dimension-index entities. Trust-tier / value-tier ladders as Glicko-2 prior inputs (truth 5). `scale_aggregated_strength_into_rating`/`ScaleToRating` stamping a scaled weight into the rating column (truth 2). `EMBEDS`/`OUTPUT_PROJECTS` listed as kinds (they are PROJECTION physicalities per the Status correction). Reference "weight magnitude → Glicko-2 prior" (truth 2). Cute names ("Vampire mode"/"Food principle") as concepts (truth 10).
- **OPEN flags:** Interior d×d cell → token-entity-pair resolution OPEN throughout. Arena/kind per role (incl. NORMALIZES unary, MoE per-expert aggregation, MLA decompression, CNN/conv kernel→pair, SSM) OPEN. Per-model pre-sparsity / retained-row counts left OPEN (downstream of interior resolution, not asserted with vocab²-derived figures). Engine paths (`engine/synthesis/src/weight_tensor_etl.{c,cpp}`, header) do not yet exist — consistent with Proposed status, left intact. Status-correction header (L5-11) is Anthony-authored "authoritative"; not deleted — only a Reconciliation marker added where its "nothing unsettled" claim conflicts with the equally-ratified anchor. Confirm the reconciliation framing with Anthony.

#### `0057-substrate-emission-discipline-product-not-packaging.md`
- **Corruptions:** Cute-name labels "Food principle"/"Vampire mode"/"codec"/"codec fidelity" as load-bearing concepts (truth 10). "What bit-perfect emittable means" + product definition "bit-perfect emittable" (truth 6). AI-model row + prose framed model ingest as merely "discard weight bytes" rather than the truths 1-2 mechanism. Model emission referenced "codec fidelity" (ADR 0037) instead of behavioral / typed-knowledge alignment.
- **OPEN flags:** Frontier-scale synthesis "pour facts into the mold" OPEN. Out-of-scope process drift NOT edited: ADR still references "Chunk 8" / Story 8.x (retired by ADR 0060) — process history, flagged for a separate cleanup pass. Interior d×d resolution not asserted here (lives in ADR 0056), so no OPEN marker needed.

#### `0058-canonicality-criterion-for-ingestible-sources.md`
- **Corruptions:** Trust-class ladder framing (truth 5) in step 5, the "What this ADR does NOT do" bullet, two Consequences bullets, two Alternatives bullets, the ADR 0044 reference gloss. Cute-name-as-concept (truth 10): "Food principle + Vampire mode" Consequence + GLOSSARY reference glosses. Bit-perfect drift (truth 6): heuristic (4) "Roundtrippability" defined canonicality as "losslessly reconstructed to the publisher's intended bit-pattern"; matrix notes "pixel content reconstructs bit-identical" / "PCM samples reconstruct bit-identical." Codec/round-trip prose (truths 10, 6): GGUF row "chat-verification round-trips", DESIGN.md "Model-codec fidelity" gloss, ADR 0037 "model-codec fidelity" gloss.
- **OPEN flags:** None.

#### `0059-format-writer-emission-matrix-and-ifw-contract.md`
- **Corruptions:** False certainty on OPEN-Q #1: asserted substrate cross-source matchup attestations ARE the AWQ activation-distribution / GPTQ Hessian / EXL2 measurement proxy, enabling calibration with "No real forward pass needed" — presumes interior q/k/v/o/gate/up/down cells resolve to (token_i, kind, token_j) matchups (unsolved). The matrix calibration column (AWQ/GPTQ/EXL2) stated substrate-derived calibration as settled. IFormatWriter CalibrationRequirements doc-comment asserted no-forward-pass calibration as settled. Consequences/Alternatives claimed "Cross-source consensus IS the activation/gradient/measurement distribution" as locked. Cute-name "the universal Food principle at the emission boundary" (truth 10).
- **OPEN flags:** AWQ/GPTQ/EXL2 substrate-derived calibration (no-forward-pass quantization) OPEN — blocked on interior d×d → token resolution; must be pinned before any writer Story for these formats. safetensors/GGUF rows describe materializing interior tensors from substrate-aggregated attestations per recipe layout — left intact as the sanctioned synthesis direction (truth 8), but the interior materialization algorithm remains OPEN. GLOSSARY links to cute-named entries ("Vampire mode", "Zero Calories") left as navigation plumbing.

#### `0060-retire-chunk-sequence-v0.1-milestone-cadence.md`
- **Corruptions:** Decision point 3 framed the v0.1 deliverable as "one faithful source-model round-trip" per the "model codec fidelity" ADR (truths 6, 10). The seed-ladder (#184-#194) called "fidelity enrichment" — implies preservation fidelity; truth 6 frames seed sources as OPTIONAL enrichment with model semantic ingest as the spine. Implicitly treated the model round-trip as a settled milestone without flagging the OPEN interior d×d dependency.
- **OPEN flags:** Interior d×d → token resolution that the v0.1 round-trip depends on marked OPEN.

#### `docs/adr/README.md`
- **Corruptions:** Stale index titles contradicting both the corrected ADR H1s and the anchor: 0037 "model-codec fidelity" (truths 6, 10; ADR H1 already corrected to "model-ingest fidelity"); 0040 "semantic Merkle DAG with lossless canonicalization" (truth 6; H1 corrected to "deterministic content-addressed canonicalization"); 0044 "source-trust-class taxonomy" (truth 5; H1 corrected to "source-trust (emergent Glicko-2, not a class ladder)"); 0057 "universal Food principle" (truth 10; H1 corrected to "universal dissolve-and-resynthesize principle").
- **OPEN flags:** None.

### Research docs (`docs/research/`)

#### `drift-register-2026-05-28.md`
- **Corruptions:** C2 + C8 + sequencing table treated the `TrustClass_AIModelProbe` / `TrustClass_*` taxonomy as legitimate structure to preserve, framing the only question as "rename vs keep canonical name (avoid hash churn)" — contradicts truth 5 (the whole ladder is corruption). C2 + STANDARDS row referenced "Kind value tiers" + "trust class table"/"trust class list" as structures to scrub inside, without noting the tier/class framing itself violates truth 5. C8 titled/scoped as "naming churn vs canonical-name stability" with default "keep canonical, scrub prose" — choosing between two corrupt forms of a banned ladder.
- **OPEN flags:** The trust-class ladder vs truth 5 now flagged OPEN throughout C2/C8 — needs Anthony to decide how to dissolve the hardcoded ladder into emergent Glicko-2 trust without picking a replacement label. Touches a substrate-canonical entity name (BLAKE3/entity-ID implications per ADR 0042); not safe to auto-resolve.

#### `grounded-model-codec-foundation-2026-05-28.md`
- **Corruptions:** "Everything maps to tokens — embedding + interior tensors [ratified 2026-05-28]" asserted an OPEN question as settled ("uniformly" maps to token↔token; "Nothing is unsettled"). Title and filename use the banned cute-name "codec" (truth 10).
- **OPEN flags:** Interior d×d → token resolution OPEN; arena/kind per role OPEN; synthesis at frontier scale OPEN.

#### `stream-plan-2026-05-27.md`
- **Corruptions:** Pervasive "codec" framing + "eats"/"drains"/"Competitors are food" + "Vampire mode" (truth 10). L7 "10 trust classes from TrustClass_SubstrateMandate (tier 1) through TrustClass_AdversarialUntrusted (tier 10)"; Phase 5 "KindValueTier.T9" + "TrustClass.AiModelProbeTier7"; kind-restore "TrustClass_AIModelProbe" (truth 5). L27 "the emitted model is substrate-consensus, not source-faithful" + L51 "43% interior drop still coherent" framed success in source-fidelity terms (truth 6). L27 + materialize_tensor (L80) asserted slot population such that `S = E @ q_proj @ k_proj^T @ E^T` align ordinally — a GEMM-at-synthesis guess presented as the answer (OPEN-Q #1). Wrong self-bilinear V/O/GATES/UP/DOWN emission corrected to name vocab²-GEMM-at-ingest as the actual disease (truth 1).
- **OPEN flags:** Interior d×d → token resolution for Q/K/V/O/GATES/UP/DOWN (subject/object/reduction, token×token vs unary) OPEN throughout. Arena/kind per role (incl. NORMALIZES) OPEN. Synthesis at frontier scale OPEN. ADR 0044 Part B trust-class ladder flagged in-doc as corruption (ADR not edited here). The single-weight-cell → initial Glicko-2 matchup scaling (`log10(strength/median)`) left as a tactical detail to ground with Anthony, not asserted settled.

#### `substrate-algorithms.md`
- **Corruptions:** Source-trust modeled as a fixed tier/class ladder with min/max envelope clamp (ADR 0044 "source-trust classes 1-10", "kind-value tiers T1-T11", "TrustClass", "Class 9 effective-mu band") in Axiom A5 (L84), ARI invariants (L261), ACG ρ-update + prose (L659/662), ACG sketch clip (L674-675), ACG novelty delta 3 (L704), ACG invariant (L711), ACG open problem 2 (L719), ACG pitch (L727), PLT invariant stub (L802) — truth 5. Physicality/geometry described as "an access/index lens, not a knowledge layer" with semantic responses only from A, not Φ (Axiom A7, L88) — truth 3. "codec" for ADR 0037 ingest/synthesis (DSS stub, L906) — truth 10. Synthesis "faithfulness metrics vs source model" (DSS stub, L909) — truth 6. DSS update-rule stub `W_ij = Ψ(M(e_i, k_ij, e_j))` presented as a single settled materialization map covering all tensors (L895) — OPEN-Q #1.
- **OPEN flags:** Interior d×d → token resolution OPEN in §10 DSS + Object (only embed_tokens/lm_head well-posed). Arena/kind per role OPEN in §10. Frontier-scale synthesis OPEN in §10 DSS Object + open-problems. ACG adversarial-robustness bound under self-tuning (non-tier) trust OPEN in §5 (the old confident tier-band bound removed). Flagged: this doc repeatedly cites ADR 0036/0044 for arena/trust policy — those ADRs' trust-class/tier prose is itself corrupt and are the next artifacts to reconcile.

---

## 3. Issues reopened

**None.** Every flagged issue was already OPEN and tracks genuinely unfinished work; none had been closed on wrong work. No reopen was warranted. Corrections were delivered as non-destructive audit comments (issue bodies left intact) so the record is reversible and the user retains authorship.

---

## 4. Issues flagged

All flagged issues received a non-destructive audit comment prefixed `AUDIT 2026-05-28 (foundation-audit)`. No bodies were edited; no issues were reopened (all already OPEN). All carry verdict **corrupt** against the anchor.

#### #281 — intent-journal checkpoint drift
Substantially clean. Core (content-addressed intent IDs surviving db-nuke causing FK failures; move journal into substrate) is verifiable plumbing consistent with the anchor; no codec/GEMM/NN/bit-perfect framing. One corruption: Option B cites "per ADR 0044 tier-T1-Mandate weight" for a HAS_LAYER_COMPLETED meta-attestation — a trust-tier/TrustClass ladder (truth 5); ADR 0044 has already self-struck that ladder, so the citation points at retired content. Audit comment posted (completion meta-attestation OK as a shape, but Glicko-2 standing is emergent, not a fixed T1 class). Body left unedited.

#### #280 — doc/issue drift reconciliation epic
Spine consistent with the anchor: C2 correctly reframes AI-model ingest as static weight-tensor ETL (ADR 0056), not probe (truth 1). Mechanical facts verified live (drift register file exists; #281 exists and is OPEN). Corruption: the C2/C8 sub-plan frames the only open question as keeping the canonical name `TrustClass_AIModelProbe` vs rename, treating the GLOSSARY Trust Class table as a row to scrub — truth 5 bans `TrustClass_*` ladders entirely (the whole enum is the rot, and `TrustClass` is a cute-label tell, truth 10). Recommended reframing C2/C8 from a naming decision to retiring the ladder for a Glicko-2 trust value. Audit comment posted; body not overwritten.

#### #276 — move ADR 0044 tier/trust prior tables into single source of truth
Corrupt at its premise (truth 5): it treats `KindValueTier.T1..T11` fixed-μ priors (ADR 0044 Table A) and the `TrustClass.SubstrateMandateTier1..AdversarialTier10` fixed-weight ladder (Table B) as substrate canon and proposes hardening them into a single source of truth (C engine alongside `glicko2.c`, or a substrate-seeded table) — making the corruption load-bearing across the engine and cascade SRF, the opposite of the anchor's correction. Proposed entry points `attestation_priors_tier()` / `attestation_priors_trust_weight()` misuse "tier." Verified: `AttestationFactory.cs` TierPrior switch (L98) and TrustWeight switch (L115) exist as quoted; `glicko2.c`/`glicko2.h` exist. Audit comment recommended rewording from "relocate the tables" toward "remove the ADR 0044 fixed tier/trust ladders and replace with self-tuning Glicko-2 trust." Body not edited.

#### #275 — SubstrateCanonicalIds helper
Source/Type/Kind/Entity consolidation is clean and anchor-consistent. Corruption: the helper includes a `TrustClass(string name)` member, and the live code it would consolidate is a fixed trust ladder (StandardsDerived, AcademicCurated, StructuredCorpus, AIModelProbe, etc.) across 12 call sites (truth 5). Audit comment recommended: drop `TrustClass` from the helper, do not migrate the 12 `trust_class` literals into it (launders corruption via a reusable-helper label) but track them for removal toward Glicko-2 effective-μ, scope acceptance to Source/Type/Kind/Entity. Verified live: helper does not exist yet; raw `substrate/` literal count is 128 (body says 152 — drifted); 12 are `substrate/trust_class/*`. Body not edited.

#### #274 — move WeightTensorETL math into C (oneMKL cblas_dgemv)
Corrupt — would harden two anchor-violating ingest paths by porting them to BLAS: (1) `AggregateLayerThroughEmbed = |W|×E` (interior weight matrix × the vocab×d_model embedding) applied to v/o/gate/up/down_proj (`WeightTensorETL.cs:152-160`) — exactly the forbidden GEMM-at-ingest (truth 1); the proposed `embed_aggregate_layer_through_w` just makes the disease faster. (2) Interior cell → token resolution treated as settled — explicit OPEN-Q. (3) `ReducePerCellMagnitude` (per-row L2 norm → magnitude signal) is the magnitude-as-rating category error (truth 2). Verified: both functions exist at cited lines; `weight_projection.cpp` present; ADR-0027 "math-out-of-C#" motivation genuine. Audit comment recommended re-scoping — block on the OPEN interior-tensor resolution, drop the `|W|×E` deliverable, port genuinely-correct streaming matchup-emission math only once ratified. Body not edited.

#### #259 — pull C# app layer back to orchestration-only (ADR 0027) epic
Spine consistent; most child stories clean. Three flags: (1) Story #274 + body item #2 would relocate and bless `WeightTensorETL.AggregateLayerThroughEmbed` (`WeightTensorETL.cs ~248-292`), which resolves interior tensors (V/O/G/U/D) to token entities via the embedding (`E @ |W|` over [vocab × dim]) — OPEN-Q #1 + forbidden GEMM-at-ingest (truth 1); recommended re-scoping #274 to move ONLY the embedding-anchored row-norm, leaving the interior projection OPEN (did NOT fabricate an alternative). (2) Story #276 + body item #5 use "tier"/"trust_class" for trust priors (truth 5); recommended Glicko-2 source-trust priors, drop "tier." Stale (mechanical): body pins HEAD=b6a78b8 but live HEAD is 1319514 (two later manual user commits); Verification-block counts are as-of-b6a78b8. Recommended fixes target child story bodies #274 and #276, not the epic body. Audit comment posted; body not edited.

---

## Cross-cutting follow-ups (out of scope for this audit pass)

- **ADR 0037** filename retains `model-codec-fidelity` (banned label, real path). Renaming the file is a separate user-authorized action; all prose references were de-cute-named.
- **Shipped C# code** still hard-codes the `TrustClass` enum ladder + fixed weights (`AttestationFactory.cs`) and `IDecomposer.TrustClassId` (`IDecomposer.cs:60`). The ADRs/docs are corrected; the code migration toward Glicko-2 priors remains (tracked via #274/#276/#275/#259).
- **Substrate-canonical entity names** (`TrustClass_AIModelProbe` etc.) appear in bootstrap SQL (`10_bootstrap.sql.in`) and possibly shipped seed — the BLAKE3 entity-ID migration is unresolved and must be reconciled with the live system before db-nuke/reseed.
- **Memory files** (`project_laplace_commercial_strategy`, others) still use "codec"/"build-a-bear" — contradict truth 10; out of scope for this corpus pass.
- **CLAUDE.md hard rule 3 tension:** DESIGN/GLOSSARY/RULES/STANDARDS/OPERATIONS/README were edited in place per the audit task instruction; flagged for the user's awareness. Direction mirrors the documented 2026-05-28 grounding.
