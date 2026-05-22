# Laplace — Decisions Log

Append-only timestamped record of architectural / engineering decisions. Format:

```
## YYYY-MM-DD — <decision title>
**By:** <agent or user>
**What:** <one-line summary>
**Why:** <reasoning, citations to RULES/DESIGN/memory>
**Supersedes:** <link to prior decision if applicable, or "—">
```

---

## 2026-05-21 — Use standard PostGIS `geometry` with Z+M = 4D; do NOT create parallel `geometry4d` type
**By:** user (multi-turn refinement) + framework initialization
**What:** Extend PostGIS GEOMETRYZM by using its standard `geometry` type with Z+M flags set (4D points + linestrings). Use `gist_geometry_ops_nd` for indexing. Write custom 4D-aware functions only where standard PostGIS is 2D/3D-only.
**Why:** Maximum leverage on PostGIS's decades of work. No custom GIST opclass needed (so no seg-fault risk). Cross-modality unification natural (same geometry type for text/audio/image/video). See [RULES.md R1](../../RULES.md).
**Supersedes:** earlier discussion suggesting `CREATE TYPE geometry4d`.

## 2026-05-21 — Three core tables; NO observations event log
**By:** user (correction during design conversation)
**What:** Substrate has `entities`, `physicalities`, `attestations` only. No `observations` table.
**Why:** Attestation IS consensus state, not event log entry. Repeated source assertions are idempotent. Provenance lives in `source_hash` column. See [RULES.md R2 / R5](../../RULES.md).

## 2026-05-21 — XXH3-128 for entity hashing (SUPERSEDED by BLAKE3 decision below)
**By:** initial framework
**What:** Use libxxhash3's 128-bit variant for entity content hashing. Stored as `bytea(16)` in Postgres.
**Why:** SIMD-vectorized, fast, 128-bit collision-resistance comfortable for ~10¹⁸ entities. Already installed (libxxhash 0.8.1). BLAKE3 considered but rejected — cryptographic strength not needed (we control all ingested content); build-from-source overhead.
**Superseded by:** "BLAKE3 truncated to 128 bits" (2026-05-21, later same day)

## 2026-05-21 — int64 fixed-point at scale 1e9 for Glicko-2
**By:** initial framework
**What:** Glicko-2 rating / RD / volatility stored as `int64` with implicit scale factor of 10⁹.
**Why:** Determinism by construction. FP non-determinism in Glicko-2 update path was the largest remaining open hole; fixed-point arithmetic eliminates it. Vectorizable for batch updates.

## 2026-05-21 — Hilbert curve over `[-1, 1]⁴` bounding hyperbox (NOT on sphere)
**By:** user (clarification)
**What:** Single 4D Hilbert curve fills the bounding hyperbox of the 4-ball. One curve indexes both S³ surface entities AND 4-ball interior centroids with consistent 1D locality.
**Why:** Sphere-native curves (HEALPix-style) only cover the surface, but the interior is meaningful (abstraction-graded reservoir). Box-shaped curve covers everything; B-tree on Hilbert index supports range scans uniformly.

## 2026-05-21 — Perf-cache and DB seed are SIBLING artifacts (both from UCD, not parent-child)
**By:** user (correction)
**What:** The build pipeline derives the perf-cache binary AND the DB seed file INDEPENDENTLY from Unicode UCD. Neither feeds the other.
**Why:** Independent regeneration; cross-verification; no single point of failure. Either artifact can be rebuilt; mismatch indicates a problem.

## 2026-05-21 — Lottery-ticket-aware sparse recording; NEVER flat thresholds
**By:** user (correction)
**What:** AI model ingestion uses a multi-pass filter: per-tensor relative top-k% + per-row top-k + probe-validated retention. A single numeric cutoff is forbidden.
**Why:** Flat thresholds destroy content (different tensors have different magnitude regimes). Per-tensor relative + per-row structural + probe-validation captures the lottery-ticket subnetwork. Linguistic resources at full fidelity (no filter applies).

## 2026-05-21 — Sparse-by-construction emission
**By:** user (insight)
**What:** At export, positions with no significant substrate attestation emit zero. Emitted models are automatically pruned, ensembled, and consensus-cleaned.
**Why:** Lottery-ticket-aware sparsity at ingest yields sparse-aware emission at export, without a separate pruning step. Smaller / cleaner / consensus-derived models for free.

## 2026-05-21 — Recipe extraction at model ingest; user JSON override for variants
**By:** user (request for repeatability)
**What:** Ingesting a model auto-extracts its `config.json` as a Recipe entity with typed attestations. Default export uses the source's Recipe as template (round-trip). User custom-recipe JSON overrides any field for parametric variants.
**Why:** Round-trip is the default proof-of-concept workflow. Custom variants are reproducible from JSON state.

## 2026-05-21 — Substrate Synthesis as the name for fully parametric export
**By:** user (selection from alternatives)
**What:** "Substrate Synthesis" is the working term for emitting a model of any architecture / dimensionality / configuration from substrate state.
**Why:** Captures the act (synthesis = assembling from parts) and the source (substrate). Open to refinement if a sharper term emerges.

## 2026-05-21 — Polymorphic plugin architecture; one plugin per new capability
**By:** user (engineering discipline)
**What:** Six plugin interfaces: `ISource`, `IDecomposer`, `IArchitectureTemplate`, `IFormatWriter`, `IFeatureExtractor`, `IProtocolEndpoint`. Adding new capability = ONE plugin, never schema + query + synthesis touches.
**Why:** Codebase stays maintainable. See [RULES.md R10](../../RULES.md).

## 2026-05-21 — BLAKE3 truncated to 128 bits for entity hashing (supersedes XXH3 decision)
**By:** user (Anthony, after consideration)
**What:** Switch from XXH3-128 to **BLAKE3** (official C implementation, FetchContent pinned to v1.5.4), truncated to 128 bits. Stored as `bytea(16)` in PG; `hash128_t = {uint64_t hi, lo}` in C engine; `byte[16]` in C#.
**Why:** SIMD-accelerated (BLAKE3 builds with SSE2/SSE4.1/AVX2/AVX-512 variants — verified in CMake configure output). Cryptographic strength is free insurance for signed substrate snapshots / supply-chain verification. Familiar from prior iteration. Vendor cost is one FetchContent block. The XXH3-128 marginal speed advantage on small inputs doesn't outweigh BLAKE3's other properties for "enterprise-grade / incredibly capable" framing.
**Discipline:** Raw bytes end-to-end. NO `bytea ↔ text` casts; NO hex conversions outside debug-only paths; NO `varchar` / `text` for hash storage. See STANDARDS.md "Hash discipline (no casts, no hex)".
**Supersedes:** "XXH3-128 for entity hashing" (earlier same day).

## 2026-05-21 — Reusable helpers — DRY at every layer (project-wide discipline)
**By:** user (Anthony)
**What:** Every operation used more than once must be a named, tested, single-source-of-truth helper. Applies to: hash ops, coord arithmetic, Hilbert encode/decode, mantissa pack/unpack, geometry serialization, Glicko-2 update, marshalling (PG ↔ engine ↔ C#).
**Why:** Correctness solved once; performance optimized in one place; behavior consistent; bugs have one place to fix not 17 inlined copies. Cross-language consistency: ONE canonical engine implementation; PG wrappers and C# bindings are thin glue.
**Codified in:** STANDARDS.md "Reusable helpers — DRY at every layer" section.

## 2026-05-21 — Agent cadence — proactively review/update issues on user input
**By:** user (Anthony, as standing instruction)
**What:** Standing agent operating procedure: when user surfaces requirements/decisions/changes, scan affected issues, update or open new ones; append decisions.md if architectural; reflect in standards/design with user authorization. Don't wait to be told. Update STATE.md per chunk completion. Re-read plan + RULES + STANDARDS + DESIGN at chunk start.
**Why:** Prevents drift. Keeps issues + status + decisions ledger current as the conversation evolves. The cadence is the anti-pattern for the 0%-after-12-months failure mode.
**Codified in:** CLAUDE.md "Cadence — standing agent operating procedure" section.

## 2026-05-21 — Two-tier CI/CD: GitHub-hosted for PR validation; self-hosted for integration on push-to-main only
**By:** user + framework setup
**What:** Two workflow files. `ci.yml` runs on GitHub-hosted disposable VMs (free for public repos) for doc checks, lints, banned-vocabulary scan, link integrity. Triggers on push + PR + manual. `integration.yml` runs on self-hosted `hart-server` runner (oneAPI + PG18 + .NET 10) for build / test / verify. Triggers on push-to-main + workflow_dispatch ONLY — never on pull_request.
**Why:** Hybrid is the right security posture for a public repo with a self-hosted runner. PR code (potentially malicious) runs on disposable VMs. Trusted code (post-merge / manual) runs on the self-hosted machine that has access to local resources (oneAPI, /vault/models, PG). User's credentials are the only path to triggering self-hosted workflows.

## 2026-05-21 — Self-hosted runner: hart-server, systemd service, label-routed
**By:** framework setup
**What:** Installed GitHub Actions runner v2.334.0 at `~/actions-runner/`. Configured as systemd service `actions.runner.SaltyPatron-Laplace.hart-server.service`. Labels: `self-hosted, Linux, X64, laplace, oneapi, postgres-18, dotnet-10, avx2`. Workflows opt in via `runs-on: [self-hosted, laplace]`.
**Why:** Enterprise-grade CI/CD with persistent local resource access (oneAPI / PG / large data dirs / models). Survives reboots via systemd. Label-routed so workflows specifically targeting Laplace's capabilities land on this machine.

## 2026-05-21 — Mantissa packing: 8 tier + 12 position + 60 truncated hash bits per vertex
**By:** initial framework
**What:** Trajectory vertex coords carry constituent identity in low mantissa bits: 8 bits tier + 12 bits position-in-trajectory + 60 bits truncated constituent hash. High mantissa bits preserve approximate spatial position for indexing.
**Why:** Self-contained trajectories. 60-bit hash collision probability negligible within a trajectory; full 128-bit hash resolution via entity-table lookup when needed.

## 2026-05-21 — Three-layer architecture: bootstrap / CI / local dev
**By:** user (Anthony) — emerging from sudo-commands-in-chat correction
**What:** Layer 0 = root-only one-time setup (system account, runner, PG roles, peer auth, sudoers). Layer 1 = DbUp + extension lifecycle (database, CREATE EXTENSION, role grants). Layer 2 = CI + Justfile (build, install, db-up, smoke test). Each layer independently resettable.
**ADR:** [0018-three-layer-architecture.md](../../docs/adr/0018-three-layer-architecture.md)
**Why:** Prevents the "everything in one big setup script" sabotage pattern. Reset paths are explicit (`bootstrap-reset` doesn't touch substrate data; `db-nuke` doesn't touch runner identity).

## 2026-05-21 — Dedicated `laplace-runner` system account replaces `ahart` as the CI runner
**By:** user (Anthony) — "we can add it as a system account with no home folder"
**What:** GitHub Actions runner runs as the `laplace-runner` system account (no home in /home, no shell), installed at `/var/lib/laplace-runner/actions-runner`. Peer-authenticates as PG role `laplace_admin` (CREATEDB CREATEROLE). Bounded NOPASSWD sudo for `/usr/bin/make install*` only.
**ADR:** [0019-laplace-runner-system-account.md](../../docs/adr/0019-laplace-runner-system-account.md) (supersedes ADR 0014)
**Why:** Separation of concerns: CI identity ≠ interactive developer identity. Reduces blast radius if runner is compromised. Eliminates "ahart is a PG superuser" anti-pattern.

## 2026-05-21 — Conventional Commits + release-please for automated CHANGELOG + SemVer
**By:** user (Anthony) — full SDLC adoption
**What:** All commits on main follow Conventional Commits format. release-please-action@v4 watches main and opens release PRs that bump version (per type → SemVer mapping) and update CHANGELOG.md.
**ADR:** [0020-conventional-commits-and-release-please.md](../../docs/adr/0020-conventional-commits-and-release-please.md)
**Why:** Versioning, changelog, and "Done means Done" share one definition. release-please removes humans from the version-bump loop entirely.

## 2026-05-21 — DbUp + Npgsql for migrations (replaces initially-drafted dbmate)
**By:** user (Anthony) — "dbmate? what the fuck is that? aren't we using npgsql?"
**What:** `app/Laplace.Migrations/` is a .NET 10 console app using DbUp + Npgsql. `up` / `status` / `reset` / `nuke` modes. Resolves connection from `--connection-string` > `DATABASE_URL` > `PG_*` env vars > peer-auth default.
**ADR:** [0021-dbup-for-migrations.md](../../docs/adr/0021-dbup-for-migrations.md) (narrowed by ADR 0023)
**Why:** Project's app layer is already .NET / Npgsql. Adding a Go binary (dbmate) for a parallel toolchain was MVP-shaped corner-cutting.

## 2026-05-21 — ADRs (Nygard format) as the canonical decision-record format
**By:** user (Anthony) — SDLC adoption
**What:** Every architectural decision lands in `docs/adr/NNNN-kebab-title.md` using the Nygard template (Status / Context / Decision / Consequences / Alternatives / References). Supersedence is explicit (never silently re-decide).
**ADR:** [0022-adrs-as-decision-format.md](../../docs/adr/0022-adrs-as-decision-format.md)
**Why:** `decisions.md` in `.agent/status/` is the lightweight ledger; ADRs are the first-class durable record discoverable from the repo root. New contributors (human or agent) can read the ADR set and understand WHY the codebase looks the way it does.

## 2026-05-21 — Laplace extension owns its schema; DbUp orchestrates lifecycle (narrows ADR 0021)
**By:** user (Anthony) — "you're ignoring the database/table/schema creation capabilities of postgres extensions"
**What:** The `laplace` extension OWNS the substrate's schema, tables (`entities` / `physicalities` / `attestations`), composite types, GIST opclasses, SRFs, aggregates, and indexes via `laplace--A.B.C.sql`. Tables holding user data are marked with `pg_extension_config_dump()`. Schema evolution uses `laplace--A.B.C--D.E.F.sql` upgrade scripts triggered by `ALTER EXTENSION laplace UPDATE`. DbUp narrows to: `CREATE EXTENSION` orchestration + role grants + non-extension operational tables (none currently). Extension is non-relocatable (`relocatable = false`) because it references `@extschema@`-qualified objects.
**ADR:** [0023-extension-owns-schema-dbup-orchestrates.md](../../docs/adr/0023-extension-owns-schema-dbup-orchestrates.md) (narrows ADR 0021)
**Why:** Substrate-binary and substrate-SQL versions must evolve together (a C function pointer in `laplace.so` must match its declaration in `laplace--X.Y.Z.sql`). PG's extension upgrade machinery is purpose-built for that lockstep. `pg_dump` of a Laplace database compacts to one `CREATE EXTENSION` line + the three tables' data via `pg_extension_config_dump`. Splitting substrate schema across "extension SQL" and "DbUp migrations" would have been the sabotage-shaped path.

## 2026-05-22 — Engine modularization: 3 shared libraries (core / dynamics / synthesis)
**By:** claude (recommendation) + user (acceptance)
**What:** `liblaplace_core.so` (no MKL; loaded by PG backend), `liblaplace_dynamics.so` (MKL+Spectra+TBB; C# only), `liblaplace_synthesis.so` (depends on dynamics; C# only). Folder restructure: `engine/{core,dynamics,synthesis}/`.
**ADR:** [0024-engine-modularization.md](../../docs/adr/0024-engine-modularization.md)

## 2026-05-22 — PG extension modularization: laplace_geom + laplace_substrate
**By:** claude (recommendation) + user (acceptance)
**What:** `laplace_geom` (general-purpose 4D PostGIS additions, reusable) + `laplace_substrate` (substrate domain, requires laplace_geom). Refines ADR 0023.
**ADR:** [0025-pg-extension-modularization.md](../../docs/adr/0025-pg-extension-modularization.md)

## 2026-05-22 — C# project structure: per-engine-lib bindings + functional + plugins
**By:** claude
**What:** `Laplace.Engine.{Core,Dynamics,Synthesis}` (1:1 with engine .so files) + `Laplace.Migrations` + `Laplace.Cli` + `Laplace.Endpoints[.*]` + `Laplace.Sources.*` + `Laplace.Decomposers.*`. .slnx coordinates.
**ADR:** [0026-csharp-project-structure.md](../../docs/adr/0026-csharp-project-structure.md)

## 2026-05-22 — Separation of concerns invariants codified
**By:** user (Anthony) — "SQL and C# are orchestration with C/C++ as the heavy lifters"
**What:** Per-layer may/must-not matrix. Math in C/C++. Orchestration in C#/SQL. PG extension is binding glue only. SQL migrations declarative DDL only. Codified into RULES.md R16.
**ADR:** [0027-separation-of-concerns-invariants.md](../../docs/adr/0027-separation-of-concerns-invariants.md)

## 2026-05-22 — Custom-built PostgreSQL 18 + PostGIS 3.6.3 with Intel toolchain (PREREQUISITE)
**By:** user (Anthony) — "epic b is required... we code against the repo itself"
**What:** Git submodules under `external/postgresql/` and `external/postgis/`, pinned to release tags, built with `icx`/`icpx`, installed to `/opt/laplace/pgsql-18/`. Hard prerequisite (not parallel/deferrable) for performance regime alignment + "code against the repo" correctness. Eliminates the apt-half-upgrade failure class that fired multiple times this session.
**ADR:** [0028-custom-built-pg-postgis-intel.md](../../docs/adr/0028-custom-built-pg-postgis-intel.md) (amended 2026-05-22 to lock in)

## 2026-05-22 — Custom indexing strategy: 5 substrate-shaped opclasses (amends RULES.md R1)
**By:** user (Anthony) — "custom gist/sp-gist indexing if we can do custom stuff to make my invention faster"
**What:** `laplace_btree_hash128_ops` + `laplace_gist_s3_ops` + `laplace_sp_trajectory_ops` + `laplace_brin_tier_ops` + Glicko-2-aware GIST internal stats. Distributed across the two PG extensions and Chunks 1-5.
**ADR:** [0029-custom-indexing-strategy.md](../../docs/adr/0029-custom-indexing-strategy.md)

## 2026-05-22 — MKL / Eigen / Spectra / TBB integration regime + MKL_CBWR determinism
**By:** user (Anthony) — "doesnt mkl benefit from integration with eigen and/or spectra as well? they all intermingle"
**What:** `EIGEN_USE_MKL_ALL` in dynamics; `mkl_set_threading_layer(MKL_THREADING_TBB)` unified scheduler; `mkl_cbwr_set(AVX2|AVX512)` for substrate determinism (5-10% perf cost accepted per RULES.md R7).
**ADR:** [0030-mkl-eigen-spectra-tbb-integration.md](../../docs/adr/0030-mkl-eigen-spectra-tbb-integration.md)

## 2026-05-22 — Custom Access Method spike (perf-cache-backed; post-v0.1.0)
**By:** claude (proposal)
**What:** Time-boxed 2-week prototype of `laplace_perfcache_am` post-v0.1.0. Go/no-go thresholds: ≥2× cascade speedup, ≥3× cold-cache.
**ADR:** [0031-custom-am-spike.md](../../docs/adr/0031-custom-am-spike.md)

## 2026-05-22 — Unified CMake build pipeline (Path B) — PGXS retired
**By:** user (Anthony) — "Ultrathink about how postgres requires PGXS and such... While we're focused on this we should focus on our cmake file too"
**What:** Top-level CMakeLists drives external/ submodules + engine/ + extension/. Phase 1 bridge keeps PGXS while on stock PG; Phase 2 fully retires PGXS once Epic B lands. One `cmake -B build && cmake --build build && cmake --install build` builds + installs everything.
**ADR:** [0032-unified-cmake-build-pipeline.md](../../docs/adr/0032-unified-cmake-build-pipeline.md) — locks ADR 0028 as prerequisite
