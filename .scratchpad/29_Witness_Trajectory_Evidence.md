<!-- DRAINED 2026-07-20 — fully reproduced by GitHub issue #451
     (witness-trajectory evidence virtualization: O(witnesses) rows -> O(facts) rows
     + testimony vertices). The law amendment this design implies is #535.
     Open work lives in GitHub issues. Kept as the design record. -->

# 29 — Witness-Trajectory Evidence (design, operator-prompted 2026-07-15)

Question: "record witnessing so it deduplicates but keeps the truths — store
what sources attest to it AS TRAJECTORIES. Do we really need these billion
records?"

Answer: we need the BITS (provenance is never mashed), not the ROWS (row grain
is an indexing choice, not an epistemological one). The general law:

## The symmetry that was already there

- CONTENT solved this on day one: entities dedup by hash; occurrence/order
  lives in trajectories (a document IS the trajectory of its word ids —
  ContentRoundtrip). One row per content, sequence in the payload.
- EVIDENCE never got the same treatment: one ROW per (fact, source, context,
  outcome). The billion-row pressure lives here — and it repeats the fact's
  triple per witness, plus per-row index cost.

The fix is the same move: facts dedup at the consensus key; WITNESSING lives
in a trajectory on the fact — one testimony vertex per source:
(source_id 128b, zigzag sum_score_fp 36b, games 16b, ordinal 16b) = the
EXISTING testimony vertex class, byte for byte. No new native, no new format.
laplace_testimony_pack_walk already writes it; the fold already touches the
cell at the right moment to maintain it.

## What each evidence guarantee becomes

1. Provenance — intact: sources + outcomes + counts are IN the vertices.
2. Scoped pours / refold — unpack, filter vertices by source set, refold.
   Cleaner than row scans; perfcache-able.
3. Audit ("decompose this cell to witnesses") — one row fetch + unpack.
4. Divergence signal — per-source vertices side by side on one fact.

Per-OCCURRENCE provenance (which game, which sentence) does NOT need vertices
either: the context's own trajectory already holds it from the other side
(the game IS its ply list; the document IS its word list). Occurrence evidence
rows are DERIVABLE — virtual, same law as pair evidence (doc 26). Tonight's
model lane proved the whole pattern end to end: 27,852 per-token testimonies
ride ONE trajectory row per circuit, token identity in-band, bit-exact.

## What it wins

O(witnesses) rows -> O(facts) rows + O(witnesses) 32-byte vertices. Density
~10x before index savings (indexes are per-row; vertices ride one indexed
row). The win concentrates exactly where billions accumulate: high-multiplicity
facts (common words, common moves, cross-model factor agreements).

## Honest costs + mitigations

- Write amplification on hot facts (read-modify-write of a growing geometry):
  maintain the witness trajectory AT THE FOLD (ConsensusAccumulatingWriter
  already accumulates client-side and touches the cell in the same tx);
  epoch-shard trajectories so appends are new rows, not rewrites.
- "All facts attested by source X" loses row-grain indexing: keep a small
  TRUST-CLASS bitmask column for the common filter (classes are few); exact
  source filtering = native vertex scan, or the source's own deposit journal.
- 16-bit games / 36-bit score per vertex: RLE across multiple vertices per
  source (ordinal disambiguates) — same convention trajectory_build_rle uses.

## Status

DESIGN ONLY — substrate-wide schema law, B-series class, belongs in the owed
reseed generation. Prerequisite sign-off: operator. Companion: doc 08
amendment (derivable-evidence virtualization, doc 26) — together they state:
  record events once, at the grain the source asserts them;
  witnessing is a trajectory on the fact;
  occurrences are trajectories on the context;
  evaluations are derivable and never materialize;
  consensus is the only thing that grows with truth instead of with volume.
