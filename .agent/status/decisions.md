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

## 2026-05-21 — XXH3-128 for entity hashing
**By:** initial framework
**What:** Use libxxhash3's 128-bit variant for entity content hashing. Stored as `bytea(16)` in Postgres.
**Why:** SIMD-vectorized, fast, 128-bit collision-resistance comfortable for ~10¹⁸ entities. Already installed (libxxhash 0.8.1). BLAKE3 considered but rejected — cryptographic strength not needed (we control all ingested content); build-from-source overhead.

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
