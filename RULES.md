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

---

## R2 — Three tables, no event log

The substrate has **exactly three** core tables: `entities`, `physicalities`, `attestations`. No `observations` table — that was over-engineering. Attestation rows ARE the consensus state; repeated observations from the same source UPSERT-no-op (`ON CONFLICT DO NOTHING`).

Provenance lives in the `source_id` column of attestation rows, NOT in a separate event log. Source version, trust class, and lineage/correlation family live as meta-attestations on the source entity.

---

## R3 — Lottery-ticket-aware sparsity, NEVER flat thresholds

For AI model sources, the noise floor is a **multi-pass relative filter**:

1. **Per-tensor relative top-k%** (e.g., top 5% by importance within each tensor — respects tensor's own scale)
2. **Per-row top-k for attention / MLP** (preserves load-bearing IO connectivity)
3. **Probe-validated retention test** (synthesize candidate sparse subgraph; verify behavior preserved on probe set)

**A flat numeric cutoff (e.g., `|w| < 0.001`) is forbidden.** It destroys content: different tensors have different magnitude regimes; load-bearing weights are sometimes small.

For **linguistic resources** (WordNet, OMW, UD, Wiktionary, Tatoeba, ConceptNet, Atomic2020) — every entry is curated and deliberate. **No noise floor applies.** Every attestation goes in at full fidelity.

---

## R4 — Sparse-by-construction emission

At export, positions in the target tensor with no significant substrate attestation emit **zero**. This makes emitted models automatically:

- Pruned (5–20% non-zero typical)
- Synthesized from arena/source-trust effective support over the selected source scope
- Cleaned (no gradient jitter, no init residue)

Default output format for chattable proofs: GGUF with appropriate Q-format (so sparsity benefits are realized in llama.cpp).

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

## R8 — No GPU at runtime

The substrate's runtime is CPU-native. Cascading-tier NN + spatial index lookups + Glicko-2 weighted A* are latency-bound and branchy — wrong workload for GPU.

**The one exception:** probe-time forward-pass of a model being ingested (running a 70B transformer to extract attestations may need GPU). After extraction, the substrate is CPU-only. GPU is the probe driver, NOT a runtime requirement.

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
| C/C++ engine | math, linalg, hashing, geometry, sparsity, codec, SIMD, fixed-point, file I/O | pipeline orchestration, plugin loading, network I/O, DB connection management |
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

Cascade traversal is a compiled substrate operator exposed via SQL, not SQL-as-control-flow. The hot path is a C/C++ set-returning function that owns frontier management, A* priority queues, visited sets, tier transitions, context checks, effective-score ranking, and early abstention. PostgreSQL provides storage, MVCC visibility, and indexes; SPI/executor access may perform batched, prepared, indexed lookups.

**Forbidden on the hot path:** app-layer row-by-row SELECT loops, recursive CTE graph search, cursor-driven traversal, or RBAR patterns that bounce between client and database for each frontier step.

---

## R20 — Arena semantics and source trust are mandatory

Every attestation kind that participates in rating composition belongs to an arena with explicit semantics: compatibility, cardinality, context policy, observation update scope, conflict policy, source-trust policy, lineage policy, and effective-score inputs. Glicko-2 updates MUST interpret incoming observations through those arena semantics.

Raw source counts are never consensus. Source credibility is tracked per source per attestation kind, and source trust classes are part of the prior: foundational constants, standards-derived sources, curated academic resources, academically linked user-curated resources, structured corpora, AI-model probe observations, and prompt-local/user content. Correlated source families do not become independent tugs merely by repetition.

Unsupported or low-trust claims MAY be stored as source-scoped observations, but they do not win strict traversal or synthesis scopes unless their arena-aware effective mu is supported by independent, trusted, structurally adjacent observations.

---

## R21 — Layered seed ingestion and model-codec fidelity

Early ingestion follows the layered seed order in [ADR 0037](docs/adr/0037-layered-seed-ingestion-and-model-codec-fidelity.md): Unicode/UCD/UCA/UAX, language registries, WordNet, OMW, UD, Wiktionary, Tatoeba/audio, ConceptNet/Atomic2020, tree-sitter/code, corpora, then AI model sources. Each layer adds explicit fidelity channels before later sources arrive.

AI model ingestion is a codec. `TransformerModelSource` records the model recipe, tokenizer content, source physicalities, probe observations, architecture-specific attestation arenas, and lottery-ticket sparse load-bearing structure. If source-scoped ingestion is faithful and synthesis uses the source recipe/scope, missing behavior is an implementation/codec bug, not an accepted architectural gap.

The v0.1 proof may be narrow: Unicode-derived T0 + one Qwen-family source model + sparse attestations + GGUF emission + chat verification. It does not need the full omniglottal seed stack to prove the AI⇄DB codec.

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

- Documenting the two-step one-time machine setup procedure in README / OPERATIONS / CHANGELOG.
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

## When a rule conflicts with reality

If you discover a rule that genuinely cannot be followed (e.g., a PostgreSQL limitation that forces a workaround), **surface it to the user immediately**. Do not silently violate. Do not engineer around it without authorization.

If a rule turns out to be wrong, the user updates this file. Agents do not.
