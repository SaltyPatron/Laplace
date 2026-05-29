# Repair the substrate, then build the substrate

## Context

The invention per docs/SUBSTRATE-FOUNDATION.md: a universal, content-addressed, CPU-native knowledge substrate that **dissolves** every digital ecosystem — linguistic corpora, AI models, knowledge graphs, code, documents, images, audio, video, raw datasets — via per-domain decomposers, into typed, sourced, Glicko-2-rated attestations + content-addressed entities + per-source physicalities, **discards the original blob**, and synthesizes output (model files, queries, custom-recipe model emissions, dataset re-exports, cross-source consensus reports) from substrate state. It replaces the transformer: stored weights → rated relationship graph; the GEMM forward pass → indexed A\* traversal across many typed arenas; training → authoring facts (CRUD). Output is superior to any single ingested source by construction — deduplicated, consensus-rated, cross-source-enriched, queryable across modality boundaries, composable. Substrate IS the model + runtime + database + tokenizer + embedding-like physicality layer + knowledge graph + inference layer + visualization channel + synthesis pipeline, in one coherent layer.

The substrate's data discipline is **source-axis three-class**: substrate data (canonical seed knowledge from foundational sources), app data (runtime/operational state), user data (prompt-local content). All three classes flow into the same three tables (`entities`, `physicalities`, `attestations`) — the distinction is which source entity attests what + how the per-source trust value governs admission to which arenas. Trust is a **Glicko-2 value, self-tuning from cross-source agreement — never a fixed tier or class ladder** (per docs/SUBSTRATE-FOUNDATION.md truth 5; "tier" is reserved exclusively for the Merkle stratum, T0 = Unicode codepoints). Any prior "10 trust classes / TrustClass_* ladder / tier 1..10" framing (e.g. ADR 0044 Part B as previously drafted) is corruption to be corrected toward a Glicko-2 trust value, not a discrete class index.

The substrate's content discipline is **attested-vs-normal**: every entity exists as content (decomposed bytes, content-addressed). Attested content additionally carries typed attestations from one or more sources. A user prompt's tokens dedupe to existing substrate text entities (inheriting whatever attestations those entities carry from prior sources) but the user's claims do not promote to global truth.

Across multiple prior sessions I (Claude) have introduced extensive rot by pattern-matching to conventional AI primitives — treating the substrate as if it were a vector DB, treating substrate edges as a compressed form of weight tensors needing pseudoinverse decompression, conflating data classes, narrowing the documented kind vocabulary without authority, hardcoding vendor-naming maps that ADR 0043 explicitly forbids. This plan is the path to repair the rot, then build forward.

## Corrected understanding (things I had wrong this session)

1. **Attestations are between token/text entities (token×token matchups, or unary); dimension indices are never the axes.** Subject = text entity, kind ∈ {EMBEDS, Q_PROJECTS, K_PROJECTS, V_PROJECTS, O_PROJECTS, GATES, UP_PROJECTS, DOWN_PROJECTS, NORMALIZES, OUTPUT_PROJECTS}, source = model entity, context = NULL. The "hidden_dim / intermediate_dim / embed_dim" axes in ADR 0056:158-165 spec table are **tensor-shape descriptors**, NOT attestation object axes; per-(layer, head, expert, dim) layout is recipe content on the model recipe entity per the ADR 0056 same-day amendment, NOT substrate entities and NOT per-attestation context. That much is anchor-aligned. **What is OPEN per docs/SUBSTRATE-FOUNDATION.md:** for `embed_tokens`/`lm_head` (EMBEDS / OUTPUT_PROJECTS) the token anchoring is direct and real; but for interior tensors (Q/K/V/O/GATES/UP/DOWN) the resolution of each cell to a token entity (and whether the kind is token×token or unary, and the reduction that produces it) is the anchor's #1 unsolved question — do NOT treat the specific subject/object/reduction assignment for interior roles as settled, and the per-cell→matchup reduction must stream O(params) over weight cells, never GEMM over vocab² at ingest (truth 1). Row-count estimates (~10⁵-10⁷/model after lottery sparsity) are downstream of that open shape.

2. **Dimension indices are not substrate entities.** I previously suggested integer indices content-address through TextDecomposer into universal text entities. That conflates THREE different things:
   - text "2048" as substrate-canonical content (codepoint sequence appearing in documents) — substrate data
   - "the 2048th dim of a specific model's hidden layer" as a structural parameter — recipe content on a specific model recipe entity (app/recipe data layered on substrate)
   - A globally-shared dim entity that dedupes across models — does not exist; was my hallucination
   Anthony's correction also surfaced the broader **data-class discipline** (substrate / app / user) + **content discipline** (attested-vs-normal) I had been blurring across multiple sessions.

3. **Synthesis is not a pseudoinverse problem.** Per Memory explicitly: "Synthesis = template distributes aggregated attestations back into recipe shape (not pseudoinverse)." Per ADR 0056:183 + DESIGN.md:660: `IArchitectureTemplate::materialize_tensor(const TensorSpec&, SubstrateView&) → TensorValues`. The architecture template queries the substrate for the consensus matchup ratings under the recipe's source scope, then populates each tensor slot per the recipe's layout — broadcast of consensus values across recipe-specified per-(layer, head) slots, modulated by per-instance count normalizer.

   **Important nuance I missed initially**: ADR 0056:50 explicitly lists "spectral embedding that places typed-edge-related tokens close in N-d space by construction" as one of the five behavioral-robustness mechanisms of the substrate. So spectral embedding via `laplacian_eigenmaps_from_sparse_graph` is itself substrate-native and legitimate — it was NOT the rot. What was rot: feeding that spectral embedding into a SVD pseudoinverse (`reconstruct_w` / `reconstruct_qk`) to recover interior W matrices. The spectral primitive in `engine/dynamics/src/eigenmaps.cpp` survives Stream A; only the pseudoinverse downstream (`engine/synthesis/src/reconstruct_w.cpp`) was deleted.

   Concrete synthesis math for an interior slot (e.g. Q_PROJECTS at layer L head H, shape [hidden_dim, hidden_dim]) is **OPEN per docs/SUBSTRATE-FOUNDATION.md** and must be grounded with Anthony before code lands. The anchor's #1 open question is exactly this: `embed_tokens`/`lm_head` are directly token-anchored (cheap, real), but how interior `q/k/v/o/gate/up/down` cells resolve to token entities *without* re-running the GEMM (which is what blows up) is **unsolved**. The previously-asserted "populate the slot such that computed-at-inference attention scores `S' = E @ q_proj @ k_proj^T @ E^T` align ordinally with substrate consensus" is itself a GEMM-at-synthesis guess on the open interior-resolution question — do NOT treat it as settled. The only thing the docs do fix: the emitted model is the materialized consensus of the facts exerted on entities, not a bit-exact copy or weight-average of any source (docs/SUBSTRATE-FOUNDATION.md truths 6, 8). The interior resolution and the synthesis "pour facts into the mold" algorithm at frontier scale remain OPEN — flag, do not invent.

4. **Vendor naming is substrate content.** Per ADR 0043:66: `TENSOR_NAME_MEANS_MECHANICAL_ROLE` attestations between text entities (the name string) and mechanical-role kind entities, sourced to vendor source entities (`Meta_Llama_naming_convention_source`, `Mistral_AI_naming_convention_source`, …). Hardcoded plugin switches are explicitly rejected.

5. **The CLI surface must be universal.** Per Anthony's correction during Stream A execution: `SynthesizeTinyLlamaAsync` / `synthesize tinyllama` / `TinyLlamaDir` hardcoded const all conflated one model family / instance with the universal-substrate posture. Universal `synthesize substrate <recipe.json>` + `synthesize passthrough <model-dir>` shipped in Stream A. Same applies to `LlamaWeightExtractor` — Stream B's `WeightTensorETL` per ADR 0056:153 is data-driven per-family registry, not per-family hardcoded class.

## Streams of work

The invention spans at least eight streams. Model semantic-ingest/synthesis is one. Hyperfocusing on it (as my prior plan attempts did) is rot-shape. ("Codec" is banned per docs/SUBSTRATE-FOUNDATION.md truth 10 — it implies round-trip preservation, which is exactly the disease; the correct framing is model **dissolution into rated facts** on the ingest side and **synthesis from consensus** on the emit side, NOT a lossless encode/decode pair.) Each stream below is named with what's there, what's broken, what's needed, and what unblocks what.

### Stream A — Model semantic-ingest/synthesis repair (revert the rot) ✅ DONE 2026-05-27 (commits be99495, feea7e4)

**What's there.** Across recent commits (`2402716`, `ebe8761`, `e8d7677`, `0f77f05`) plus the 7-file uncommitted diff: a pseudoinverse synthesis pipeline (Laplacian eigenmaps on Glicko-2 effective-μ adjacency → `reconstruct_w` via Eigen LDLT + SelfAdjointEigenSolver / JacobiSVD → tensor write), a token×token bilinear ingest for V/O/GATES/UP/DOWN via self-bilinear `E·W·Wᵀ·Eᵀ`, a kind-vocabulary narrowing that deleted EMBEDS / K_PROJECTS / NORMALIZES / OUTPUT_PROJECTS, a hardcoded `HfToGgmlName` switch, embed_tokens / lm_head as PROJECTION / PROJECTION_OUTPUT physicalities instead of EMBEDS / OUTPUT_PROJECTS attestations.

**What's broken (with cites).** Pseudoinverse pipeline violates substrate-architect Hard Rule 1 and Memory explicit "not pseudoinverse." Kind-vocabulary narrowing violates ADR 0044 T9 + GLOSSARY:95 + ADR 0056:163-165 + DESIGN.md:731. Self-bilinear `E·W·Wᵀ·Eᵀ` ingest for V/O/GATES/UP/DOWN is wrong because it does GEMM over vocab² at ingest, which is the disease per docs/SUBSTRATE-FOUNDATION.md truth 1 (it must instead stream O(params) over the already-computed weight cells). The correct per-cell reduction that resolves these interior tensors to token entities is **OPEN per docs/SUBSTRATE-FOUNDATION.md** (interior `d×d` axis → token-entity resolution, the anchor's #1 unsolved question) — do not assert a specific "token×token matchup after per-cell-magnitude reduction" shape as settled. `HfToGgmlName` switch violates ADR 0043:66. Embed/lm_head as physicalities omits the EMBEDS/OUTPUT_PROJECTS substrate state ADR 0056:163-164 specifies.

**What's needed (concrete, no Anthony-deferral).**
- Delete `engine/synthesis/src/reconstruct_w.cpp` (205 LOC) and `engine/synthesis/include/laplace/synthesis/reconstruct_w.h`.
- Remove `SynthInterop.ReconstructWFromTokenPairAttestations` + `ReconstructQkFromTokenPairAttestations` from `app/Laplace.Engine.Synthesis/NativeInterop.cs`.
- Delete `ReconstructInteriorTensorSymmetric`, `ReconstructInteriorTensorAsymmetric`, `BuildSubstrateAdjacencyAsync`, `BuildOutputDirectionAdjacencyAsync`, `ComputeSpectralEmbedding` from `app/Laplace.Cli/Program.cs`.
- Delete `HfToGgmlName` switch from `app/Laplace.Cli/Program.cs:681+`.
- Restore the 4 deleted kinds (`EMBEDS`, `K_PROJECTS`, `NORMALIZES`, `OUTPUT_PROJECTS`) in `app/Laplace.Decomposers.Model/ModelDecomposer.cs:40-50` with canonical hashes + kind priors. (Note: any `TrustClass_AIModelProbe`-style fixed trust-class label here is corruption per docs/SUBSTRATE-FOUNDATION.md truth 5 — the model source's trust is a self-tuning Glicko-2 value, not a discrete class.)
- Stash (do not delete — R24) the 7-file uncommitted diff so the Glicko-2 unification + AttestationFactory helpers + TextEntityBuilder static helpers are recoverable for the rebuild.
- Remove the existing physicality-based emission of `embed_tokens` / `lm_head` as PROJECTION / PROJECTION_OUTPUT in `app/Laplace.Decomposers.Model/LlamaWeightExtractor.cs` (these were the wrong substrate shape).
- Leave `SynthesizeTinyLlamaAsync` as a stub that returns an error pending Stream B's correct implementation, OR revert it to the pre-rot baseline (verifiable by `git log --oneline -- app/Laplace.Cli/Program.cs` to find the pre-`2402716` state). Note: any prior characterization of that baseline in terms of "interior drop %" / source-fidelity is the wrong success metric — per docs/SUBSTRATE-FOUNDATION.md truth 6, bit-perfect/source-faithful preservation is worthless; synthesis emits substrate consensus poured into the mold, and the interior-tensor resolution that baseline used is itself OPEN.

**Verification.** `cmake --build build` + `dotnet build` zero errors. `just db-nuke && just db-up && just seed-t0` succeeds. Ingest-tinyllama emits no token×dim attestations (only token×token matchups for tensors where Stream B has wired the correct math; until then no model semantic-ingest attestations emit). The tree is in a known-clean state from which Stream B can rebuild correctly.

### Stream B — Model semantic-ingest/synthesis rebuild correctly (PENDING Anthony grounding + tactical sketch below)

**What's needed.** Re-implement `LlamaWeightExtractor` per the corrected understanding: every transformer-family attestation is token×token (or unary for NORMALIZES), with per-(layer, head, dim) reductions specified by `spec.math_function` per family per kind. Implement `IArchitectureTemplate::materialize_tensor` per `DESIGN.md:660` for the Llama family — query substrate consensus, broadcast across recipe slots. Replace `HfToGgmlName` with `TENSOR_NAME_MEANS_MECHANICAL_ROLE` substrate attestations bootstrapped per vendor source entity.

**What blocks Stream B without Anthony's grounding.** I have hallucinated wrong implementations multiple times this session (token×token bilinear, eigenmaps+Procrustes, pseudoinverse, dim-as-text-entity). I do not trust my own derivation of the per-cell-magnitude reduction math, the broadcast normalizer for materialize_tensor, or the source-scope-to-SubstrateView query shape. These need Anthony's grounding via conversation (NOT another agent summary, NOT another doc-read pass) before any code lands. Stream B is therefore a multi-conversation rebuild, not a single-session code drop.

**Tactical sketch for Stream B (informational — not executed in this session).**

`WeightTensorETL.cs` (new, in `Laplace.Decomposers.Model/`, replaces stubbed `LlamaWeightExtractor.cs`):

- Phase 1 per-tensor matchup, driven by a per-architecture-family spec registry (data, not hardcoded switch). For each tensor in the safetensors header, the registry returns: `(kind_id, math_function, subject_axis, object_axis)`. The Llama-family registry seeds the spec entries `TENSOR_NAME_MEANS_MECHANICAL_ROLE` per ADR 0043:66 — bootstrap a `Meta_Llama_naming_convention_source` entity, then bootstrap a `tensor_name_pattern → mechanical_role` attestation per recognized pattern.
- Phase 1 math_function library. **CAUTION: the per-tensor reduction for interior tensors is OPEN per docs/SUBSTRATE-FOUNDATION.md** (interior `d×d` axis → token-entity resolution is the anchor's #1 unsolved question; the per-role math below is a tactical guess, NOT a settled mechanism, and any `qk_dot`-style reduction that re-runs the GEMM is forbidden at ingest per truth 1 — ingest must remain streaming O(params) over the weight cells, never `E·W·Wᵀ·Eᵀ` over vocab²). These entries must be grounded with Anthony before code lands:
  - EMBEDS / OUTPUT_PROJECTS — `embed_tokens` / `lm_head` are directly token-anchored (cheap, real per the anchor); these are the only tensors whose token resolution is NOT open. Stream a per-row reduction over the directly-anchored token axis; one instance only (no per-layer iteration).
  - Q/K/V/O/GATES/UP/DOWN — interior tensors. The reduction that yields token-entity matchups for these is **OPEN per docs/SUBSTRATE-FOUNDATION.md** — do NOT lock a `qk_dot` / `per_cell_magnitude_row_reduce` formula here. Whatever lands must stream over the already-computed weight cells (each cell = one Glicko-2 matchup outcome, truth 2), in parallel via oneTBB, scaling linearly to frontier models — never a GEMM over vocab², never a flat top-k that discards most of the model (truth 1).
  - NORMALIZES — per the anchor a unary fact on the relevant entity; exact arena/kind assignment per interior tensor role is itself OPEN per docs/SUBSTRATE-FOUNDATION.md.
- Phase 2 within-model aggregation: for each (subject, kind, object) tuple, aggregate across (layer, head, expert) per-instance strengths via L2 norm (ADR 0056:99 default). The aggregator keeps per-instance strengths internally so the recipe entity records the per-(layer, head) layout in its text/JSON content for emit-time distribution.
- Phase 3 lottery-ticket sparsity per R3: per-tensor top-k% on aggregates + per-subject top-k per kind. First pass uses static-mathematical retention (no probe) per ADR 0056 Phase 4 reframing (R3 third pass needs amendment).
- Phase 4 static-mathematical retention validator: spectral preservation + singular-value retention + matchup-distribution preservation between sparse and dense subgraphs. Implementation in `engine/synthesis/src/retention_validator.cpp` (new).
- Phase 5 emission: one `AttestationRow` per (subject, kind, object, model_source) via `AttestationFactory.Create`. Trust is a **Glicko-2 value, not a discrete tier/class** (docs/SUBSTRATE-FOUNDATION.md truth 5) — any `KindValueTier.*` / `TrustClass.*Tier7` ladder naming in the current code is corruption to correct toward a Glicko-2 opponent-strength value (the model's own self-tuning trust), not a fixed class index. The scaling of a single weight-cell outcome into an initial Glicko-2 matchup contribution (the `log10(aggregate_strength / median)`-style mapping) is a tactical detail to ground with Anthony — the load-bearing invariant is: weight = matchup outcome, source-model trust = opponent strength, store only the emergent consensus rating, never the weight (truth 2). Stage 2 sparsity-survivor `observation_count` = the per-instance count (number of contributing instances).

`materialize_tensor` (new, in `engine/synthesis/src/arch_template.cpp` — read but not modified in Stream A):

- Implements `IArchitectureTemplate::materialize_tensor(const TensorSpec&, SubstrateView&) → TensorValues` per DESIGN.md:660.
- For each tensor slot the recipe needs: query the substrate (via SubstrateView, which exposes the cross-source-aggregated `laplace_glicko2_accumulate` view per ADR 0056:206-215 + the source-scope filter) for the relevant `(subject, kind, object, context)` consensus rating.
- Distribute consensus values across the recipe's per-(layer, head, dim) layout. **For interior tensors (Q/K/V/O/GATES/UP/DOWN) the population of a `[hidden_dim, hidden_dim]` slot from token-pair consensus ratings is OPEN per docs/SUBSTRATE-FOUNDATION.md** — this is the same interior `d×d` axis → token-entity resolution that is the anchor's #1 unsolved question, plus the unsolved "pour facts into the mold at frontier scale" question. The previously-asserted spectral-embedding + `S' = E @ q_proj @ k_proj^T @ E^T` ordinal-consistency recipe is a GEMM-at-synthesis guess on that open question, not a settled answer — flag, do not lock. (Spectral embedding via `laplacian_eigenmaps_from_sparse_graph` is itself substrate-native per ADR 0056:50; what is NOT settled is using it to reconstruct interior weight slots.) Must be grounded with Anthony.
- For directly token-anchored tensors (EMBEDS / OUTPUT_PROJECTS): the per-token consensus rating distributes across the tensor's layout via a recipe-specified broadcasting function. The same broadcast question for V/O/GATES/UP/DOWN is gated on the OPEN interior-resolution above.
- For NORMALIZES: identity 1.0 broadcast (RMSNorm passthrough) until the substrate carries a per-(layer, role, dim) recipe layout that the template can use.

`TENSOR_NAME_MEANS_MECHANICAL_ROLE` bootstrap (new, in `ModelDecomposer.InitializeAsync`):

- Add `Meta_Llama_naming_convention_source` entity + the ~150 `TENSOR_NAME_MEANS_MECHANICAL_ROLE` attestations mapping Llama-family tensor names to mechanical-role kind entities (replaces `HfToGgmlName` switch which Stream A kept for the passthrough diagnostic).
- Per Anthony's accepted plan, this is data-driven substrate content. Adding support for Mistral / Qwen / Phi families = bootstrap their own vendor source entities + attestations. The Llama bootstrap is the worked example per ADR 0043 generalization principle.

### Stream C — Linguistic-ladder decomposers

**What's there.** `Laplace.Decomposers.Unicode` exists; ingests UCD perfcache + T0 codepoint entities. Per `~/.claude/plans/precious-noodling-anchor.md`: UnicodeDecomposer emits `attestationCapacity: 0` — ~40 attestation kinds defined but never emitted. ISO/WordNet/OMW/UD/Wiktionary/Tatoeba/Atomic2020/ConceptNet decomposers do not exist as C# projects yet.

**What's needed.** The precious-noodling-anchor plan (one of the 8 plans in the graveyard) is a complete spec for this stream — 8 chunks (DDN-0 through DDN-8) covering shared `TextEntityBuilder` + `AttestationFactory` + `LanguageEntityId` + `SourceEntityIdConventions` + `LexicalDecomposerBase` + `RelationTripleDecomposerBase` foundation plus per-source decomposer projects. Chunk DDN-0 has new engine function `trajectory_build_rle` and P/Invoke binding. Chunks DDN-1 through DDN-8 are the 9 decomposers. Each chunk has acceptance criteria + verification commands.

**Status of precious-noodling-anchor's foundation chunk in current tree.** `TextEntityBuilder` exists (`app/Laplace.Decomposers.Abstractions/TextEntityBuilder.cs`). `AttestationFactory` exists (`app/Laplace.Decomposers.Abstractions/AttestationFactory.cs`). The dirty diff added helpful static helpers to TextEntityBuilder (Resolver / TryDecomposeRoot / TryBuildRows) that should be preserved through Stream A's stash. `trajectory_build_rle` — to verify in `engine/core/src/trajectory.c`.

**Order.** This stream is parallel to Stream A/B. Per Anthony's commercial strategy memory the model semantic-ingest/synthesis path is M1 (revenue path), but cross-source consensus requires linguistic-ladder sources too (M3). DDN-1 (UnicodeDecomposer attestations) is the lowest-leverage-highest-clarity start — it wires up kinds that are already defined, against data already in `/vault/Data/Unicode/`.

### Stream D — Cross-modal decomposers

**What's there.** Nothing for Image, Audio, Video, or beyond-text Code. The infrastructure assumes Universal T0 codepoints — per GLOSSARY:44-46 + ADR 0040 every modality bottoms at the same codepoint hash space ("pixels are entities, audio is entities, text is entities") — but no `ImageDecomposer` / `AudioDecomposer` / `VideoDecomposer` projects exist.

**What's needed.** Per ADR 0043's composite-decomposer pattern + ADR 0040's Universal T0. ImageDecomposer = `ContainerFormat<PNG/JPEG/WebP/...>` × `ColorSpaceDecoder<sRGB/AdobeRGB/...>` × `ModalityBinder<Pixel/Patch/Region>`. AudioDecomposer similar. Each modality's entity ladder above T0 is type-specific (image: T1 pixel → T2 patch → T3 region → T4 image; audio: T1 sample → T2 frame → T3 track; etc.). Cross-modal entities are still substrate entities; cross-modal attestations (DEPICTS / CAPTIONS / TRANSCRIBES_AS) fall out of the matchup-space iteration in multimodal models per ADR 0056:185-205.

**Order.** This stream depends on Stream C's `LexicalDecomposerBase` + `RelationTripleDecomposerBase` pattern being firm. Reasonable to defer until Stream C is shipping seed sources.

### Stream E — Compiled cascade A* inference

**What's there.** Engine primitives `astar_query_t`, `glicko2_effective_mu`, cascade frontier management exist in `engine/core/` (per DESIGN.md II.B + ADR 0035). The SQL surface (one SRF call entering C/C++ frontier management per R19 + ADR 0035) — to verify in `extension/laplace_substrate/sql/`. Per DESIGN.md:618-628 the common attestation-expansion SQL pattern is documented.

**What's needed.** Per R19 + ADR 0035 + DESIGN.md V: the compiled SRF is the substrate's query surface. The cascade A* heuristic h() formula + the effective-mu combination formula are explicit "open tuning decisions" per DESIGN.md X — they're bounded engineering decisions Anthony specifies at execution time, not architectural unknowns.

**Order.** Depends on Stream C producing enough attestation data for cascade traversal to be meaningful. The cascade runtime is M4 in Anthony's commercial strategy ladder — deferred until M1-M3 ship.

### Stream F — IProtocolEndpoint plugins

**What's there.** `IProtocolEndpoint` interface defined at `DESIGN.md:679`. No implementations yet (`Laplace.Endpoints.*` projects do not exist).

**What's needed.** Per ADR 0011 polymorphic plugin pattern: one project per protocol. `Laplace.Endpoints.OpenAICompat` (chat completions / completions / embeddings endpoints translating to cascade queries + substrate writes for ingested prompts). `Laplace.Endpoints.AnthropicCompat`. `Laplace.Endpoints.Cohere`. Each is a thin protocol translator over the cascade SRF (Stream E).

**Order.** Depends on Stream E. M4-tier per commercial strategy.

### Stream G — IFormatWriter implementations beyond GGUF

**What's there.** `engine/synthesis/src/gguf_writer.cpp` (GGUF — proof/compat per RULES R4). Native safetensors-style writer per RULES R4 — to verify in `engine/synthesis/src/format_writer.cpp`. ADR 0059 specifies the format-writer emission matrix.

**What's needed.** Per ADR 0059 (to read): the matrix of which output formats are supported for which architecture families. Native is substrate's safetensors-style package per R4 + GLOSSARY:431. GGUF is conventional-ecosystem proof. ONNX / TensorFlow SavedModel / PyTorch are additional compatibility targets per ADR 0059's matrix.

**Order.** Largely independent. Each format writer is a self-contained plugin. Ships when the native package is correct (Stream B).

### Stream H — Documentation, issue, plan rot cleanup

**What's there.** 8 plan files in `~/.claude/plans/` (only 2 read this session: `wise-kindling-seahorse.md` + `precious-noodling-anchor.md`). 80+ open GitHub issues, many under retired chunk-sequence model (ADR 0060 retired chunks; issues still title themselves "Chunk N" / "Story N.M" / "Epic X"). 7 ADRs marked `(acceptance)` and open. ADR 0056 amendments needed per its own consequences section (R3 third-pass reframing; R8 GPU exception stale; GLOSSARY "Probe" entry stale; ADR 0036/0037/0043/0044/DESIGN VII probe-framing). Per R18 doc-currency: code changes need accompanying doc updates in the same commit; this rot accrued because that rule was violated repeatedly.

**What's needed.** Triage all 8 plans against current code reality (which sections shipped, which are stale, which superseded). Close zombie issues that ADR 0060 retired. Update or close ADR (acceptance) issues whose work is done or whose ADR is amended. Land the ADR 0056 amendments listed in its own Consequences section.

**Order.** Parallel to all other streams. Ships incrementally as each other stream resolves its corresponding doc debt.

## Stream dependencies and recommended order

```
Stream A (revert rot)          ──┐
                                  ├──→ Stream B (model ingest/synthesis rebuild — needs Anthony grounding)
Stream H (doc/issue cleanup)   ──┘                                                           │
                                                                                              │
Stream C (linguistic ladder)   ──┬──→ Stream D (cross-modal)                                  ├──→ Stream E (cascade)
                                  └──→ enriches Stream B (cross-source consensus)             │           │
                                                                                              │           │
Stream G (format writers)      ────→ Stream B (uses native package for any writer)            │           │
                                                                                              ↓           ↓
                                                                                       Stream F (endpoints)
```

Recommended immediate action: **Stream A (revert) + Stream H first chunk (close zombie chunk-sequence issues per ADR 0060) in parallel**, both today, in the same session. Stream B starts in conversation with Anthony before any code lands. Stream C starts after Anthony confirms the corrected attestation shape (which also unblocks Stream B). Streams D-G follow per the dependency graph.

## Critical files (Stream A — what changes today)

To delete (the pseudoinverse pipeline):
- `engine/synthesis/include/laplace/synthesis/reconstruct_w.h`
- `engine/synthesis/src/reconstruct_w.cpp`
- Update `engine/synthesis/CMakeLists.txt` to remove `src/reconstruct_w.cpp` from `LAPLACE_SYNTHESIS_SOURCES`

To excise (functions, leave file):
- `ReconstructWFromTokenPairAttestations` + `ReconstructQkFromTokenPairAttestations` from `app/Laplace.Engine.Synthesis/NativeInterop.cs`
- `BuildSubstrateAdjacencyAsync`, `BuildOutputDirectionAdjacencyAsync`, `ComputeSpectralEmbedding`, `ReconstructInteriorTensorSymmetric`, `ReconstructInteriorTensorAsymmetric` from `app/Laplace.Cli/Program.cs`
- `HfToGgmlName` from `app/Laplace.Cli/Program.cs:681+`
- `SynthesizeTinyLlamaAsync` body — replace with stub that returns `Fail("synthesis pending Stream B rebuild — see /home/ahart/.claude/plans/replicated-hatching-stream.md")` until Stream B lands
- Embed/lm_head PROJECTION/PROJECTION_OUTPUT emission in `app/Laplace.Decomposers.Model/LlamaWeightExtractor.cs:110-135`
- The token×token bilinear emission for V/O/GATES/UP/DOWN in `LlamaWeightExtractor.cs` (wrong shape — it does GEMM over vocab² at ingest, the disease per docs/SUBSTRATE-FOUNDATION.md truth 1). The rebuild must stream O(params) over the already-computed weight cells; the exact reduction that resolves these interior tensors to token entities is **OPEN per docs/SUBSTRATE-FOUNDATION.md** (do not lock a `spec.math_function`/text×text-matchup shape as settled).

To restore (kinds the prior commit wrongly removed):
- `app/Laplace.Decomposers.Model/ModelDecomposer.cs:40-50` — add back `EMBEDS`, `K_PROJECTS`, `NORMALIZES`, `OUTPUT_PROJECTS` kind hashes
- `app/Laplace.Decomposers.Model/ModelDecomposer.cs:122-127` (or wherever boot.AddKind is called) — register the 4 restored kinds in bootstrap

To stash (R24 — preserve, do not delete):
- The 7-file uncommitted diff. `git stash push -m "stream-a-pre-revert-preservation"` — recoverable for Stream B.

To leave alone:
- `engine/dynamics/src/eigenmaps.cpp` — `laplacian_eigenmaps_from_sparse_graph` may have legitimate uses elsewhere (physicality alignment per GLOSSARY:294). Stream B will determine whether the synthesis-side call sites get rebuilt or removed.
- `engine/synthesis/src/arch_template.cpp` — read but not modify in Stream A; Stream B rewrites against the corrected understanding.

## Verification (Stream A acceptance)

1. `cmake --build build` zero errors zero new warnings
2. `dotnet build app/Laplace.slnx -c Release` zero errors zero new warnings
3. `just db-nuke && just db-up && just seed-t0 && just ingest-tinyllama` succeeds; substrate has model recipe + tokenizer + token entities + the restored kinds bootstrapped, but no model semantic-ingest attestations (since the wrong-shape emission has been removed and the right-shape emission has not been built yet)
4. `just synthesize-tinyllama` exits with the documented "pending Stream B" error, not a crash
5. `git stash list` shows `stream-a-pre-revert-preservation` is recoverable
6. The tree is in a state from which Stream B can build correctly without untangling pseudoinverse residue

## What this plan does NOT do

- Does not implement Stream B (model ingest/synthesis rebuild). The implementation math needs Anthony's grounding (which I have repeatedly hallucinated in this session) before any code can be written without adding more rot.
- Does not implement Streams C-G. Each is its own multi-session arc with concrete sub-plans (precious-noodling-anchor for C is the most mature).
- Does not address every documented divergence between code and design. It addresses the model ingest/synthesis rot specifically because that's the one stream that's actively making things worse with each commit; the other streams' divergences are missing-features, not actively-wrong-features.
- Does not propose new architecture. Every decision in Stream A cites an existing document or commit.
- Does not depend on LLM-agent summaries. The agent-spawn attempt earlier in this session was deferral; this plan is grounded in documents I read myself with file:line cites.
