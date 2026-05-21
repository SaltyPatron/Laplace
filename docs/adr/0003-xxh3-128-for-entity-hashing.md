# ADR 0003: XXH3-128 for entity hashing

## Status

**Superseded by ADR 0015 (BLAKE3-128)** — 2026-05-21

## Context

Need a 128-bit hash for entity content addressing. XXH3-128 (libxxhash 0.8.1) is already installed on the dev box via apt; fast on small inputs (most of our hashes are 4-256 bytes).

## Decision

Use XXH3-128 stored as `bytea(16)` in PG.

## Consequences

- Already installed; no FetchContent needed.
- Fast on small inputs (codepoint hashes, short Merkle compositions).

## Alternatives considered

- **BLAKE3**: cryptographic, SIMD-accelerated. Considered but rejected at this stage for "build complexity not worth it."

## References

- [STANDARDS.md](../../STANDARDS.md)

## Why superseded

User chose BLAKE3 for SIMD acceleration, cryptographic strength, and future-proofing (signed substrate snapshots). See [ADR 0015](0015-blake3-for-entity-hashing.md).
