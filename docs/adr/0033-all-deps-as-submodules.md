# ADR 0033: All direct dependencies as git submodules ("code against the repo, not the package")

## Status

**Accepted** — 2026-05-22

Generalizes [ADR 0028](0028-custom-built-pg-postgis-intel.md) — what 0028 mandated for PostgreSQL + PostGIS, this ADR mandates for every direct C/C++ dependency. Supersedes the `FetchContent` clauses in [ADR 0015](0015-blake3-for-entity-hashing.md) (BLAKE3) and the `apt` / `FetchContent` clauses in [ADR 0030](0030-mkl-eigen-spectra-tbb-integration.md) (Eigen, Spectra).

## Context

ADR 0028 made PostgreSQL and PostGIS git submodules under `external/` for two reasons: (1) the Intel-toolchain performance regime can't be achieved with apt packages, and (2) when something fails, "code against the repo" means errors point at code we can read and audit. Both reasons generalize.

Through the May 2026 architectural overhaul session we accumulated several direct C/C++ dependencies under different acquisition strategies:

| Dependency | Acquisition (pre-0033) | Problem |
|---|---|---|
| PostgreSQL 18 | apt — `postgresql-18` | Mismatched .control / .so versions; performance regime can't be set |
| PostGIS 3.6.3 | apt — `postgresql-18-postgis-3` | `update-alternatives` left a stale `.control` after `apt --reinstall`, fired live on hart-server; performance regime can't be set |
| GEOS, PROJ, GDAL | apt — `libgeos-dev`, `libproj-dev`, `libgdal-dev` | Same apt-shaped failure class as PostgreSQL/PostGIS, plus shared-library version skew between hart-server and any other deploy target |
| Eigen 3.4 | apt — `libeigen3-dev` | Same skew risk; can't pin to a precise commit for cross-machine determinism |
| Spectra v1.2.0 | CMake `FetchContent` | Build-time network fetch; tarball-extracted into `engine/third_party/`; no audit trail beyond URL pin |
| BLAKE3 1.5.4 | CMake `FetchContent` | Same as Spectra |

Five different acquisition mechanisms for the same conceptual thing (direct C/C++ source dependency). Each has its own failure shape. None lets us bisect into the dep when something breaks. None preserves cross-machine determinism beyond "trust the URL still resolves to the same bytes."

The session also fired a concrete failure (commit `a689478` etc.): apt's `update-alternatives` machinery silently refused to overwrite `/usr/share/postgresql/18/extension/postgis.control`, leaving a `3.7.0dev` version declaration pointing at a `3.6.3` binary. The diagnostic path required decompiling `postgis-3.so` to confirm the missing symbol, which is exactly the failure class memory note [feedback_no_bandaids_on_chunk0](../../memory/feedback_no_bandaids_on_chunk0.md) and [project_code_against_repo](../../memory/project_code_against_repo.md) say we will not engineer around.

The substrate's performance regime ([memory: laplace-performance](../../memory/project_laplace_performance.md)) further reinforces this — we need control over the compile invocation for each dep to set `icx`/`icpx` + `-march=${LAPLACE_TARGET_ISA}` + determinism flags. apt and FetchContent don't give us that.

## Decision

**Every direct C/C++ dependency is a git submodule under `external/` pinned to a release tag.**

Concretely, post-this-ADR the dependency table is:

| Dependency | Path | Pinned to | Build script |
|---|---|---|---|
| PostgreSQL 18 | `external/postgresql/` | `REL_18_0` (GA release tag) | `scripts/build-pg.sh` |
| PostGIS 3.6.3 | `external/postgis/` | `3.6.3` release tag | `scripts/build-postgis.sh` |
| GEOS 3.12.2 | `external/geos/` | `3.12.2` release tag | `scripts/build-geos.sh` |
| PROJ 9.4.1 | `external/proj/` | `9.4.1` release tag | `scripts/build-proj.sh` |
| GDAL 3.9.3 | `external/gdal/` | `v3.9.3` release tag | `scripts/build-gdal.sh` |
| Eigen 3.4.0 | `external/eigen/` | `3.4.0` release tag | header-only — used via `add_library(INTERFACE)` from `engine/CMakeLists.txt` |
| Spectra v1.2.0 | `external/spectra/` | `v1.2.0` release tag | header-only — same pattern |
| BLAKE3 1.5.4 | `external/blake3/` | `1.5.4` release tag | `add_subdirectory(external/blake3/c)` from `engine/CMakeLists.txt` |
| tree-sitter (runtime) | `external/tree-sitter/` | `v0.22.6` or current stable | `scripts/build-tree-sitter.sh` |
| GoogleTest | `external/googletest/` | `v1.15+` (TBD on Epic A) | `add_subdirectory` from engine test CMakeLists |

**tree-sitter grammars** (303 `tree-sitter-<lang>` parser source repos, ~1.9 GB unpacked at `/vault/Data/TreeSitter/`) ARE bulk-submoduled under `external/tree-sitter-grammars/<lang>/`. The bulk-add is performed via `scripts/import-tree-sitter-grammars.sh` which reads each grammar's pinned SHA from `/vault/Data/TreeSitter/<lang>/.git/HEAD` and issues a `git submodule add <upstream-url> external/tree-sitter-grammars/<lang>` per language. `.gitmodules` grows by 303 entries (~50KB) — acceptable for full reproducibility. **Init is opt-in per grammar** — most contributors only init the language they're working on (`git submodule update --init external/tree-sitter-grammars/tree-sitter-python`); a full `--recursive` is a 5-10 minute one-time fetch.

**The one exception is Intel oneAPI** (icx/icpx, oneMKL, oneTBB, IPP). It's installed at `/opt/intel/oneapi/` via Intel's installer. Rationale: (1) it's a vendor compiler+runtime, not a source-code dep we'd ever modify; (2) building oneAPI from source is impractical (closed components); (3) Intel's installer is the only supported acquisition path; (4) it lives in `/opt/intel/` — a vendor-owned, version-pinned path with no apt-style update-alternatives drift.

### Submodule invariants

1. **Pinned to release tags only** — never `main` / `master` / `HEAD`. Tag pins are reviewable in `.gitmodules`.
2. **Updates require an ADR amendment** documenting the version bump and what changed.
3. **The submodule is the source of truth.** No vendored copies, no in-tree forks. If a patch is needed, apply it via the build script as a `git apply` step against the submodule's checkout — and surface the patch in `external/patches/`.
4. **No apt installation of these libraries.** Once this ADR lands, `apt install libpostgresql-18-dev` (etc.) is forbidden in any bootstrap path. The bootstrap-script `bootstrap_build_environment` step installs only build-time tooling (build-essential, cmake, ninja, autoconf, ...) and supporting libraries Intel oneAPI doesn't provide (libxml2-dev, libicu-dev, etc.).
5. **All built into `/opt/laplace/<dep>/`** — a single prefix tree owned by `laplace-runner`. Multiple `LAPLACE_*_PREFIX` env vars in build scripts allow override.

### Submodule init in the bootstrap flow

`scripts/bootstrap-laplace-runner.sh` (Layer 0) handles privileged setup. Submodule initialization is a per-developer / per-CI-runner step (not privileged):

```sh
git submodule update --init --recursive
# or, more selectively for the deps you'll build:
git submodule update --init external/postgresql external/postgis \
                            external/proj external/geos external/gdal \
                            external/eigen external/spectra external/blake3
```

`scripts/build-all-deps.sh` invokes `git submodule update --init` defensively at the top before invoking the per-dep build scripts.

### CI integration

`integration.yml` on hart-server runs `scripts/build-all-deps.sh` once on bootstrap. Subsequent runs use a marker-file check: if `/opt/laplace/pgsql-18/bin/pg_config` exists AND the marker file's recorded submodule SHA matches the current submodule SHA, the build is skipped. This makes the dep-build a true cache: ~25 min on first run, sub-second on subsequent runs unless a submodule pin changes.

## Consequences

- **One acquisition shape.** Every direct dep is `git submodule update --init <path>` + `scripts/build-<dep>.sh`. New contributors don't have to learn five different acquisition stories.
- **`git bisect` works across the dep frontier.** If a behavior changes between substrate commits, and the only thing that changed is a submodule pin bump, `git bisect` over the bump commit isolates the root cause in O(log n) of the submodule's commits between the two pins.
- **Cross-machine determinism.** Same git SHA → byte-identical build artifacts on every machine. apt-supplied libraries have no such guarantee (Debian's reproducible-builds program is real but partial; substrate determinism per [RULES.md R7](../../RULES.md) needs all-the-way).
- **Compile regime aligned everywhere.** icx/icpx + `-march=${LAPLACE_TARGET_ISA}` + `-fno-fast-math -ffp-contract=off` apply uniformly across all deps. No "this lib was built with gcc -O2, that lib with icx -O3" mismatch.
- **Cost: build time.** First `build-all-deps.sh` invocation is ~25 minutes wall-clock on hart-server. Subsequent runs hit the cache. CI caches via marker file. This is a real cost paid once per fresh checkout / clean build.
- **Cost: disk.** `external/` submodules total ~530 MB checked out. `/opt/laplace/` builds total ~3.5 GB after install. Worth it for the control.
- **Cost: submodule discipline.** Every contributor must know `git submodule update --init --recursive` or equivalent. Mitigated by `scripts/build-all-deps.sh` invoking it defensively.

## Alternatives considered

- **apt for everything.** Status quo before this ADR. Rejected — failure class fired live on hart-server (the `postgis.control` mismatch), compile regime can't be set, cross-machine determinism unverifiable.
- **vcpkg for C/C++ deps.** Considered. Rejected — vcpkg's manifest-mode pinning works, but vcpkg itself is a layer between us and the dep's source. With submodules, the dep IS in our tree; with vcpkg, vcpkg fetches it. The "code against the repo" property is weaker.
- **CMake `FetchContent` for everything.** Considered. Rejected for the same reason as vcpkg + the build-time network dependency. `FetchContent` fetches at configure time; submodules fetch at clone time (or on explicit demand). `FetchContent` also doesn't pin against a specific commit unless the URL points at a tarball SHA, which is opaquer than a submodule tag.
- **Vendored copies (`external/<dep>/` not a submodule).** Considered. Rejected — vendoring means we accumulate stale copies of upstream code with no clear update path. Submodules preserve the upstream `git log` + tag history for inspection.
- **Build Intel oneAPI from source.** Considered. Rejected — closed components in oneAPI's distribution make this impractical. Vendor installer is the supported path.

## Consequences for existing ADRs

- **ADR 0015 (BLAKE3)** is amended: BLAKE3 is now `external/blake3/` submodule, not CMake `FetchContent`. The 1.5.4 version pin stays.
- **ADR 0028 (Custom-built PG + PostGIS)** is amended: scope broadens from PG+PostGIS-only to all geospatial+linalg deps. The "code against the repo" + "performance regime" rationales generalize cleanly. The original 0028 framing is preserved as the substrate of this generalization.
- **ADR 0030 (MKL/Eigen/Spectra/TBB integration)** is amended: Eigen is now `external/eigen/` submodule (not apt); Spectra is now `external/spectra/` submodule (not FetchContent). The MKL+TBB integration story is unaffected — those are oneAPI vendor components and remain at `/opt/intel/oneapi/`.

## References

- [ADR 0015 — BLAKE3-128 for entity hashing](0015-blake3-for-entity-hashing.md) (amended by this ADR)
- [ADR 0028 — Custom-built PostgreSQL + PostGIS with Intel toolchain](0028-custom-built-pg-postgis-intel.md) (amended by this ADR)
- [ADR 0030 — MKL / Eigen / Spectra / TBB integration](0030-mkl-eigen-spectra-tbb-integration.md) (amended by this ADR)
- [ADR 0032 — Unified CMake build pipeline](0032-unified-cmake-build-pipeline.md) (compatible with this ADR; build pipeline now feeds from `external/` submodules end-to-end)
- [Memory: project_code_against_repo](../../memory/project_code_against_repo.md) — origin of the "code against the repo, not the package" principle
- [Memory: feedback_no_bandaids_on_chunk0](../../memory/feedback_no_bandaids_on_chunk0.md) — companion discipline
- Commit `574216e` — initial all-deps-as-submodules implementation (PROJ, GEOS, GDAL, Eigen, Spectra, BLAKE3 added)
- Commit `ba019f4` — engine CMakeLists cutover to submodule-based deps
- Commit `42b03eb` — per-dep build scripts + build-all-deps orchestrator
