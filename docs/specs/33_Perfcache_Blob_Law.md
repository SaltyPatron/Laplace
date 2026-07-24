# 33 — Perfcache blob law (the two-tier storage contract)

Written 2026-07-18, generalizing the UCD/t0 perfcache law before blobs three and four
land. Spec-class: binding, annotate-on-supersede.

## Why this exists

Serving keeps migrating hot read paths out of Postgres into mmap'd deterministic
blobs: tier-0 geometry/segmentation (`laplace_t0_perfcache.bin`), the highway relation
law (`laplace_highway_perfcache.bin`), with the factor blob (model-lane read path,
`.scratchpad/26` item B / docs/specs/19 candidate b — tracked as GH #526 since the
2026-07-20 scratchpad drain) and the GenCorpus blob
(generation lane, GH #409) designed. That is a de facto two-tier storage
architecture. It stayed implicit, and the authority question already bit once: an
implementation seeded the DATABASE from the t0 blob — inverting the derivation — and
had to be hunted down. This spec makes the contract explicit so every future blob
inherits it instead of re-fighting it.

## The law

1. **Postgres is the system of record. Blobs are derived artifacts.** Every blob is a
   deterministic function of (raw source data and/or DB state + the generating code).
   The rebuild direction is ONE-WAY: source/DB → blob. Seeding or repairing DB state
   from a blob is a violation, always — the t0 incident is the standing example
   (UnicodeDecomposer is the single origin: it seeds the DB from raw UCD AND emits
   the t0 blob; the blob never feeds back).
2. **Deterministic and CI-gated.** Same inputs → byte-identical blob. Determinism is
   a gate (double-generate and compare), not a hope. Emit is source-hash-gated:
   unchanged inputs skip the write (write-if-changed).
3. **Self-describing and staleness-detectable at load.** Every blob carries a header
   with format version + BLAKE3 body CRC; loaders verify before mmap use. A blob
   whose generating inputs changed must be detectably stale — key the header on the
   input fingerprint, and refuse (or warn-and-refuse per caller policy) on mismatch.
   Never silently serve a stale blob.
4. **Load once, share everywhere.** Postmaster prewarm inherits the mapping into
   every backend (PG side, gated on the `laplace_substrate.perfcache_path` GUC);
   process-global load with an already-loaded guard on the app side (unguarded
   re-load/Unload is the known test-suite flake class — fixtures must never unload).
5. **Scoping is data, not code.** Where a blob has coverage scopes (t0: ASCII / BMP /
   all-codepoints for embedded-vs-server), the scope is declared in the header and
   validated at load. The highway blob is universal by design. New blobs declare
   their scoping rule here when they land.
6. **Retention and versioning.** A format change bumps the header version; loaders
   reject unknown versions (no silent best-effort parse). Blobs are evictable at
   will — deleting one costs a regeneration, never data.

## Current and planned blobs (update this table as they land)

| Blob | Inputs | Consumer | Status |
|---|---|---|---|
| `laplace_t0_perfcache.bin` | raw UCD (UnicodeDecomposer single origin) | tier-0 geometry/segmentation, native + app | live |
| `laplace_highway_perfcache.bin` | relation manifest codegen | highway mask bit ops, zero-SQL gating | live |
| factor blob (name TBD at build) | deposited factor trajectories (DB) | model-lane pair scoring / row top-k / forward walk (pointer arithmetic instead of varlena SPI fetches) | designed — GH #526 (was `.scratchpad/26` item B, drained 2026-07-20) |
| GenCorpus blob (name TBD at build) | generation corpus (DB) | `walk_text` / generation lane (kills the >240 s cold build) | prescribed — GH #409 |

## Rule of engagement for new blobs

A new blob lands only with: (a) its row in the table above; (b) the one-way rebuild
path implemented and the reverse path structurally absent; (c) determinism +
staleness gates wired into CI or the loader; (d) its scoping rule stated. If any of
those is missing, it is not a perfcache blob — it is a cache bug waiting to be found.
