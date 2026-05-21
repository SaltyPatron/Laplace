# Laplace — Open Blockers

Format: open blockers first (most recent at top). Resolved blockers move to a "Resolved" section at the bottom (chronological).

```
## YYYY-MM-DD — <blocker title>
**Severity:** blocking | high | medium | low
**Reported by:** <agent or user>
**Context:** <what we were trying to do>
**Diagnostic:** <what we found>
**Proposed resolution:** <or "open — needs investigation">
```

---

## Open

## 2026-05-21 — Spectra library not installed
**Severity:** medium (needed for Laplacian eigenmaps in physicality pipeline)
**Reported by:** machine survey
**Context:** Preparing for AI model ingestion pipeline implementation (Chunk 7).
**Diagnostic:** `find /usr/include /usr/local/include -name Spectra -type d` returns empty. Spectra is header-only; needs download from https://github.com/yixuan/spectra.
**Proposed resolution:** During Chunk 7 prep: `git clone https://github.com/yixuan/spectra engine/third_party/spectra` and `target_include_directories(laplace_engine PRIVATE engine/third_party/spectra/include)` in CMakeLists.txt.

## 2026-05-21 — tree-sitter library not installed
**Severity:** medium (needed for code decomposition in CodeDecomposer)
**Reported by:** machine survey
**Context:** Preparing for code ingestion via `IDecomposer` interface.
**Diagnostic:** `dpkg -l libtree-sitter-dev` returns no result. `apt-cache search` shows the package is available.
**Proposed resolution:** `sudo apt install libtree-sitter-dev`. Defer until first code-ingestion source is implemented.

## 2026-05-21 — BLAKE3 standalone library not installed (low priority — using xxHash3-128 instead)
**Severity:** low (we use XXH3-128, not BLAKE3 — see decisions.md)
**Reported by:** machine survey
**Context:** Initial hash-function selection.
**Diagnostic:** BLAKE3 only present as LLVM-15 internal header; no standalone lib.
**Proposed resolution:** None needed — XXH3-128 is the chosen hash function. Close this blocker once decision is locked in DESIGN.md (it is).

## 2026-05-21 — AVX-512 not available on dev machine
**Severity:** informational (not blocking; deployment-target consideration)
**Reported by:** machine survey
**Context:** Dev machine is i7-6850K (Broadwell-E), AVX2 only.
**Diagnostic:** `lscpu` shows `avx avx2` flags but no `avx512*`.
**Proposed resolution:** Design hot kernels with both AVX2 and AVX-512 code paths (CPU dispatch). Performance benchmarks on this box reflect AVX2; AVX-512 deployment targets require separate benchmarking.

---

## Resolved

(none yet)
