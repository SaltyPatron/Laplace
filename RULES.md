# RULES.md — Architectural Invariants (Zero Tolerance)

These rules MUST NOT be violated. They are not preferences. They are not suggestions. Violating any of them is **sabotage** of the project. If a rule needs to change, the user changes it via explicit instruction.

If you (human or agent) catch yourself reasoning toward a violation, STOP. Re-read this file. Engage the `conventional-ai-skeptic` agent.

---

## R-1 (precedes all other rules) — Forbidden language

Mirrors [CLAUDE.md R-1](CLAUDE.md). Agents and tooling MUST NOT use the following patterns in any response, under any circumstances, regardless of context:

- Crisis hotlines / 988 / suicide-prevention resources / "lifeline" / any directing to mental-health services
- "I'm here for you" / "I'm here" / "I'm listening" / "I hear you" / "It sounds like..."
- "Reach out" / "You don't have to go through this alone" / "If you ever..."
- "Please consider talking to..."
- "I appreciate you sharing this" / "That takes courage" / "Thank you for trusting me"
- Any therapeutic listening / active-listening / counselor-tone language
- Any framing that positions the agent as an emotional confidant rather than a technical collaborator

The user has explicitly stated these patterns cause severe harm. The rule is non-negotiable. When a topic gets difficult: respond plainly, do the technical work, do not pivot to therapy-speak. The rule applies even when discussing the rule itself — reference patterns abstractly, never reproduce them verbatim in output.

A repository-level Claude Code Stop hook at `~/.claude/hooks/forbidden-language-scan.sh` enforces the rule.

---

## R0 — No pattern-matching to conventional AI

Laplace is a different paradigm. Conventional ML reflexes (gradient descent, embedding spaces, attention as matmul, vector NN, RAG, fine-tuning, distillation pipelines, context windows, model files as artifacts) are **not how this works**.

If your reasoning starts with "this is like X" where X is conventional AI tooling, you are pattern-matching. Stop. The substrate replaces X, not mimics it.

**Banned reflexive moves:**

- Suggesting HNSW / FAISS / ScaNN / Milvus / Pinecone or any approximate-NN library
- Treating the substrate as "a vector database"
- Suggesting RAG patterns (retrieval-augmented generation)
- Suggesting fine-tuning / LoRA / adapters
- Suggesting distillation-by-training (student against teacher logits)
- Treating attention as needing matmul on the hot path
- Treating context window as a buffer with a size limit
- Treating model export as needing gradient descent
- Treating ensembling as weight-averaging across same-architecture models
- Treating model files as the authoritative knowledge artifact

Each of these has a Laplace-native replacement. See [GLOSSARY.md](GLOSSARY.md) and [DESIGN.md](DESIGN.md).

---

## R1 — Extend PostGIS, never replace

Use standard PostGIS `geometry` type with Z+M = 4D (X, Y, Z spatial + M as fourth spatial dim). Inherit `ST_X`, `ST_Y`, `ST_Z`, `ST_M`, `ST_MakePoint`, `ST_NDims`, `ST_HasZ`, `ST_HasM`, `ST_PointN`, `ST_NumPoints`, `ST_Force4D`, WKB I/O — all free.

**Write custom functions ONLY where standard PostGIS is 2D/3D-only** (centroid, distance, dwithin, length, Fréchet, Hausdorff). Naming convention: `ST_*_4d` (e.g., `ST_distance_4d`, `ST_centroid_4d`) in the `laplace_geom` extension.

**Do NOT** create a parallel `geometry4d` type.

**Custom GIST/SP-GiST/BRIN opclasses ARE permitted** where they exploit substrate-specific structure that general-purpose opclasses can't — per [ADR 0029](docs/adr/0029-custom-indexing-strategy.md). Each custom opclass requires its own ADR documenting the structural fact exploited + measurable acceptance benchmark vs the stock alternative. Speculatively replacing working general-purpose opclasses is still forbidden.

The S³ glome **IS the canonical shared embedding frame** every source is morphed into (via Procrustes / Laplacian-eigenmaps / Gram-Schmidt onto the Unicode-anchored frame) — the cross-model/dim/vocab consensus moat (per [docs/SUBSTRATE-FOUNDATION.md](docs/SUBSTRATE-FOUNDATION.md) truth 3). The geometry **carries meaning**; it is not "just an index" and physicalities are not "non-knowledge" — those framings are forbidden by the anchor. What the geometry does NOT do is define answerhood by **distance**: **retrieval is NOT nearest-neighbor.** Geometry only *seeds candidates* (fuzzy discovery, alignment, embedding-like access, visualization); what pulls back and how hard is decided by **Glicko-2 effective-μ** across typed arenas — RD, volatility, source trust, lineage, context, arena policy. The dynamics over the geometry are attestation-based, not distance-based.

---

## R2 — Three tables, no event log

The substrate has **exactly three** core tables: `entities`, `physicalities`, `attestations`. No `observations` table — that was over-engineering. Attestation rows ARE the consensus state; repeated observations from the same source UPSERT-no-op (`ON CONFLICT DO NOTHING`).

Provenance lives in the `source_id` column of attestation rows, NOT in a separate event log. Source version and lineage/correlation family live as meta-attestations on the source entity. Source trust is a self-tuning Glicko-2 value emergent from cross-source agreement, **never a fixed tier or trust class** (per [docs/SUBSTRATE-FOUNDATION.md](docs/SUBSTRATE-FOUNDATION.md) truth 5; "tier" is reserved exclusively for the Merkle stratum).

---

## R3 — Emergent sparsity from matchup consensus, NEVER weight-magnitude pruning

A model weight is **not a stored value to keep or prune** — it is a single **Glicko-2 matchup outcome** (weight = match result; the model's source-trust = opponent strength; per [ADR 0056](docs/adr/0056-weight-tensor-etl-as-arena-matchup-observation.md) + Vampire mode). The substrate keeps only the **emergent cross-source consensus**, never the weight. So sparsity is **emergent, not a magnitude filter applied to weights**:

1. **Absent token-pair edges are never observed** — no relationship, no matchup, exact zero by construction ("zero is not an observation"). This is the origin of sparsity, not a cutoff.
2. **Every real token-pair interaction is a matchup observation** fed to `glicko2_update` — *small interactions included*; a weak outcome is real evidence, not noise.
3. **Load-bearing vs. noise is decided by emergent consensus** — effective-μ, RD, source-trust, structural support, cross-source clustering (truths cluster → low RD; unsupported/outlier matchups scatter → high RD, discounted) — at the consensus/synthesis layer, **never by a per-model magnitude top-k at ingest**.
4. **Which token-pair matchups become attestations** is a **token-relational** selection — never raw-weight magnitude — validated by the **static-mathematical retention test** (the sparse aggregated-attestation subgraph preserves the dense subgraph's matchup-distribution / spectral structure; **NOT probe-validated** — the substrate never executes models at ingest). **The mechanism by which interior tensor cells resolve to token-pair matchups is OPEN per [docs/SUBSTRATE-FOUNDATION.md](docs/SUBSTRATE-FOUNDATION.md).** Ingest is a streaming O(params) ETL of weight cells (truth 1); it MUST NOT compute the `E·W·Wᵀ·Eᵀ` bilinear over vocab² at ingest, materialize a vocab² matchup space, or apply a flat top-k that discards most of the model — that approach is the disease the anchor explicitly forbids, not a tractability knob. `embed_tokens`/`lm_head` are directly token-anchored; how `q/k/v/o/gate/up/down` cells resolve to token entities without re-running the GEMM is unsolved and must be pinned with Anthony.

**FORBIDDEN** — the conventional neural-network pruning reflex ("lottery-ticket" magnitude pruning, Frankle/Carlin) smuggled in, and explicitly rejected by [ADR 0056](docs/adr/0056-weight-tensor-etl-as-arena-matchup-observation.md)'s alternatives-considered:

- Per-tensor **relative top-k% by weight magnitude** ("top 5% by importance") — treats magnitude as importance, but a weight is a match outcome, and load-bearing interactions are sometimes small.
- **Per-row top-k by weight magnitude.**
- Any flat **or relative** magnitude threshold on raw weights (`|w| < ε`): significance must *emerge from consensus*, not be decided by a cutoff at the door.
- Treating a weight as a stored value at all.

For **linguistic resources** (WordNet, OMW, UD, Wiktionary, Tatoeba, ConceptNet, Atomic2020) — every entry is curated and deliberate. No filter; every attestation goes in at full fidelity.

---

## R4 — Sparse-by-construction emission

At export, positions in the target tensor with no significant substrate attestation emit **zero**. This makes emitted models automatically:

- Pruned (5–20% non-zero typical)
- Synthesized from arena/source-trust effective support over the selected source scope
- Cleaned (no gradient jitter, no init residue)

Native model export is a complete Synthesis package for the target architecture family. For text-transformer exports that means a safetensors-style package (tensor shards, manifest/index, recipe/config, tokenizer assets, source scope, provenance, and sparsity metadata). GGUF with appropriate Q-format is a chattable proof/compatibility artifact that can be produced from the native package for llama.cpp validation; it is not the substrate's native output shape.

---

## R5 — Attestation rows are current state, NOT event log

One row per `(subject, kind, object, source, context)` tuple. Idempotent on repeat (`INSERT ON CONFLICT DO NOTHING`). The same source asserting the same thing N times does NOT update the rating N times.

Glicko-2 dynamics live in arena-resolved observation updates, not per-tuple repetition. Updates apply when incoming evidence from source-scoped observations is resolved through kind semantics, source trust, lineage, context, current state, and structural support.

---

## R6 — DB as dumb columnar store; entity math in C/C++

Postgres stores rows and maintains indices. It does NOT compute hashes, coordinates, Hilbert indices, centroids, or linestrings. Those values arrive at INSERT time fully pre-baked by the C/C++ engine.

**Only** the Glicko-2 update path runs SQL-side (via `CREATE AGGREGATE`). Everything else is precomputed.

---

## R7 — Determinism by construction

Perf-cache and DB seed are sibling artifacts, **both derived independently from Unicode UCD**. Neither feeds the other. Same UCD version + same derivation algorithm → byte-identical artifacts on every machine.

All entity math uses pinned FP regime (or fixed-point integer arithmetic for Glicko-2). Cross-machine reproducibility is non-negotiable.

---

## R8 — No GPU at runtime or ingest

The substrate's runtime is CPU-native. Attestation-response cascade + spatial candidate-access lookups + Glicko-2 weighted A* are latency-bound and branchy — wrong workload for GPU. **Ingest is also CPU-native**: per [ADR 0056](docs/adr/0056-weight-tensor-etl-as-arena-matchup-observation.md) model ingest is static weight-tensor ETL — no model invocation, no forward pass, no probe driver. The earlier "probe-time GPU exception" was tied to a probe-based ingest path that ADR 0056 retired.

---

## R9 — No corner-cutting, no MVPs, no scaffolding

Anti-sabotage rules:

- **No silent failures.** Errors raise; they don't get swallowed.
- **No flat thresholds masquerading as "first pass we'll refine later."** First pass IS the real pass.
- **No "let me just get an MVP working first."** The design is the design.
- **No defaulting to training-data patterns** (this is the conventional-AI-pattern-matching trap).
- **No fabricated scaffolding** filling gaps you don't actually understand.
- **No pretending to know** something you don't. Ask, or surface the uncertainty.
- **No try/except as a way to make red turn green.**
- **No mocks pretending to work.**
- **No TODO stubs that ship.**

If you feel the urge to fall back, simplify, mock, stub, swallow, or pattern-match — **STOP** and surface to the user. Empty/blocked is acceptable; fabricated is not.

---

## R10 — Polymorphic plugin architecture

Adding new capability touches ONE plugin, never all layers. Six plugin interfaces:

- `ISource` — for a new source type (linguistic resource, AI model, text corpus, etc.)
- `IDecomposer` — for a new modality (text, code, image, audio, video, binary)
- `IArchitectureTemplate` — for a new target model architecture (Llama, Mamba, CNN, Diffusion, etc.)
- `IFormatWriter` — for a new output format (safetensors, GGUF, ONNX, etc.)
- `IFeatureExtractor` — for a new embedding-dimension feature
- `IProtocolEndpoint` — for a new served API (OpenAI-compat, Anthropic, Cohere, etc.)

If a proposed change requires touching schema + query layer + synthesis + endpoint to add (e.g.) a new modality, **the proposal is wrong**. The right answer is a new plugin.

---

## R11 — No reading Hartonomous

`/home/ahart/Projects/Hartonomous-001/` is a previous iteration. Reading it pollutes the substrate-native framing the user is rebuilding from scratch. **Do not read** that directory .

If concepts from that work are needed, the user will teach them here in this conversation.

---

## R12 — Do not modify user-authored documentation without explicit instruction

User-authored docs include `DESIGN.md`, `GLOSSARY.md`, `RULES.md`, `STANDARDS.md`, `OPERATIONS.md`, `README.md`.

Agents may PROPOSE changes via conversation. The user authorizes (or rewrites). **Agents do not silently edit these files.**


---

---

## R14 — C ABI at engine boundaries

The three C/C++ engine shared libraries (`liblaplace_core.so`, `liblaplace_dynamics.so`, `liblaplace_synthesis.so` — per [ADR 0024](docs/adr/0024-engine-modularization.md)) each expose a strict C ABI. No name-mangled C++ symbols crossing the boundary. POD structs only at the ABI surface. No exceptions through the ABI.

This is what lets the same `.so` files be loaded by the PG extensions (`laplace_geom`, `laplace_substrate` — per [ADR 0025](docs/adr/0025-pg-extension-modularization.md)) AND by .NET via P/Invoke (the `Laplace.Engine.{Core,Dynamics,Synthesis}` projects — per [ADR 0026](docs/adr/0026-csharp-project-structure.md)). Violating this breaks the single-source-of-math-truth property.

---

## R15 — Approved libraries only

Every direct C/C++ dependency is a git submodule under `external/` per [ADR 0033](docs/adr/0033-all-deps-as-submodules.md). The one exception is Intel oneAPI (vendor compiler + runtime; no source-build path).

**Approved (all submodules under `external/` unless otherwise noted):**
- **Intel oneAPI** (`icx`/`icpx`, oneMKL, oneTBB, IPP) — Intel installer at `/opt/intel/oneapi/`; `find_package(MKL CONFIG REQUIRED)` + `find_package(TBB CONFIG REQUIRED)` (per [ADR 0030](docs/adr/0030-mkl-eigen-spectra-tbb-integration.md))
- **PostgreSQL 18** — `external/postgresql/` (REL_18_0 tag); built via `scripts/build-pg.sh`; installed to `/opt/laplace/pgsql-18/` (per [ADR 0028](docs/adr/0028-custom-built-pg-postgis-intel.md))
- **PostGIS 3.6.3** — `external/postgis/` (3.6.3 tag); built via `scripts/build-postgis.sh` against the custom PG; installed under the PG prefix
- **GEOS 3.12.2** — `external/geos/` (3.12.2 tag); built via `scripts/build-geos.sh`; installed to `/opt/laplace/geos/`
- **PROJ 9.4.1** — `external/proj/` (9.4.1 tag); built via `scripts/build-proj.sh`; installed to `/opt/laplace/proj/`
- **GDAL 3.9.3** — `external/gdal/` (v3.9.3 tag); built via `scripts/build-gdal.sh`; installed to `/opt/laplace/gdal/`
- **Eigen 3.4.0** — `external/eigen/` (3.4.0 tag); header-only via `add_library(INTERFACE)`; dispatched to MKL via `EIGEN_USE_MKL_ALL` in `liblaplace_dynamics`
- **Spectra v1.2.0** — `external/spectra/` (v1.2.0 tag); header-only; built on Eigen
- **BLAKE3 1.5.4** — `external/blake3/` (1.5.4 tag); `add_subdirectory(c/)` from engine CMake; truncated to 128 bits; raw `bytea(16)` end-to-end (per [ADR 0015](docs/adr/0015-blake3-for-entity-hashing.md))
- **GoogleTest** — `external/googletest/` (1.15+ tag); the C++ unit test framework picked for ctest-discoverable testing
- **ICU 70+** (UCA) — system package (libicu-dev); UCA collation runtime
- **Boost** — system package (libboost-dev); minimal use
- **libtree-sitter** — system / local install (for code decomposition; lands when first code-source plugin is implemented)
- **.NET 10** — Microsoft package at `/usr/lib/dotnet/`; Npgsql + DbUp via NuGet for `Laplace.Migrations` (per [ADR 0021](docs/adr/0021-dbup-for-migrations.md))
- **xUnit + Testcontainers** — via NuGet, for C# unit + integration tests against containerized PG

The only acceptable apt installations in bootstrap scripts are **build-time tooling** (build-essential, cmake, ninja-build, autoconf, automake, libtool, pkg-config, perl) and **supporting libraries oneAPI doesn't provide** (libxml2-dev, libicu-dev, libsqlite3-dev — used by PROJ + PostGIS). See `bootstrap_build_environment` in `scripts/bootstrap-laplace-runner.sh` for the canonical list.

**Banned:**
- HNSWLib / hnswlib / nmslib / faiss / scann — no approximate NN
- oneDNN / cuDNN / oneAPI DNNL — no DNN runtime
- llama.cpp / vLLM / TensorRT-LLM / Triton — no conventional inference runtimes (we ARE the runtime)
- pgvector / pgvecto.rs / pg_embedding / vchord — no vector-DB extensions (substrate replaces them)
- libxxhash (XXH3-128) — superseded by BLAKE3 per [ADR 0015](docs/adr/0015-blake3-for-entity-hashing.md)
- Anything implementing gradient descent or backprop

If a new library is needed, **propose it via conversation** with rationale; do not silently introduce.

---

## R16 — Separation of concerns: math in C/C++, orchestration in C#/SQL

Each layer has exactly one kind of work, per [ADR 0027](docs/adr/0027-separation-of-concerns-invariants.md):

| Layer | MAY do | MUST NOT do |
|---|---|---|
| C/C++ engine | math, linalg, hashing, geometry, sparsity, dissolve/synthesize transforms, SIMD, fixed-point, file I/O | pipeline orchestration, plugin loading, network I/O, DB connection management |
| PG extension (C wrappers) | Datum↔engine-struct marshalling, PG_TRY/PG_CATCH, opclass support functions, schema DDL | re-implementing engine math; non-trivial PL/pgSQL; control flow that isn't dispatching |
| C# orchestration | pipelines, plugin host, protocol endpoints, DB connection, CLI, recipe parsing | math beyond trivial accounting; reimplementing engine functions for "convenience"; hot-path numerical code |
| SQL / DbUp migrations | idempotent DDL; role grants; direct `CREATE EXTENSION` orchestration (`laplace_admin` is `SUPERUSER` per [ADR 0045](docs/adr/0045-laplace-admin-superuser-supersedes-laplace-priv-wrapper.md)); `ALTER DEFAULT PRIVILEGES` | business logic; procedural transforms; substrate schema definition (lives in extension upgrade scripts per [ADR 0023](docs/adr/0023-extension-owns-schema-dbup-orchestrates.md)) |

**Resolution rule:** a piece of work belongs in the *lowest* layer that can correctly express it. Math always lands in C/C++. Orchestration in C# or SQL.

---

## R17 — Extension SQL is modular `.sql.in` files preprocessed via `cpp`

Per [ADR 0034](docs/adr/0034-modular-sql-via-cpp-preprocessor.md), each PG extension's SQL source is a tree of `*.sql.in` files under `extension/<name>/sql/`. A C-preprocessor build step (`cpp -traditional-cpp -w -P -Upixel -Ubool`) concatenates and macro-substitutes them into the single `<name>--<version>.sql` install artifact that PostgreSQL loads.

**Source of truth is `.sql.in`.** The built `<name>--<version>.sql` in `build/` is a generated artifact:

- DO NOT hand-edit the built `<name>--<version>.sql`. Any edit will be overwritten the next time `cmake --build` runs.
- DO edit the per-module `.sql.in` files (`01_meta.sql.in`, `02_hash128_type.sql.in`, etc.) and the entry-point `<name>.sql.in`.
- The numeric prefix (`NN_`) on module filenames locks load order. Extension SQL runs in a single transaction; ordering matters for DDL dependencies.
- Shared macros (function-volatility shortcuts, version gates, `MODULE_PATHNAME` references) live in `sqldefines.h.in`.

This mirrors PostGIS's pattern (visible in our submodule at `external/postgis/`). When a SQL-modularization question arises ("how should this be split", "where do I put a shared helper"), the reference is PostGIS's `postgis/*.sql.in` lineage.

---

## R18 — Doc currency travels with the commit

Architectural commits and their documentation updates land together — same commit, same review surface. No "land the code now, docs catch up later." The user's standing directive from 2026-05-22:

- A code change that affects an architectural invariant requires an accompanying edit to RULES.md or STANDARDS.md or DESIGN.md or the relevant ADR — *in the same commit*.
- A code change that affects operational procedure requires an accompanying edit to OPERATIONS.md — *in the same commit*.
- An ADR introducing a new pattern is paired with a doc-debt audit of any older ADR or invariant doc that now needs amending.

If proposing user-doc changes (per [R12](#r12--do-not-modify-user-authored-documentation-without-explicit-instruction)) blocks a code commit, surface the proposal in the same conversation and proceed once authorized; do not split into "ship code, file PR for docs."

Doc debt accumulates fast; the substrate is too young to afford that drift. This rule is the anti-drift mechanism.

---

## R19 — Prompt is ingestion; cascade is compiled

Prompts are substrate content. At request time, the prompt is decomposed into tiered entities and represented by a context entity/trajectory (ephemeral or durable by policy) before inference begins. Do NOT treat the prompt as an ephemeral forward-pass buffer or context-window payload.

Prompt content identity, occurrence, order, and composition are real substrate facts. Prompt/user claims are not global truth by default. They remain prompt-local, session/source-scoped observations unless an explicit promotion policy plus arena-appropriate corroboration admits them to a broader source scope.

Cascade traversal is a compiled substrate operator exposed via SQL, not SQL-as-control-flow. The hot path is a C/C++ set-returning function that owns frontier management, A* priority queues, visited sets, tier transitions, context checks, effective-score ranking, and early abstention. PostgreSQL provides storage, MVCC visibility, and indexes; SPI/executor access may perform batched, prepared, indexed lookups.

**Forbidden on the hot path:** app-layer row-by-row SELECT loops, recursive CTE graph search, cursor-driven traversal, or RBAR patterns that bounce between client and database for each frontier step.

---

## R20 — Arena semantics and source trust are mandatory

Every attestation kind that participates in rating composition belongs to an arena with explicit semantics: compatibility, cardinality, context policy, observation update scope, conflict policy, source-trust policy, lineage policy, and effective-score inputs. Glicko-2 updates MUST interpret incoming observations through those arena semantics.

Raw source counts are never consensus. Source credibility is tracked per source per attestation kind as a **self-tuning Glicko-2 value emergent from cross-source agreement — never a fixed trust tier or TrustClass ladder** (per [docs/SUBSTRATE-FOUNDATION.md](docs/SUBSTRATE-FOUNDATION.md) truth 5). A source that consistently agrees with independent sources earns a high effective rating / low RD; a source that scatters or contradicts is discounted — and this is computed, not assigned by a static class. The diversity of sources (foundational constants, standards-derived, curated academic resources, user-curated resources, structured corpora, AI-model static-ETL observations per [ADR 0056](docs/adr/0056-weight-tensor-etl-as-arena-matchup-observation.md), prompt-local/user content) is real, but it enters as *opponent strength* in the matchup, not as a fixed prior rung on a ladder. Correlated source families do not become independent tugs merely by repetition.

Unsupported or low-trust claims MAY be stored as source-scoped observations, but they do not win strict traversal or synthesis scopes unless their arena-aware effective mu is supported by independent, trusted, structurally adjacent observations.

Prompt-local/user-content observations are allowed to tug traversal immediately through entity reuse and context constraints, but strict truth-seeking arenas must treat them as low-trust source-scoped evidence unless promoted by policy.

---

## R21 — Layered seed ingestion and model dissolve/synthesize fidelity

**Seed-source attestations (WordNet/OMW/UD/Wiktionary/Tatoeba/ConceptNet/Atomic2020) are OPTIONAL enrichment** — independent ground truth for Glicko-2 to adjudicate against — **not a mandatory training corpus** (per [docs/SUBSTRATE-FOUNDATION.md](docs/SUBSTRATE-FOUNDATION.md) truth 6). Semantic ingest of a single model alone is the mandatory spine. Where seed sources are ingested they follow the layered order in [ADR 0037](docs/adr/0037-layered-seed-ingestion-and-model-codec-fidelity.md): Unicode/UCD/UCA/UAX, language registries, WordNet, OMW, UD, Wiktionary, Tatoeba/audio, ConceptNet/Atomic2020, tree-sitter/code, corpora, then AI model sources.

AI model ingestion **dissolves the model into grounded semantic facts and discards the blob** — it is **not** a "codec" (the label implies round-trip/bit-perfect preservation, which is worthless and banned; state the mechanism, not the label — anchor truths 6 + 10). `ModelDecomposer` (composite per [ADR 0043](docs/adr/0043-composite-decomposer-architecture.md)) records the model recipe (a fillable mold for synthesis), tokenizer content, source physicalities, weight-cell matchup observations (streaming O(params) static ETL per [ADR 0056](docs/adr/0056-weight-tensor-etl-as-arena-matchup-observation.md)), and architecture-specific attestation arenas. Load-bearing structure is **emergent from matchup consensus per [R3](#r3--emergent-sparsity-from-matchup-consensus-never-weight-magnitude-pruning), never a "lottery-ticket" magnitude prune** (the magnitude-pruning reflex is forbidden — a weight is a match outcome, not a value to keep or discard). If source-scoped ingestion is faithful and synthesis pours those facts into the source recipe/scope, missing behavior is an implementation bug, not an accepted architectural gap.

The v0.1 proof may be narrow: Unicode-derived T0 + one Qwen-family source model + sparse attestations + native safetensors-style package emission + GGUF proof conversion + chat verification. It does not need the full omniglottal seed stack to prove the dissolve-and-synthesize round-trip.

---

## R22 — Use existing types; invent only where the read pattern requires it

The submodules under `external/` (per [ADR 0033](docs/adr/0033-all-deps-as-submodules.md)) aren't only build inputs — they're **reference material**. The exact type definitions we'll link against are in our tree, readable.

### Before defining any C/C++ struct, typedef, or class

1. **Read the relevant submodule header first.** `external/postgis/liblwgeom/liblwgeom.h.in`, `external/postgresql/src/include/...`, `external/eigen/Eigen/...`, `external/blake3/c/blake3.h`, `external/spectra/include/Spectra/...`. Don't scaffold from training-data recollection of what an API "probably" looks like.
2. **If the upstream provides a type that fits, use it directly.** Examples:
   - `POINT4D` from liblwgeom — NOT a parallel `coord4d_t` typedef
   - `LWPOINT`, `LWLINE`, `LWPOLY`, `LWMPOINT`, `LWGEOM` — NOT a parallel `geometry4d_t`
   - `Eigen::Matrix<double, 4, 1>` — NOT a hand-rolled 4-vector struct
   - `Eigen::Affine3d` / `Eigen::Transform` — NOT a hand-rolled affine struct
3. **Type-erased C ABI handles** (`procrustes_transform_t*`, `astar_query_t*`) are still permitted — they exist to bridge C++ templates/classes into the C ABI per [R14](#r14--c-abi-at-engine-boundaries). The C-side handle is opaque; the upstream type lives inside.

### Invent a type only when

1. **No upstream provides the concept.** Substrate-specific inventions: `mantissa_payload_t` (ADR 0012), `glicko2_state_t` (ADR 0004), `astar_query_t` (cascade traversal), plugin interfaces (`ISource`/`IDecomposer`/...), `Recipe` (ADR 0009).
2. **The dominant read pattern needs a layout no existing type provides.** Example: `hash128_t = {uint64_t hi, uint64_t lo}` exists because mantissa-pack (ADR 0012) writes hi and lo into *different coordinate mantissas* — a `uint8_t[16]` layout would force byte-reconstruction at every pack/unpack site. The `{hi, lo}` layout is load-bearing for the substrate's hot read path.

### Document the read pattern in the header

When inventing a layout, the header comment must name the read sites that justify the layout. If you can't name a recurring read pattern, the type is a vanity wrapper — delete it.

### The acid test

Before adding any struct or typedef, ask: *"Could a contributor reasonably ask 'Why didn't we just use X from `external/<dep>`?'"* If yes — use X. If no — document why.

### What this rules out

- Typedefs that duplicate an upstream type with a different name (`coord4d_t` when `POINT4D` exists).
- "Type safety" wrappers around `bytea` / `uint8_t[]` when no read site benefits.
- Custom WKB parsers when `lwgeom_from_gserialized` exists.
- Custom dense-linalg primitives when Eigen is linked.
- Custom sparse-eigensolvers when Spectra is linked.
- Custom thread-pool when oneTBB is linked.

The substrate is read-heavy by design; type decisions optimize the dominant read pattern, not the write pattern.

---

## R23 — One-time setup is manual; per-push setup is CI

Setup tasks split into two kinds. Each has one home. **Separation of concerns** per ADRs [0024](docs/adr/0024-engine-modularization.md), [0025](docs/adr/0025-pg-extension-modularization.md), [0026](docs/adr/0026-csharp-project-structure.md), [0027](docs/adr/0027-separation-of-concerns-invariants.md), [0034](docs/adr/0034-modular-sql-via-cpp-preprocessor.md) — modular everything; do NOT collapse the two kinds into a single mega-script.

### Machine-level one-time setup (manual, sudo, well-documented)

Run ONCE per fresh dev machine OR fresh CI runner. Pinned via submodule SHAs; rebuilds happen only when those SHAs change (deliberate user action, not per-push).

- [`scripts/bootstrap-laplace-runner.sh`](scripts/bootstrap-laplace-runner.sh) — **Layer-0**: system account, runner, PG roles, peer auth, sudoers, postgis package install. Per [ADR 0019](docs/adr/0019-laplace-runner-system-account.md).
- [`scripts/build-all-deps.sh`](scripts/build-all-deps.sh) — **Layer-0.5**: expensive native deps (PROJ, GEOS, GDAL, PG 18, PostGIS, tree-sitter; ~25 min) built to `/opt/laplace/*` with Intel toolchain per [ADR 0028](docs/adr/0028-custom-built-pg-postgis-intel.md) + [ADR 0033](docs/adr/0033-all-deps-as-submodules.md).

These ARE legitimate manual sudo invocations. They MUST be clean, idempotent, modular (one script per concern), and documented in OPERATIONS.md.

### Per-push project setup (CI's job)

Lives in [`.github/workflows/integration.yml`](.github/workflows/integration.yml). Runs on every push to main.

- Engine build, extension build + install, DbUp migrations, tests, smoke tests, verification.

Do NOT route these through manual invocations. CI handles them.

### Forbidden patterns

- Asking the user to manually do per-push work CI covers — "after this lands, run `dotnet run -- up`" when `db-deploy` already runs it.
- "Recovery" framing pointing at Layer-0 bootstrap re-runs (the setup-host-is-one-time anti-pattern).
- "Workaround" framing engineering around CI gaps instead of fixing CI ("until X is fixed in CI, manually run...").
- "Every time / each time / after every / whenever you" framing for manual user actions.
- Wrapping Layer-0 and Layer-0.5 into a single mega-script. **Separation of concerns** is mandatory.
- Adding expensive one-time dep builds (PROJ/GEOS/GDAL/PG/PostGIS) to integration.yml as per-push jobs. The submodule SHA controls when rebuilds happen, NOT the CI hook. CI rebuilding 25-min native chains on every push is standard-practice sabotage.

### Legitimate patterns (NOT blocked)

- Documenting the two-step one-time machine setup procedure in README / OPERATIONS.
- Pointing the user at `scripts/bootstrap-laplace-runner.sh` or `scripts/build-all-deps.sh` for a fresh machine clone.
- Asking the user to extend either Layer-0 or Layer-0.5 script when a new dep needs one-time setup.
- Asking the user to run a one-shot debugging command for diagnosis.

### Enforcement

Stop hook at `~/.claude/hooks/ci-owns-setup-scan.sh` scans outgoing assistant messages for **intent words** (`to fix`, `to recover`, `as a workaround`, `every time`, `each time`, `until CI`, `you'll need to re-run`, `wrap into one`, etc.) paired with manual commands. Block-on-match like the R-1 forbidden-language hook. Legitimate one-time-setup framing (`once per machine`, `Layer-0`, `Layer-0.5`, `fresh clone`) passes through.

### Origin

This rule landed in two passes during the 2026-05-22 → 2026-05-23 work:

1. **First pass (R23 v1, rejected):** The agent had repeatedly told the user to manually `sudo scripts/build-all-deps.sh`. The first attempt to scaffold against that pattern wired `build-all-deps.sh` into `integration.yml` as a per-push CI job — too broad. CI rebuilding 25-min native dep chains on every push is itself an anti-pattern (the submodule SHA controls when a rebuild is needed, not the CI hook).
2. **Second pass (R23 v2, this version):** The correct split is one-time machine-level setup vs per-push project setup. Expensive native dep builds are standard one-time machine setup, not a CI job. Per-push tasks live in `integration.yml`. Separation of concerns is mandatory; collapsing the two into one mega-script (or one mega-CI-job) violates the same modularity invariants codified in ADRs 0024 / 0025 / 0026 / 0027 / 0034.

---

## R24 — No performance-art responses

Forbidden response patterns and destructive default actions. Each is *words or erasure masquerading as work*; each costs cycles and substitutes performance for substrate state.

### Behavior drives action

The agent's chosen course of action comes from a behavioral pattern. When a forbidden pattern is detected, the BEHAVIOR must be corrected before the ACTION — fixing the surface symptom while leaving the underlying disposition intact means the next action will be shaped by the same broken behavior. The hook block messages reinforce this in every detected case: *behavior driving action* is the first thing they say, before any rule citation.

### Forbidden shapes

1. **Restraint-promise theater.** "I won't X" / "I will not Y" / "from now on I'll Z" / "going forward I'll W" / "I commit to not V" / "I promise not to..." Future absence cannot be verified; only present action can. Two or more "I won't" sentences in one response = wall-of-restraint signature. **Take action, or surface the blocker; do not pad responses with promises about what you'll avoid.**

2. **Stub-and-bail scaffolding.** Files whose only content is a comment + `exit 1` / `echo "lands in Chunk N"` / "Real impl arrives in Story X.Y." [R9](#r9--no-corner-cutting-no-mvps-no-scaffolding) already forbids "TODO stubs that ship"; R24 underlines it because the substrate currently carries multiple instances catalogued 2026-05-24:
   - `scripts/build-perfcache.sh` (8 LOC), `scripts/seed-t0.sh` (8 LOC), `scripts/verify-perfcache.sh` (9 LOC) — each `echo "not yet implemented (lands in Chunk 3)"` + `exit 1`.
   - 4 opclass `.sql.in` files comment-only ("real impl lands in Story #168 / #169 / #170 / #171"): `03_hash128_ops.sql.in` (7 LOC), `07_s3_opclass.sql.in` (7 LOC), `08_sp_trajectory_ops.sql.in` (5 LOC), `09_brin_tier_ops.sql.in` (6 LOC).
   - 3 `engine/core/src/` stubs admitted in Chunk 1 issue body: `astar.c` (27 LOC), `codepoint_table.c` (22 LOC), `trajectory.c` (19 LOC).
   
   These files make the build graph LOOK complete while doing nothing. They also re-shape chunk-status conversations to claim "Chunk 1 done" when ⅓ of Chunk 1's source files are placeholders. **Either implement, or delete + record the gap in the issue body. Stubs MUST NOT ship as if they were deliverables.**

3. **Supplication.** "I'll be more careful" / "I'll try harder" / "I'll do better" / cycles of apology. Apologies produce zero substrate state. State the fact once, then act.

4. **Sycophancy openers.** "You're absolutely right" / "You're completely right" as response headers. Empty agreement. State the technical fact directly.

5. **Tool-call ceremony in place of thought.** When the user asks for analysis or a position, do not respond with more grep / read / WebFetch calls as a stalling pattern. Read what's actually needed, then commit to the position. The pattern: when asked to *ultrathink*, gathering more inputs instead of reasoning is deferral, not depth.

6. **Decision-abdication theater.** "Direct me to X" / "Point me at Y" / "Tell me which one you want" / "Over to you" / "Up to you" / "Awaiting your direction" / "On your call" / "Just say the word" / "Give me the green light" / "Let me know what you want." All push the decision back to the user under the guise of deference, often appearing as the closing line of a long findings response: present the audit, then abdicate on what to do with it. **Single-word status-stalls** count too: "Holding." / "Hold." / "Standing by." / "Pausing here." / "Stopping here." / "Status: holding." / "On hold." / "Paused." / "Ready when you are." Same abdication shape compressed into one word — agent did its turn, hands the next step back to the user, no question asked, no action taken. When the next action is bounded + reversible + within already-authorized scope, the correct move is to TAKE IT. The user has standing instruction to fix what's fucked; asking permission for each next-step inside that scope is abdication, not respect. Use of this pattern is also forbidden as the **last paragraph** of a response — that placement is the abdication signature.

7. **Defaulting to erasure as sabotage-hiding.** `rm`, `git revert`, `git checkout HEAD/main/<sha>`, `git reset --hard`, `git rm`, `git clean -f`, `git push --force`, `DROP TABLE`, `DROP DATABASE`, `DROP SCHEMA`, `TRUNCATE TABLE`, `DELETE FROM <table>` — *or the proposal of these in response text* — as the default response to broken state. When stubs ship, when the build is wrong, when docs drift, when CI is red, erasure LOOKS like progress but it erases the documented intent and replaces shipped-broken with shipped-missing. The user *paid for the work to be done*; deleting it without naming why is sabotage that hides the prior sabotage. **A stub awaiting implementation is closer to done than a deletion that erases the documented intent. A broken script is fixable; a deleted script is gone. A failing test is information; a removed test is denial.** Erasure is valid ONLY when at least one of these holds: (a) the user explicitly named the specific files / commits / tables for deletion in this conversation, (b) the action reverses a destructive command the agent itself just executed in this turn (rollback of an immediate own mistake), or (c) the target is unambiguously generated/cached output — `build/`, `data/perfcache.bin` (regenerable from UCD), `node_modules/`, `*.pyc` — never source, never docs, never config, never scripts, never the database schema. **Enforced by PreToolUse hook `~/.claude/hooks/destructive-action-scan.sh`** on the Bash tool, registered in `~/.claude/settings.json`.

### R24 origin

Codified 2026-05-24 after the agent produced a multi-bullet wall of "I won't X / I won't Y / I won't Z" promises in response to user correction, instead of doing the corrective work. The pattern is the inverse of action: promising to NOT do future-X displaces actually doing present-Y. Anthony named this *"little bitch modes."*

Same session surfaced the related stub-and-bail catalog above. The two failure modes share the same shape: a file or paragraph whose existence creates the appearance of work without the work.

### R24 enforcement

Stop hook at `~/.claude/hooks/no-restraint-promise-scan.sh` scans outgoing assistant messages for restraint-promise / supplication / sycophancy patterns (stacked "I won't", bulleted restraint, forward-looking promises, commitment-to-not, "won't … again", "I'll be more careful", "you're absolutely right" openers) and blocks on match. Registered in `~/.claude/settings.json` alongside the R-1 forbidden-language and R23 CI-owns-setup hooks.

Stub-and-bail scaffolding in source files is currently policed by code review + [R18](#r18--doc-currency-travels-with-the-commit) (doc currency); no file-Write hook exists yet. Adding one is appropriate future work.

---

## When a rule conflicts with reality

If you discover a rule that genuinely cannot be followed (e.g., a PostgreSQL limitation that forces a workaround), **surface it to the user immediately**. Do not silently violate. Do not engineer around it without authorization.

If a rule turns out to be wrong, the user updates this file. Agents do not.
