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

## 2026-05-21 — Layer-0 bootstrap not yet executed on hart-server
**Severity:** blocking (CI integration job + all Layer-1 work — i.e., everything that needs to actually touch Postgres on hart-server)
**Reported by:** push 2026-05-21 surfaced the peer-auth probe failure
**Context:** New `integration.yml` `capabilities` job now verifies `psql -d postgres -tAc "SELECT current_user"` returns `laplace_admin` (per ADR 0019). The current runner on hart-server is still the legacy install registered under the interactive `ahart` user; the probe correctly returned `ahart on postgres` and failed CI fast with the error pointing at the bootstrap command.
**Diagnostic:**
```
Peer-auth probe: ahart on postgres
::error::Expected 'laplace_admin on postgres', got 'ahart on postgres'
::error::Run: sudo scripts/bootstrap-laplace-runner.sh bootstrap
```
The script (`scripts/bootstrap-laplace-runner.sh`) is idempotent — it tears down the legacy `/home/ahart/actions-runner` (deregisters from GitHub, stops + disables old systemd unit, archives to `/tmp/laplace-runner-prev-<epoch>`) before installing the new one under `/var/lib/laplace-runner/actions-runner` as the `laplace-runner` system account.

**Proposed resolution (run on hart-server):**
```sh
# Anthony's gh CLI must be authenticated with admin on SaltyPatron/Laplace
# so the script can mint registration + remove tokens via the gh API.
cd ~/Projects/Laplace
git pull origin main

# One-time Layer-0 bootstrap (idempotent — safe to re-run):
sudo scripts/bootstrap-laplace-runner.sh bootstrap

# Verify the new state:
sudo scripts/bootstrap-laplace-runner.sh status

# Re-trigger CI:
gh workflow run integration.yml
# or just: git commit --allow-empty -m 'ci: re-trigger after runner bootstrap' && git push
```
After bootstrap completes, the runner deregisters as `ahart`/old-name and re-registers as `hart-server` running as `laplace-runner`. The peer-auth probe will then return `laplace_admin on postgres` and CI proceeds through `build → db-ensure → extension-smoke-test`.

If something goes wrong, the script supports `sudo scripts/bootstrap-laplace-runner.sh reset` (requires typing `RESET`) to fully tear down and start fresh.

---

## 2026-05-21 — Spectra library not installed
**Severity:** medium (needed for Laplacian eigenmaps in physicality pipeline)
**Reported by:** machine survey
**Context:** Preparing for AI model ingestion pipeline implementation (Chunk 6/7).
**Diagnostic:** `find /usr/include /usr/local/include -name Spectra -type d` returns empty. Spectra is header-only.
**Proposed resolution:** Per [STANDARDS.md](../../STANDARDS.md), Spectra ships via CMake `FetchContent` — pinned to a tagged release. No system install needed; the engine's `CMakeLists.txt` will fetch + vendor it during Chunk 6. Reclassified from "needs apt install" to "vendored at build time."

## 2026-05-21 — tree-sitter library not installed
**Severity:** medium (needed for code decomposition in `CodeDecomposer`)
**Reported by:** machine survey
**Context:** Preparing for code ingestion via `IDecomposer` interface (post-Chunk-7).
**Diagnostic:** `dpkg -l libtree-sitter-dev` returns no result.
**Proposed resolution:** `sudo apt install libtree-sitter-dev`. Defer until first code-ingestion source is implemented.

## 2026-05-21 — AVX-512 not available on dev machine
**Severity:** informational (not blocking; deployment-target consideration)
**Reported by:** machine survey
**Context:** Dev machine is i7-6850K (Broadwell-E), AVX2 only.
**Diagnostic:** `lscpu` shows `avx avx2`, no `avx512*`.
**Proposed resolution:** Design hot kernels with both AVX2 and AVX-512 code paths (CPU dispatch). Performance benchmarks on this box reflect AVX2; AVX-512 deployment targets require separate benchmarking.

---

## Resolved

### 2026-05-21 — BLAKE3 standalone library not installed
**Resolved by:** ADR 0015 (BLAKE3-128 for entity hashing) — landed via `FetchContent` in the engine's `CMakeLists.txt`. Pinned to v1.5.4. No system install needed.
**Note:** This blocker was originally framed as "low priority — using xxHash3-128 instead." That framing was wrong: BLAKE3 is the canonical choice now (ADR 0003 → 0015), and it's bundled at build time, not installed.
