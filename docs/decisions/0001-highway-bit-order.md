# 0001 — Highway bit assignment: alphabetical codegen vs append-only registry

Status: **ACCEPTED 2026-07-20** — explicit `bit = N`, append-only. Written 2026-07-18
from the forest audit; decided by the operator 2026-07-20 (see §Decision). Implementation
is GH #551; the layout freezes at the next operator-ordered full reseed.

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

**Operator, 2026-07-20: adopt the explicit `bit = N` append-only registry.**

Verbatim reasoning: *"standard practice would say to come up with as complete a list as
possible and anything in the future would be appended as to prevent data loss... these
will be in millions/billions of records."*

That adds one binding requirement the recommendation above did not state: the freeze is
not merely "stop moving bits from now on" — it is **enumerate as completely as possible
FIRST, then append forever**. The one-time cost of thinking hard about the full relation
inventory is paid once; every bit assigned carelessly now is a bit that can never be
reordered later, because at billions of rows a re-mask is not a maintenance operation.

Binding consequences:

1. `bit = N` becomes a required field on every `[[relation]]` in
   `engine/manifest/relation_types.toml`. Codegen VALIDATES (unique, in range, no
   reuse of a retired bit) instead of assigning.
2. Before the freeze, do a deliberate completeness pass over the relation inventory —
   the known-coming families (the M0 modality ladders in
   `docs/invention/modality-ladder-law.md`, the git-lane ledger in GH #504, any
   governance/credit bands) get their bits reserved in that pass rather than appended
   piecemeal afterwards. Reserved-but-unused bits are cheap; renumbering is not.
3. Retired relations leave a **tombstone**, never a reused bit. A reused bit would make
   an old stored mask decode as a different relation — silent corruption, exactly the
   failure mode this decision exists to end.
4. The layout freezes at the next operator-ordered full reseed. That reseed is the last
   bit-order reseed the project pays.
5. Capacity is the thing to watch, not order: 256 bits against 203 live canonicals.
   Family collapse (GH #399) remains the capacity lever, and the completeness pass in
   (2) must not blow the budget — count before reserving.
