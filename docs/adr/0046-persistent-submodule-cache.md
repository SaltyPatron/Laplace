# ADR 0046: `/opt/laplace/external/` as the canonical source for dependency checkouts

## Status

**Accepted** — 2026-05-24
**Authors:** Anthony Hart

Amends:
- [ADR 0033](0033-all-deps-as-submodules.md) — keeps submodule policy + .gitmodules as the pin oracle, but replaces "per-developer / per-CI-runner `git submodule update --init --recursive`" with a persistent host-level checkout maintained by `scripts/sync-external.sh`.
- [ADR 0038](0038-unified-deps-cmake-pipeline-gcc-toolchain.md) — extends with `$LAPLACE_EXTERNAL` (default `/opt/laplace/external/`) as the canonical source location read by `external/CMakeLists.txt` + `engine/CMakeLists.txt`.

## Context

ADR 0033 mandated all direct C/C++ deps as submodules under `external/` and described init as "per-developer / per-CI-runner step (not privileged)" via `git submodule update --init --recursive`. ADR 0038 then wrapped the build into one `cmake --build build/deps -j` driven by `external/CMakeLists.txt`.

In practice that model fired three recurring failures on the self-hosted runner over several days of integration runs:

1. **Per-job submodule init was slow and fragile.** Every CI job that needed dep sources re-ran `git submodule update --init --recursive` against upstreams. With 303 tree-sitter grammars plus ten primary deps, this is hundreds of clones per job, hitting upstream rate limits and adding minutes of wall-clock to every run.
2. **`--reference` cache + workspace `.git/modules/` interleaving was unstable.** Earlier iterations tried `--reference /opt/laplace/external` against bare clones, with workspace submodules sharing object stores. This required the workspace `.git/modules/<name>/` to point back at the bare entry's object store correctly, and `git submodule update` had several distinct failure modes depending on whether the bare entry, the workspace gitlink, and `.gitmodules` agreed on the SHA (commits `aa26cd08`, `0648b15`, `04929a2`, `cd1e24d`, `3c8a70f`, `9cf9bc2` chronicle the fight).
3. **Ownership cliff between root (bootstrap) and laplace-runner (CI/dev).** `sudo scripts/bootstrap-laplace-runner.sh` created `/opt/laplace/external/` as root-owned in some passes, then CI as `laplace-runner` couldn't write into it — or git's `safe.directory` check refused to operate on mismatched-ownership repos. Commits `c87eff6`, `e939a63`, `3a23c88` cycled through chown/perm fixes.

The settled model that produced a green Integration run (commit `9897fa4`, 2026-05-24) is:

- `/opt/laplace/external/` is a persistent directory with `mode 2775` group `laplace-runner` (setgid → group-inherit).
- Each submodule is a **non-bare working tree** under `/opt/laplace/external/<name>/`. No bare repos, no `--reference`, no workspace `.git/modules/` interleaving.
- `scripts/sync-external.sh` is the single source-of-truth maintainer: for each `.gitmodules` entry, clone if missing, un-bare if a legacy bare entry is found, fetch only if the pinned SHA isn't already present, then `git reset --hard <pinned>` to bring the working tree to the pin. Idempotent; no-op when nothing changed.
- The substrate build reads dep sources via `-DLAPLACE_EXTERNAL=<path>` (default `/opt/laplace/external`). `external/CMakeLists.txt` and `engine/CMakeLists.txt` both reference `${LAPLACE_EXTERNAL}/<dep>` rather than `${CMAKE_SOURCE_DIR}/external/<dep>`.
- In CI, no workspace `external/` is checked out (`actions/checkout` with `submodules: false`). `scripts/sync-external.sh` runs once in the `capabilities` job; all downstream build jobs (proj/geos/gdal/postgresql/postgis/tree-sitter/engine) read directly from `$LAPLACE_EXTERNAL`.
- Local developers may keep a workspace `external/` for editing (per ADR 0033 ergonomics), but it is no longer load-bearing for the build.

This model collapses the three failure modes above:
1. **No per-job network fetches.** Submodules are already on disk at the pinned SHA. New job starts use existing checkouts.
2. **No `--reference` interleaving.** One non-bare checkout per dep at `/opt/laplace/external/<dep>/.git`. Git operates on it as an ordinary repo.
3. **One owner, one group.** `bootstrap-laplace-runner.sh` creates `/opt/laplace/external/` as `laplace-runner:laplace-runner mode 2775`. Both `ahart` (developer) and the CI runner are in the `laplace-runner` group, so both can write.

## Decision

**`/opt/laplace/external/` is the canonical source for dependency checkouts.** Concretely:

1. **Bootstrap creates the directory only.** `scripts/bootstrap-laplace-runner.sh` (Layer 0, the one legitimate sudo surface) creates `/opt/laplace/external/` as `laplace-runner:laplace-runner mode 2775`. It does **not** populate the directory. (Per the memory `setup-host_is_one_time_NEVER_for_recovery`: bootstrap is one-time machine setup, not project state.)

2. **`scripts/sync-external.sh` populates and maintains the cache.** It is the only writer to `/opt/laplace/external/<dep>/`. It runs in the CI `capabilities` job before any build job, and developers may invoke it directly after pulling new `.gitmodules` pins. Idempotent; safe to call repeatedly.

3. **CMake reads from `$LAPLACE_EXTERNAL`.** `external/CMakeLists.txt` (the `ExternalProject_Add` orchestrator per ADR 0038) and `engine/CMakeLists.txt` reference dep sources via `${LAPLACE_EXTERNAL}/<dep>` (default `/opt/laplace/external`). The env var / cache var allows local developers to point at an alternate checkout if needed.

4. **CI does not init workspace submodules.** `actions/checkout` runs with `submodules: false, clean: false`. Persistent `_work` keeps `build/deps/*` stamp files + `build/*` CMake artifacts intact between runs. Stamp-based ExternalProject + Ninja incremental + dotnet incremental together make no-change re-runs effectively no-op per dep.

5. **Local developer ergonomics.** Developers may run `git submodule update --init <path>` for editing convenience (especially for tree-sitter grammar work). The workspace `external/<dep>` is not consumed by the substrate build; only `/opt/laplace/external/<dep>` is. Workspace and cache may temporarily diverge; the cache wins for the build.

## Consequences

- **Faster CI.** No upstream network round-trips per job. Sub-second sync in the `capabilities` job when no submodule pin moved. Dep-build is then stamp-cached at the ExternalProject layer.
- **No upstream rate limits.** Particularly relevant for the 303 tree-sitter grammars.
- **Pin-bump flow is simpler.** Bump `.gitmodules` → push → `scripts/sync-external.sh` fetches the new SHA on the next CI run → ExternalProject stamps invalidate → rebuild → green. No per-job re-init dance.
- **Build invariants encoded in one place.** `scripts/sync-external.sh` is the single oracle for "what version of each dep is the build reading." `git -C /opt/laplace/external/<dep> rev-parse HEAD` is the answer for any dep, on any machine.
- **Cross-host reproducibility unchanged.** Same `.gitmodules` pin + same `sync-external.sh` → same `/opt/laplace/external/<dep>/.git/HEAD` SHA on every host.
- **Cost: ownership discipline.** `/opt/laplace/external/` must be writable by everyone who runs `scripts/sync-external.sh` (developer accounts and the CI runner). Enforced by `bootstrap-laplace-runner.sh` setting `mode 2775` group `laplace-runner` and adding developer accounts to that group.
- **Cost: bootstrap surface.** One more directory `bootstrap-laplace-runner.sh` creates — but it's purely additive (the surface was already creating `/opt/laplace/{lib,share,include,bin,...}`).
- **Cost: documentation.** Workspace `external/` is no longer the canonical source; OPERATIONS.md and any onboarding material that says "run `git submodule update --init`" must be amended to reference `scripts/sync-external.sh` and `/opt/laplace/external/`.

## Alternatives considered

- **Per-job `git submodule update --init --recursive` against workspace** (the ADR 0033 default). Rejected — hundreds of upstream fetches per job; rate-limited; slow; the fragility chronicled in the commit cascade above.
- **Bare `/opt/laplace/external` + workspace `--reference`.** Rejected — workspace `.git/modules/<name>/` linkage to the bare object store was the source of recurring "fatal: not a git repository" + "object store mismatch" failures. Non-bare-only is the simpler model.
- **`scripts/bootstrap-laplace-runner.sh` populates the cache** (prior model — commit `3a23c88` tried this). Rejected per the "bootstrap is machine setup, not project state" principle (memory: `setup-host_is_one_time`). `.gitmodules` is project state; project state belongs in pipeline/dev hands, not in the privileged Layer 0 surface. Splitting out into `sync-external.sh` keeps bootstrap idempotent on machine setup alone.
- **Workspace `external/` checked out in CI + symlinked into `/opt/laplace/external`.** Rejected — symlinks across the workspace/persistent boundary survive cleanly but offer no caching benefit (CI's workspace is reused per `clean: false` anyway), and any `actions/checkout` with `submodules: recursive` would still trigger the failure class. Persistent host-level directory is the cleaner separation.
- **`vcpkg` overlay against the cache.** Rejected — adds vcpkg as a tool layer between us and the dep sources. Same "code against the repo" objection as in ADR 0033.

## Consequences for existing ADRs

- **ADR 0033 amended:** the "Submodule init in the bootstrap flow" section is superseded by `scripts/sync-external.sh`. The `external/<dep>/` paths in the dependency table now resolve to `${LAPLACE_EXTERNAL}/<dep>/` for build consumption; the repo-local `external/<dep>/` remains as developer ergonomics. The marker-file caching in the "CI integration" section is superseded by ExternalProject stamp files + sync-external.sh idempotence.
- **ADR 0038 amended:** the `external/CMakeLists.txt` ExternalProject project now reads sources from `${LAPLACE_EXTERNAL}/<dep>/`, with `LAPLACE_EXTERNAL` defaulting to `/opt/laplace/external/` and overridable via env var or CMake `-D`.

## References

- [ADR 0033 — All direct deps as git submodules](0033-all-deps-as-submodules.md) (amended)
- [ADR 0038 — Unified deps CMake pipeline + gcc toolchain](0038-unified-deps-cmake-pipeline-gcc-toolchain.md) (amended)
- [`scripts/sync-external.sh`](../../scripts/sync-external.sh)
- [`scripts/bootstrap-laplace-runner.sh`](../../scripts/bootstrap-laplace-runner.sh) — `bootstrap_external_dirs` step
- [`.github/workflows/integration.yml`](../../.github/workflows/integration.yml) — `capabilities` job + `LAPLACE_EXTERNAL` env
- Refactor commit trail: `aa26cd08` (--reference attempt), `0648b15`, `04929a2`, `cd1e24d`, `3c8a70f`, `9cf9bc2`, `0d104c7` (cache population out of bootstrap into pipeline), `3a23c88` (persistent + sudoless), `e2bddfd` (drop per-job submodule init), `c87eff6`, `e939a63` (chown to laplace-runner), `9897fa4` (RPATH from oneAPI env — green run).
- Memory: [`setup-host_is_one_time_NEVER_for_recovery`](../../memory/feedback_setup_host_is_one_time.md) — origin of the "bootstrap is machine setup, not project state" framing.
- Memory: [`project_code_against_repo`](../../memory/project_code_against_repo.md) — root rationale for repo-local source dependencies.
