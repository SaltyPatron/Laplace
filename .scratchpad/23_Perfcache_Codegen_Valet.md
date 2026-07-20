<!-- DRAINED 2026-07-20 — checklist superseded by GitHub issues.
     Steps landed: CMake DEPENDS rewire (engine/core/CMakeLists.txt:139-155),
     gitignored blobs (.gitignore:414-418), --scope full.
     Still open, tracked in #382: C# valet routing, highway source-hash header parity.
     Separately: #524 (highway blob has no BLAKE3 CRC / input fingerprint, spec 33 law 3)
     and #503 (skip t0 re-CRC on per-backend remap).
     Open work lives in GitHub issues. -->

# 23 — Perfcache codegen via the decomposer valet (source-hash-gated, gitignored)

Author-directed 2026-07-12. Make the perfcache blobs (codepoint T0 **and** highway)
generated-into-tree, source-controlled-in-spirit (gitignored, not committed — no LFS),
testable, and free of the multi-minute-crawl-every-build / "emit must exist" gymnastics.

## Principles (author-locked)

1. **Valet pattern, generic across sources.** The C# decomposer ORCHESTRATES only —
   resolves + shuttles source path(s) via the CLI. Tree-sitter + native C/C++ do the
   heavy lifting (extract raw product from packaging → compute records/tables → emit).
   "C# and SQL orchestrate; C/C++/SPI are marshalled and do the work." The generation
   LOGIC is native (invoked by the build's codegen step AND reachable via the C# valet);
   no dotnet-in-the-C++-build dependency.
2. **Two outputs, one parse.** From one UCD read: (a) DB seed attestations (decomposer,
   already via UnicodeSeed P/Invoke), (b) the generated perfcache artifact. Both trace to
   native `laplace_unicode_seed_compute` — guaranteed-consistent. **Never seed DB from the
   blob** (fix `EnsureComputed`).
3. **Gitignored + source-hash no-op.** The generated artifact is gitignored. Its header
   carries a **source-hash** = hash(UCD-XML bytes ⊕ DUCET bytes ⊕ generator-version). The
   codegen step computes the current source-hash; if the existing artifact's header
   matches → **no-op** (skip the crawl); else regenerate + restamp. Codegen `DEPENDS` on
   the UCD source (not on laplace_core) so a relink never re-crawls.
4. **Determinism test.** Force-regenerate → assert byte-identical (deterministic) / the
   source-hash gate holds. "Different code in the future → test fails."
5. Generalizes to the **highway** perfcache (its source-of-truth is the manifest TOML;
   same source-hash-gate discipline, already codegen'd by codegen-attestation-law.py).

## Current substrate (verified)

- `engine/core/src/unicode_seed.cpp` = the one compute (`laplace_unicode_seed_compute`),
  called by BOTH the emit tool (C++) and the decomposer (C# P/Invoke `UnicodeSeed.Compute`).
- `engine/core/tools/ucd_tables_emit/main.cpp`: compute → tree-sitter SAX parse
  (`laplace_ucd_xml_parse`) for break props/ccc/decomp → assemble blob w/ 128-byte header
  (magic, version, ucd_version[8], uca_version[8], counts, offsets) → write .bin.
- CMake `engine/core/CMakeLists.txt:116-166`: extract XML → emit .bin → determinism target
  re-runs emit + `compare_files`. Gymnastics: "blob is a pure function — don't DEPENDS the
  tool or every relink re-crawls" (:130); order-only `add_dependencies(laplace_t0_perfcache
  laplace_ucd_tables_emit)` (engine/CMakeLists.txt:225). Wave 1 retires this gymnastics.
- Codegen precedent: `scripts/codegen-attestation-law.py` (add_custom_command, DEPENDS on
  manifest TOML), mix of committed header (highway_manifest.h) + gitignored src/generated/*.c.
- **FIXED (Phase 0 / `a2a3b32`):** `UnicodeDecomposer.EnsureComputed` always computes from
  raw UCD via `UnicodeSeed.Compute` — never reads `CodepointPerfcache.Records` to seed the DB.
  The blob remains a sibling OUTPUT of the same native compute, not a seed input.

## Implementation sequence

1. **Source-hash header + no-op gate** — **DONE (Phase 0).** `source_hash` in
   `perfcache_format.h`; emit tool no-ops when header matches.
2. **CMake rewire.** Codegen `add_custom_command` DEPENDS on the UCD XML + DUCET + the tool
   source; drop the order-only "emit must exist" dance. Determinism target compares against
   a regenerate (now cheap: gate no-ops unless sources changed). → Wave 1.
3. **Gitignore** the generated blob(s) + add the artifact dir to `.gitignore`; ensure a
   fresh checkout generates once then no-ops. (Phase 0 / Wave 1.)
4. **Fix the seed-from-blob violation** — **DONE (Phase 0).** `EnsureComputed` always
   computes from raw UCD; both hosts carry raw UCD.
5. **Valet routing (C#).** A CLI path so the decomposer valet can (re)generate the perfcache
   through the same native machinery it uses to seed — one entry, two outputs. Generic hook
   so other sources can register a codegen artifact.
6. **Highway parity.** Apply the source-hash-gate to the highway perfcache codegen.
7. **(later, own reseed) Unihan scope tiers + credit attestations** — see
   project_source_credit_attestations + the campaign reseed queue.

Steps 1+4 are landed (Phase 0). Remaining core = Wave 1 CMake + `--scope` + loader.
Nothing in the blob path owes a reseed (calculated-layer); Unihan/credit/scope items do
and sequence separately.
