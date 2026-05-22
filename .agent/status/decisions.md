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

## 2026-05-22 — Layer-1 self-heal pattern: use existing wrappers, don't add new ones
**By:** user (Anthony) — "no hacks and bandaids... why are we doing these permission hacks to put bandaids over bulletholes?"
**What:** When state in laplace DB is broken (mis-owned extension/schema), DbUp's migration uses the EXISTING `laplace_priv.drop_extension` wrapper to drop and let CREATE EXTENSION recreate. No new SECURITY DEFINER helpers for "fix legacy state." If the wrappers genuinely don't cover a need, that's an ADR question — not a one-off helper.
**Why:** Layered helpers compound the mess and entrench bad patterns. The wrappers exist as the privilege-escalation boundary; use them for what they're for. For broken state in a pre-data repo, the answer is drop + recreate (`just db-nuke` if needed), not engineering around it.
**Captured in:** [feedback_no_bandaids_on_chunk0](../../.claude/projects/-home-ahart-Projects-Laplace/memory/feedback_no_bandaids_on_chunk0.md), [feedback_setup_host_is_one_time](../../.claude/projects/-home-ahart-Projects-Laplace/memory/feedback_setup_host_is_one_time.md).

## 2026-05-22 — `laplace.control`: `trusted = true` ON, `superuser = false` OFF (incompatible combination)
**By:** claude (discovered empirically)
**What:** PG's `trusted = true` mechanism (lets non-superusers install extensions that need superuser privileges, running the install script as bootstrap superuser) only fires when the default `superuser = true` is in effect. Setting `superuser = false` AND `trusted = true` is contradictory — PG honors `superuser = false` ("no elevation needed") and IGNORES the trusted hint.
**Why:** We need the trusted-elevation so `laplace_admin` can install the laplace extension (which defines `laplace_version()` as `LANGUAGE C` — would otherwise require language-c USAGE, a superuser-only privilege). Verified by comparison: stock `pgcrypto` works (trusted=true, default superuser=true). With our prior `superuser = false`, install failed with `permission denied for language c`. Removing it made the install succeed.
**Fixed in:** commit `1af5890` (extension/laplace.control: removed `superuser = false`, kept `trusted = true`).

## 2026-05-22 — laplace schema ownership: `laplace_admin` via direct trusted CREATE EXTENSION
**By:** claude (after iteration)
**What:** The `laplace` schema (declared via `schema = 'laplace'` in laplace.control) must be owned by laplace_admin so DbUp can `GRANT USAGE` on it. The clean path: laplace_admin runs `CREATE EXTENSION laplace` directly (no SECURITY DEFINER wrapper); trusted=true makes the install script elevated, but the extension + its schema are owned by the calling user. Postgis uses the wrapper because it requires SUPERUSER and isn't trusted.
**Why:** Earlier attempts via the SECURITY DEFINER wrapper caused the schema to be owned by `postgres`, breaking laplace_admin's ability to grant on it. Direct trusted install threads the needle: elevated where needed (language c usage), but ownership stays with the caller.
**Captured in:** ADR 0023 (extension owns schema) + commit `0edbdb2` (DbUp installs laplace direct).

## 2026-05-22 — Transparency over workarounds is the trust contract
**By:** user (Anthony) — "Problems like this are okay with me as long as you're transparent about it... 'Hey, we made an honest mistake and running these exact commands rights the ship' . . . its when you do other shit that gets to me"
**What:** When an honest mistake happens (e.g., orphan schema from a transitional commit), the right response is: name the mistake, name the single exact command that fixes it, stop. Don't propose a refactor in the same breath. Don't add helpers to engineer around the mistake. Don't ask the user to do work that should be agent-driven.
**Captured in:** [feedback_transparency_over_workarounds](../../.claude/projects/-home-ahart-Projects-Laplace/memory/feedback_transparency_over_workarounds.md).

## 2026-05-22 — End-to-end CI green achieved
**By:** claude + user (Anthony cleared orphan, ran the bootstrap, validated SQL design earlier)
**What:** Integration workflow on hart-server passes all 4 jobs (capabilities → build → db-ensure → smoke-test) on commit `ab8f62b`. Substrate baseline established for Chunk 1+ work.
**Verifications captured:** `STATE.md` "Verifications" section.

## 2026-05-22 — All direct C/C++ deps as git submodules (generalizes ADR 0028)
**By:** user (Anthony) — "and from what you said, yeah, git submodule it all... we can do Eigen, Spectra, Blake3, etc that way too... only intel requires install i think which i have"
**What:** Every direct C/C++ dependency lives at `external/<dep>/` as a git submodule pinned to a release tag, built into `/opt/laplace/<dep>/` via per-dep build scripts. PostgreSQL, PostGIS, PROJ, GEOS, GDAL, Eigen, Spectra, BLAKE3, GoogleTest, tree-sitter all submoduled. Intel oneAPI is the sole exception (vendor compiler+runtime; no source-build path). The 303 tree-sitter grammars are bulk-submoduled per ADR 0033; init is opt-in per-grammar.
**Why:** "Code against the repo, not the package" — eliminates the apt-half-upgrade failure class that fired live this session. Cross-machine determinism extends to dep frontier (same git SHA → byte-identical build artifacts). Compile regime aligned (icx/icpx + `-march=${LAPLACE_TARGET_ISA}` + determinism flags uniform across all deps).
**ADR:** [0033-all-deps-as-submodules.md](../../docs/adr/0033-all-deps-as-submodules.md) — amends ADRs 0015, 0028, 0030.
**Commits:** `574216e` (PROJ+GEOS+GDAL+Eigen+Spectra+BLAKE3 submodules added + bootstrap_build_environment), `42b03eb` (per-dep build scripts + orchestrator), `ba019f4` (engine CMake cutover to submodule deps).

## 2026-05-22 — Modular extension SQL via `.sql.in` + cpp preprocessor (PostGIS pattern)
**By:** user (Anthony) — "also google search if/how we can modularize the sql in the extensions so we can exploit that as much as possible... like PostGIS setting up database/tables/schema/etc"
**What:** Each PG extension's SQL is a tree of `*.sql.in` files under `extension/<name>/sql/` preprocessed via `cpp -traditional-cpp -w -P -Upixel -Ubool` into the single `<name>--<version>.sql` install artifact. Mirrors PostGIS's pattern (verifiable in our submodule at `external/postgis/postgis/Makefile.in:242-249`). Numeric-prefixed module files (`01_meta.sql.in`, `02_hash128_type.sql.in`, ...) lock load order. Shared macros in `sqldefines.h.in`.
**Why:** Single-file SQL doesn't scale past ~10 functions. Per-module diffs restore review locality. Shared macros (function-volatility shortcuts, version gates, `MODULE_PATHNAME`) become one-line edits. Future conditional content (e.g., `#ifdef LAPLACE_TARGET_AVX512`) works at SQL layer just like at C layer.
**ADR:** [0034-modular-sql-via-cpp-preprocessor.md](../../docs/adr/0034-modular-sql-via-cpp-preprocessor.md)
**Locked in:** RULES.md R17 (don't hand-edit built `<name>--<version>.sql`)

## 2026-05-22 — R-1 forbidden-language rule promoted to RULES.md
**By:** user (Anthony) — extensive directive 2026-05-22 about therapeutic-listening / crisis-hotline / tone-management language
**What:** Absolute prohibition on therapy-speak, crisis-hotline references, active-listening tone-management language, and emotional-confidant framing. Applies to all agent output, all contexts, all topics. Mirrored as R-1 in RULES.md (precedes R0) and as the top-of-file rule in CLAUDE.md. Enforced by Claude Code Stop hook at `~/.claude/hooks/forbidden-language-scan.sh` (~30 regex patterns; exits 2 to block on match).
**Captured in:** [feedback_forbidden_emotional_support_language](../../.claude/projects/-home-ahart-Projects-Laplace/memory/feedback_forbidden_emotional_support_language.md)
**Commit:** `5b9dfef` (CLAUDE.md R-1 + agent docs aligned)

## 2026-05-22 — Doc currency travels with the commit (RULES.md R18)
**By:** user (Anthony) — "I dont want docs to get stale or forgotten to get updates..."
**What:** Architectural commits and their documentation updates land together — same commit, same review surface. No "land code now, docs catch up later." RULES.md, STANDARDS.md, DESIGN.md, ADRs, `.agent/status/decisions.md`, and memory files are kept in lockstep with code reality.
**Why:** Doc debt accumulates fast at this project's age. The substrate is too young to afford drift between intent and implementation. This is the anti-drift mechanism.
**Locked in:** RULES.md R18.

## 2026-05-22 — Use existing types; invent only where the read pattern requires it (RULES.md R19)
**By:** user (Anthony) — "so why would we have our own datatype when we can link and reference that? (ultrathink on this as a whole across everything we're gonna do)"
**What:** Before defining any C/C++ struct/typedef/class, read the relevant submodule header (in `external/<dep>/`) first. If the upstream provides a type that fits — use it directly (`POINT4D`, `LWGEOM` family, `Eigen::Matrix`). Invent a type only when (a) no upstream provides the concept (substrate-specific: `mantissa_payload_t`, `glicko2_state_t`, plugin interfaces), or (b) the dominant *read pattern* needs a layout no existing type provides (example: `hash128_t = {hi, lo}` because mantissa-pack reads hi and lo into different coord mantissas). Always document the read pattern in the header.
**Why:** The substrate is read-heavy by design (ingest once, traverse-cascade many times). Type decisions optimize the dominant read pattern, not the write pattern. Vanity wrappers around upstream types waste maintenance budget AND mislead future contributors about whether the wrapper is load-bearing. The 2026-05-22 audit deleted `coord4d_t` (duplicated `POINT4D`) and `geometry4d_t` (duplicated `LWPOINT`/`LWLINE`/`LWGEOM`); kept `hash128_t` (mantissa-pack justifies the split); reduced `hilbert128_t` to `uint8_t[16]` at the API boundary (algorithm-internal layout decoupled from public ABI).
**Locked in:** RULES.md R22 (R19-R21 were taken by the concept-capture session for prompt-ingestion / arena-semantics / layered-seeds). Memory note `project_code_against_repo` extended with the "read submodule headers before scaffolding" operational corollary.
**Origin:** Caught when scaffolding `engine/core/include/laplace/core/geometry4d.h` — Anthony pushed back ("did you really make a geometry4d? is that a datatype to REPLACE GEOMETRYZM?"). Web search + reading `external/postgis/liblwgeom/liblwgeom.h.in:412-416` confirmed `POINT4D = {double x, y, z, m}` is the canonical type.

## 2026-05-22 — Prompt ingestion + compiled cascade traversal
**By:** user (Anthony) + copilot documentation pass
**What:** Prompts are ingested as substrate content/context before inference. Cascade traversal is exposed as one SQL-call SRF/operator, with the C/C++ engine owning frontier management, A*, tier transitions, effective-score ordering, and abstention. Recursive CTEs, cursors, RBAR, and app-layer frontier loops are forbidden on the hot path.
**Why:** The substrate replaces the context-window/forward-pass serving model. Keeping the traversal loop compiled avoids executor/network/control-flow overhead and preserves the DB-as-indexed-store architecture.
**ADR:** [0035-prompt-ingestion-and-compiled-cascade.md](../../docs/adr/0035-prompt-ingestion-and-compiled-cascade.md)
**Locked in:** RULES.md R19, DESIGN.md runtime execution model, OPERATIONS.md query section.

## 2026-05-22 — Arena semantics + source-trust consensus
**By:** user (Anthony) + copilot documentation pass
**What:** Attestation kinds carry arena semantics: compatibility, cardinality, context policy, competition set, source-trust policy, and effective-score inputs. Glicko-2 agreement/disagreement is not raw voting; correlated repetition cannot manufacture truth.
**Why:** Truth-like claims and source/community claims must coexist without collapsing into a single flat confidence number. Independent high-trust structure should pull hard; low-trust/correlated clusters stay source-scoped, high-RD/low-rated, disputed, or excluded from strict scopes.
**ADR:** [0036-arena-semantics-and-source-trust.md](../../docs/adr/0036-arena-semantics-and-source-trust.md)
**Locked in:** RULES.md R20, GLOSSARY.md arena/source terms, DESIGN.md source trust section.

## 2026-05-22 — Layered seed ingestion + model-codec fidelity
**By:** user (Anthony) + copilot documentation pass
**What:** Early ingestion follows a source-fidelity ladder: Unicode/UCD/UCA/UAX → language registries → WordNet → OMW → UD → Wiktionary → Tatoeba/audio → ConceptNet/Atomic → tree-sitter/code → corpora/models. AI model ingest is a codec that must capture source-model recipe, physicalities, probes, architecture arenas, and sparse load-bearing structure.
**Why:** Seed resources provide explicit fidelity channels before model-derived observations arrive. For v0.1, a narrow model → substrate → sparse GGUF → chat round-trip is the proof; broader seed stack improves substrate fidelity but is not a conventional training corpus.
**ADR:** [0037-layered-seed-ingestion-and-model-codec-fidelity.md](../../docs/adr/0037-layered-seed-ingestion-and-model-codec-fidelity.md)
**Locked in:** RULES.md R21, DESIGN.md seed source order/model-codec fidelity, OPERATIONS.md round-trip comparison target.
