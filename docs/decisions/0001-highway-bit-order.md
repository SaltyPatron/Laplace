# 0001 — Highway bit assignment: alphabetical codegen vs append-only registry

Status: **PROPOSED — operator decision pending.** Written 2026-07-18 from the forest
audit. Implementation, whichever way the decision goes, is its own GH issue; nothing
changes until decided AND the next operator-ordered full reseed is scheduled.

## The problem

Highway bits (the 256-bit relation-type channel bank on every entity and attestation)
are assigned **alphabetically by canonical name at codegen**. Adding ANY relation
renumbers approximately every existing bit. The law that follows — "regenerate, never
backfill" — means every relation addition owes a **full reseed**, and until that
reseed runs, stored masks decode against the wrong layout (mask-band filtering against
older rows is silently wrong — GH #413).

This is a recurring, compounding tax, paid repeatedly in one month: the manifest grew
189 → 194 → 203 canonicals across the model-lane, credit/license, and modality
campaigns, each step re-queueing a reseed (`.scratchpad/24`). Every future modality,
governance band, or source-credit addition pays it again. The reseed itself is
hours-scale today and grows with the substrate.

## What the current design buys

- **Purity**: the bit layout is a deterministic function of the TOML alone — no
  hidden state, no registry to maintain. The CI policy job proves codegen
  determinism by double-regeneration.
- **Density**: bits are always contiguous, no holes from retired relations.
- **Simplicity**: codegen is a sort; nothing to validate beyond name uniqueness.

## The alternative: explicit bit assignment in the manifest (append-only registry)

Add a required `bit = N` field to each `[[relation]]` in `relation_types.toml`.
Codegen validates (unique, in-range, no gaps beyond retired tombstones) instead of
assigning. New relations take the next free bit; existing bits never move.

- **Kills the reseed class**: adding a relation no longer touches existing masks —
  reseeds are owed only when semantics change (rank/family edits, or genuinely
  re-masking old rows to gain the new bit's coverage on historical data).
- **Kills the stale-layout window** (#413's defect class): a stored mask is valid
  forever against the layout that wrote it, because there is only one layout.
- **Stays deterministic**: the TOML remains the single input; the registry IS the
  TOML. The CI determinism gate is unaffected.
- Costs: one more field per relation (one-time mechanical edit, generatable from the
  current alphabetical layout); the "sorted = assigned" aesthetic dies; codegen gains
  a validation pass; merge conflicts on concurrent relation additions now conflict on
  bit numbers (which is a feature — the conflict is real).
- Capacity note: 256 bits, 203 canonicals live (aliases add none; DEP_*/FEAT_*
  already collapse to family roots per GH #399). Headroom exists but is finite
  either way; family-collapse policy is the capacity lever, not bit order.

## Recommendation

Adopt the explicit `bit` field, frozen from the layout of the **next** operator-ordered full
reseed (the one `.scratchpad/24` already owes). That reseed was being paid anyway;
freezing the layout at that moment makes it the last bit-order reseed the project
ever pays. The purity argument is fully preserved because the assignment lives in the
manifest itself — nothing becomes hidden state.

## Decision

_(operator)_ —
