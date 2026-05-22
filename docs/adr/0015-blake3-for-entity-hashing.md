# ADR 0015: BLAKE3-128 for entity hashing (raw bytes, no casts, no hex)

## Status

**Accepted** — 2026-05-21 (supersedes ADR 0003)
**Amended** — 2026-05-22: BLAKE3 acquisition switched from CMake `FetchContent` to git submodule at `external/blake3/` per [ADR 0033](0033-all-deps-as-submodules.md). Version pin unchanged (1.5.4). The `add_subdirectory(${external}/blake3/c)` pattern from `engine/CMakeLists.txt` produces the same `blake3` CMake target as before; downstream linkers are unaffected.

## Context

Initial choice was XXH3-128 (ADR 0003) — already apt-installed, fast on small inputs. User pushed back: BLAKE3 is the better long-term choice (SIMD-accelerated, cryptographic strength, future-proofing for signed substrate snapshots).

Also a concern about casts / hex conversions / `text` vs `bytea` representations.

## Decision

Use **BLAKE3** (official C implementation, git submodule at `external/blake3/` pinned to `1.5.4` per [ADR 0033](0033-all-deps-as-submodules.md)), **truncated to 128 bits**.

Stored as `bytea(16)` in PG; `hash128_t = {uint64_t hi, lo}` in C engine; `byte[16]` / POD struct in C# via `[StructLayout(Sequential)]`.

**Hash discipline (raw bytes end to end):**
- No `bytea ↔ text` casts in code paths.
- No hex encoding / decoding outside debug-only paths.
- No `varchar` / `text` for hash storage.
- No `tolower` / `toupper` / normalization.
- All Postgres comparisons via `bytea` native byte-comparison (identical to memcmp).
- C# never uses `string` for hash values; never `BitConverter.ToString`.

Reusable helpers only — `hash128_from_bytes`, `hash128_merkle`, `hash128_equals`, `hash128_zero`, `hash128_compare`. No inlined `blake3_hasher_*` calls outside these helpers.

## Consequences

- BLAKE3's SIMD parallelism (verified: SSE2/SSE4.1/AVX2/AVX-512 variants build successfully) helps at ingestion scale.
- Cryptographic strength enables signed substrate snapshots / supply-chain verification of Synthesis outputs (future use).
- Build adds a small dependency (~5 .c files + ~5 .s files for assembly SIMD variants).
- Zero-cast discipline matches the user's "no excessive conversions" requirement.

## References

- ADR 0003 (superseded)
- [STANDARDS.md](../../STANDARDS.md) — "Hash discipline (no casts, no hex)" section
- ADR 0016 (reusable helpers — companion discipline)
- [GitHub Discussion #10](https://github.com/SaltyPatron/Laplace/discussions/10)
