# Chess line geometry gate (Phase A) — 2026-08-03

## Setup

Deterministic QGD transposition pair (same as `ChessLineIdentityTests`):

| Path | SAN | `LineId` (hex) |
|---|---|---|
| Direct | `d4 d5 c4 e6` | `ee9c7c0839abb75ba7e344be210d265c` |
| Transposed | `c4 e6 d4 d5` | `da14dcf0018b2e5f8579a47cf15f3718` |
| Shared final position | — | `f3da4c4dae94de1b9a60aeece3b5f4a7` |

Compose offline via `ChessCompose.Position` / `ChessCompose.LineId` (content-hash, O(tier) — same spine as text). Frechet via `laplace_frechet_4d` on `ST_MakeLine` of **realized position coords** (the operand `entity_curve(line)` builds once a trajectory is deposited). Packed-trajectory Frechet kept only as the Rule #3 counterexample.

Gate test: `ChessLineGeometryGateTests` (`Tier=integration`).

## Measured (live `laplace_frechet_4d`)

| Metric | Value | Role |
|---|---|---|
| Frechet (realized position-coord polylines) | **0.011722527393980933** | shape — separates |
| Angular distance (line centroids) | 0.018972576996684584 | locality only |
| Hilbert equal (line centroids)? | **false** | locality prefilter OK; not identity |
| Frechet on packed `Trajectory.Build` verts | 3.2372903353793712 | **bogus** — different number; never admit |
| Same final position id? | yes | transposition |
| Same `LineId`? | **no** | path identity |

Lexical Cat/Act baseline (spec 05, measured 2026-08-02): ang≈0 on centroids, Frechet≈1.78 on `entity_curve`. Chess QGD shows the same **law** at a smaller scale: Frechet separates paths that share a destination; Hilbert/centroid is not “same opening.”

## What this says about a chess perfcache

Lexical t0 (`laplace_t0_perfcache.bin`) is the resident **codepoint → coord** floor. Chess already composes positions from that (`ChessCompose.ComposeToken` → codepoint records → Merkle + centroid). So:

1. **Not Syzygy-as-perfcache.** Finish facts are substrate rows after closings ingest. Vault `.rtbw` is packaging only.
2. **Not packed trajectory as geometry.** Shape peers need realized child coords (position coords ordered by line ordinal) — same defect `word_shape_peers` already fixed.
3. **What a real chess accelerator would be** (when shape peers / Hilbert prefilter hit scale):
   - A resident **position_id → coord** (and maybe Hilbert) floor for boards that appear in catalog lines + common openings — mirror of t0 for the chess ladder’s position tier — so `entity_curve` / Frechet admission does not join `physicalities` per vertex on every peer probe.
   - Optional: precomposed opening-line curves for the ECO catalog once openings are LINEs (Phase B).
   - In-process `PositionMemo` already exists for ingest; a durable mmap is the read-side twin, not a second identity space.
4. **Until that blob exists**, compose-time coords + deposited physicalities are enough to prove law and to implement prefix/board match; shape-peer SQL can land after openings-as-lines, gated the same way as `word_shape_peers_fast` (Hilbert ball → Frechet on curves).

## Gate status

- [x] Frechet separates QGD transposition on realized curves
- [x] Hilbert alone is not “same opening”
- [x] Packed-trajectory Frechet is a different, non-shape number
- [x] Phase B: openings compose as LINE + trajectory + name on line (bridge stamp on final board)
- [x] Phase C: closings catalog live (`HAS_WDL`/`HAS_DTZ` = 200 on this box after smoke); `chess_syzygy_line`
- [x] Phase D reads: `chess_distance_to_syzygy`, `chess_missed_finish`; expand helper `ChessExpandUnexplored`
- [ ] Extension rebuild/install required before new SQL (`chess_opening_shape_peers`, DTB/missed-finish) appears in `api()`
- [ ] Re-seed openings for line entities on a fresh box; entity_curve Frechet parity once both QGD lines deposited
