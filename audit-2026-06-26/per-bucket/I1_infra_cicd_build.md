## Bucket: I1 — infra / CI-CD / build

### Files read (42/42)
- [x] .github/actions/build-dep/action.yml
- [x] .github/actions/setup-laplace-env/action.yml
- [x] .github/workflows/_build-deploy.yml
- [x] .github/workflows/_ingest.yml
- [x] .github/workflows/atomic2020.yml
- [x] .github/workflows/ci.yml
- [x] .github/workflows/conceptnet.yml
- [x] .github/workflows/deploy-app.yml
- [x] .github/workflows/framenet.yml
- [x] .github/workflows/integration.yml
- [x] .github/workflows/iso639.yml
- [x] .github/workflows/mapnet.yml
- [x] .github/workflows/model-pipeline.yml
- [x] .github/workflows/omw.yml
- [x] .github/workflows/opensubtitles.yml
- [x] .github/workflows/propbank.yml
- [x] .github/workflows/seed-ladder.yml
- [x] .github/workflows/semlink.yml
- [x] .github/workflows/tatoeba.yml
- [x] .github/workflows/ud.yml
- [x] .github/workflows/unicode.yml
- [x] .github/workflows/verbnet.yml
- [x] .github/workflows/wiktionary.yml
- [x] .github/workflows/wordframenet.yml
- [x] .github/workflows/wordnet.yml
- [x] .gitignore
- [x] .gitmodules (full 928 lines — two reads)
- [x] CMakeLists.txt
- [x] Justfile
- [x] cmake/postgis_config_win.h.in
- [x] cmake/toolchains/gcc-deterministic.cmake
- [x] cmake/toolchains/intel-oneapi.cmake
- [x] cmake/win32_lwgeom_compat.h
- [x] deploy/README.md
- [x] deploy/linux/bootstrap-host.sh
- [x] deploy/linux/deploy.sh
- [x] deploy/linux/laplace-api.env.example
- [x] deploy/linux/laplace-api.service
- [x] deploy/linux/nginx-laplace.conf
- [x] deploy/windows/Install-LaplaceSite.ps1
- [x] deploy/windows/laplace-api.env.example
- [x] deploy/windows/publish.ps1

---

### VERIFIED: the "stop nuking the DB on every push" fix (736d941) is in place and correct

Two push-triggered workflows could reach a destructive nuke; both are now gated so push CANNOT nuke/reseed:

- **deploy-app.yml:5,25-35,98-107** — triggers on `push: branches:[main]` (paths filter) AND `workflow_dispatch`. The nuke is `if [ "${{ inputs.fresh_db }}" = "true" ]; then dotnet "$MIG" nuke --yes`. On a `push` event `inputs.fresh_db` is the empty string (not `"true"`), so the nuke is skipped; push does only `migrate up` + in-place `ALTER EXTENSION UPDATE` (lines 116-134) + publish + restart. All seed steps are gated `if: ${{ inputs.fresh_db == 'true' && !inputs.skip_seed }}` (lines 162-201) → no reseed on push. **Verified by reading the literal `if` guards.** Confidence: high.
- **integration.yml:259-322** — the destructive `db-deploy` job (nuke + reseed) is gated `if: ${{ github.event_name == 'workflow_dispatch' }}` (line 266). On push it is skipped, and everything downstream (`dotnet-test`, `regress`, `endpoint-health`, `seed-ladder`, `model-pipeline`) `needs:` it, so the whole destructive chain is dormant on push; only build/capability jobs run. **Verified.** Confidence: high.

No remaining workflow nukes or reseeds the DB on push. This is the headline positive.

---

### Findings

**1. deploy-app.yml:175-201 — SEVERITY: MEDIUM — CATEGORY: fake-test / correctness**
CLAIM: Secondary seed steps swallow failure, so a broken decomposer deploys green. Steps for `verbnet propbank ud` (177-180) and `omw framenet semlink mapnet wordframenet` (181-186) and the opt-in full corpora (196-201) run `scripts/ingest-source.sh "$s" || echo "::warning::seed $s failed (data may be absent)"`. A non-zero ingest exit becomes a warning, not a failure. VERIFIED: traced the `|| echo "::warning..."` on each loop; contrast with `unicode/iso639/cili/wordnet` (161-172) which run bare (failure propagates). The final validate (204-221) only asserts `/health/ready` and a `dog` recall (wordnet), so a silently-broken verbnet/propbank/ud/omw/framenet/bridge ingest passes the deploy. Given the "no silent scope-cut / measure don't assert" mandate this is a green-masking gap for 8+ sources. (Partial mitigation: it is explicitly framed as "tolerate absent data".) Confidence: high.

**2. _build-deploy.yml:23-25 vs deploy-app.yml:71-86 / integration.yml:207-218 — SEVERITY: MEDIUM — CATEGORY: fork / correctness**
CLAIM: The build+install invocation is forked into 3–4 drifting copies, and the `just`-based copy is admitted-broken on the runner. `_build-deploy.yml` (used by the `deploy` job of every per-source ingest workflow when `deploy=true`, the dispatch default) runs `just build` then `just install`. deploy-app.yml:71-86 inlines raw `cmake -B build … && cmake --build && cmake --install` with the comment "Rebuild + install … directly via cmake — NOT `just` (the runner's modern `just` rejects the Justfile's blank recipe lines)." integration.yml:207-218 inlines the same cmake again. So the same logic exists in Justfile `build`/`install`, deploy-app inline, integration inline, and `_build-deploy` via just — four variants. VERIFIED: Justfile recipe `install:` (lines 50-53) has a blank first body line (line 51); `db-fresh:` (101-103) and `migrate-new:` (84-86) likewise — exactly the "blank recipe lines" the deploy-app comment says modern just rejects. If that claim holds, `_build-deploy.yml`'s `just install` fails on the same runner, breaking the per-source manual-ingest deploy path. Could not run `just` here to confirm the parse error, so the rejection itself is medium confidence; the fork (4 copies) is high confidence. Convergence violation regardless. Confidence: med (failure) / high (fork).

**3. integration.yml:259-266 — SEVERITY: MEDIUM — CATEGORY: correctness (residual destructive hazard)**
CLAIM: A `workflow_dispatch` of integration.yml nukes the live `laplace` DB that deploy-app serves. `db-deploy` runs `nuke` then `up` against `laplace` (lines 278-286), the same DB deploy-app.yml seeds and serves. The job's own comment admits it: "TODO: point this at a throwaway laplace-ci DB so even a dispatch can't touch prod." The "two-DB law" (_ingest.yml:23 "laplace = CI, laplace-dev = manual") is not enforced here — integration's destructive path targets `laplace`, not a throwaway/`-dev` DB. VERIFIED by reading the nuke step DB target and the TODO. Confidence: high.

**4. Build invocation race: deploy-app.yml + integration.yml both run on push, both `cmake --install` into /opt/laplace — SEVERITY: LOW/MEDIUM — CATEGORY: correctness (race)**
CLAIM: On a push to `main` touching `engine/**`, BOTH integration.yml (push trigger, line 4) and deploy-app.yml (push trigger, line 25-35) fire. They use SEPARATE concurrency groups (`integration-${{github.ref}}` cancel-in-progress:true vs `deploy-app` cancel-in-progress:false), so they are not serialized against each other. Both configure/build into `build/` and `cmake --install` into `/opt/laplace` (integration db-deploy is dispatch-only, but integration's `engine` job at 197-233 still builds into `build/` on push, and deploy-app installs into the same `build/` + /opt/laplace). If more than one self-hosted runner is registered for the `[self-hosted, laplace]` label, these interleave on the same build tree/prefix → corrupt artifacts. With a single runner they queue and are safe. VERIFIED the triggers + disjoint concurrency groups + shared `build/` dir; runner count unknown, hence the conditional severity. Confidence: med.

**5. CMakeLists.txt:64-66 — SEVERITY: MEDIUM — CATEGORY: altitude / per-host-hardcode**
CLAIM: `CMAKE_INSTALL_RPATH` hardcodes absolute host paths `/opt/laplace/geos/lib` and `/opt/laplace/proj/lib` instead of deriving from `CMAKE_INSTALL_PREFIX` / `LAPLACE_EXTERNAL`. A build with a different prefix still bakes `/opt/laplace/...` into the RPATH of installed libs → broken on any host that isn't `/opt/laplace`. This is the "differences should be config, not hardcode" smell. VERIFIED by reading the literal `list(APPEND CMAKE_INSTALL_RPATH "/opt/laplace/geos/lib")`. Confidence: high.

**6. CMakeLists.txt:67-80 vs cmake/toolchains/gcc-deterministic.cmake — SEVERITY: MEDIUM — CATEGORY: fork / correctness**
CLAIM: The root CMakeLists hard-`FATAL_ERROR`s if `MKLROOT`/`TBBROOT`/`CMPLR_ROOT` env vars are unset (lines 73-78) and appends them to RPATH, so EVERY configure requires Intel oneAPI sourced — including a build with the `gcc-deterministic.cmake` toolchain, which sets no MKL/oneAPI paths and presumably exists to build without Intel. The gcc toolchain is therefore unusable standalone: it still demands oneAPI env + bakes oneAPI RPATH. Either the gcc toolchain is dead/decorative or the oneAPI requirement should be conditional. VERIFIED by reading both files: gcc-deterministic.cmake (whole file) sets only gcc/g++ + march flags, never MKLROOT; root CMakeLists unconditionally requires it. (Charter's "MKL-optional-default issue" likely lives in engine/CMakeLists, out of this bucket — flagging the root-level coupling I can see.) Confidence: high (coupling); med (intent of gcc toolchain).

**7. cmake/postgis_config_win.h.in:19-20 — SEVERITY: LOW — CATEGORY: correctness**
CLAIM: `POSTGIS_GEOS_VERSION 0` and `POSTGIS_PROJ_VERSION 0` are hardcoded to 0 in the Windows postgis build shim. Zero version may disable geos/proj version-gated codepaths in postgis. This is a Windows build compat hack; whether it degrades the geom extension on Windows can't be judged without the consumers (out of bucket). Noted for the Windows-build auditor. Confidence: low.

**8. ci.yml:60-62,70-76 — SEVERITY: INFO — CATEGORY: other (advisory-by-design, not fake green)**
markdown-lint uses BOTH `continue-on-error: true` AND `|| true` (double-masked); link-check uses `continue-on-error: true`. These are explicitly advisory and never gate. NOT fake-green of real tests — the real gates in ci.yml (`pipeline-validate`, `attestation-engine-law` git-diff drift gate, `anti-vocabulary-scan` with `exit "$violations"`) DO fail. Noted only so it's not mistaken for masking. Confidence: high.

**9. ci.yml:36-49 — SEVERITY: LOW — CATEGORY: correctness (robustness)**
CLAIM: The "Forbid C# attestation policy" check does `files=$(find …)` then `rg -l "$pattern" $files`. If `find` returns zero files, `rg` is invoked with no path args and reads stdin (empty in CI) — benign here but fragile; also `shopt -s globstar` is set but unused (logic uses `find`). Low-impact robustness nit. Confidence: med.

**10. _ingest.yml:74-80 / model-pipeline.yml:56-64 — SEVERITY: LOW — CATEGORY: correctness (dynamic SQL)**
CLAIM: Gate helpers interpolate `$1` into `relation_type_id('$1')` via `psql -tAc`. Unparameterized dynamic SQL, but the values are static literals authored in the workflow YAML (e.g. `IS_A 50000`), not external input → not exploitable. Style note only. Confidence: high.

**11. deploy-app.yml:214-221 — SEVERITY: LOW — CATEGORY: fake-test (weak smoke)**
CLAIM: The "recall must answer" and "embeddings" validate steps `curl -fsS` the endpoints but assert nothing about the body — a 2xx with empty/garbage JSON passes. The real gate is the preceding `-f` `/health/ready` (line 211), which the comment says fails on a hollow stack; so this is a weak-but-not-load-bearing smoke. Confidence: high.

**12. .gitignore — SEVERITY: INFO — CATEGORY: dead-code (duplication)**
CLAIM: Duplicate/triplicate blocks: the `.vscode/*` allowlist block appears twice (299-304 and 350-354), `__pycache__/`+`*.pyc` twice (262-263, 362-363), `*~` thrice (199, 357, 357-area), `*.aps`/`*.ncb` twice. Cosmetic. Secrets hygiene is otherwise correct: `*.env` (6), `secrets/` (344), and explicit `deploy/{windows,linux}/laplace-api.env` (382-383) are ignored; `*.env.example` is tracked (doesn't match `*.env`); `!engine/test/fixtures/*.bin` (369) correctly un-ignores fixtures. Confidence: high.

---

### Submodules / secrets confirmations (per charter)

- **.gitmodules (928 lines, ~310 entries):** all confirmed vendored deps — 10 core libs (postgresql, postgis, proj, geos, gdal, eigen, spectra, blake3, googletest, tree-sitter) + ~300 `tree-sitter-grammars/tree-sitter-*` grammar repos. Legitimately out of audit scope (third-party source). CI never checks them out: every workflow uses `submodules: false` and sources deps from `$LAPLACE_EXTERNAL`; integration.yml:35-49 even adds a guard that FAILS if `.git/modules` is populated (catches accidental submodule init). Good architecture. Note: working tree shows `m external/gdal` (dirty submodule pointer) — cosmetic, not in these files.
- **Secrets handling:** every workflow declares `permissions: contents: read`; NO secrets/tokens are consumed anywhere. Ingestion/migration authenticate via Postgres peer auth over the unix socket (`Host=/var/run/postgresql;Username=laplace_admin`), so no credentials transit CI. env.example files carry only dev-posture knobs (`LAPLACE_BILLING_BYPASS=true`, etc.) — low priority per charter. Clean.

### Per-host "fork" assessment (memory: differences should be config)
The Windows (IIS: Install-LaplaceSite.ps1 / publish.ps1) vs Linux (systemd+nginx: bootstrap-host.sh / deploy.sh) split is genuine platform-deployment mechanics, not forked app/pipeline code — the app is one single-origin binary configured by env files (deploy/README.md states this and it holds). NOT a violation. The real fork is finding #2 (build invocation duplicated 4×), which is internal to the build, not cross-host.

### Positives worth keeping (do not "fix")
- Determinism: both toolchains set `-fno-fast-math -ffp-contract=off` (gcc-deterministic.cmake:24-25, intel-oneapi.cmake:28-29) — correct for content-address FP determinism.
- deploy-app.yml:116-134 in-place `ALTER EXTENSION UPDATE` (content-hash version → idempotent upgrade body) is a genuine non-destructive SQL hot-deploy.
- model-pipeline.yml:33-44 idempotency job greps for "already ingested" — a real re-ingest short-circuit assertion (matches invariant 2/7 dedup-is-the-hash). consensus/relation-type gates (46-98) assert real `consensus_count >= floor`, not vacuous.
- The seed ladder order (unicode→iso639→cili→wordnet→omw/framenet/bridges, seed-ladder.yml + deploy-app.yml:161-186) respects the convergence-index anchoring (CILI before wordnet/omw; ISO-639 axis early) — invariant 6.

---

### Bucket summary
- CRITICAL: 0
- HIGH: 0
- MEDIUM: 5 (#1 masked seed failures, #2 build-invocation fork + broken `just install`, #3 integration dispatch nukes live `laplace`, #5 hardcoded /opt/laplace RPATH, #6 oneAPI hard-requirement couples gcc toolchain)
- LOW: 4 (#4 conditional, #7, #9, #11)
- INFO: 3 (#8, #10, #12)

**Single worst issue:** #1 — deploy-app.yml swallows ingest failure for 8+ secondary sources (`|| echo "::warning…"`), so a broken decomposer deploys green; combined with the weak content-blind validate smokes (#11), the deploy gate can pass with a partially-hollow substrate. (The DB-nuke-on-push danger the bucket asked me to verify is FIXED and confirmed.)
