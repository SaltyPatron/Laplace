# ADR 0007: Sparsity is emergent from matchup consensus, NEVER weight-magnitude pruning

## Status

**Superseded** on the core mechanism by [RULES.md R3](../../RULES.md) + [ADR 0056](0056-weight-tensor-etl-as-arena-matchup-observation.md), reconciled with [docs/SUBSTRATE-FOUNDATION.md](../SUBSTRATE-FOUNDATION.md) — 2026-05-28. Originally **Accepted** 2026-05-21 under a "lottery-ticket magnitude pruning" framing that is now explicitly **forbidden**. The surviving, correct intent ("never a flat threshold; small weights can be load-bearing") is restated below in matchup-consensus terms; the magnitude-filter mechanism it originally prescribed is rejected.

## Context

Model ingestion is a streaming O(params) ETL of the weight tables — each weight cell is an *already-computed* relationship, emitted as a single **Glicko-2 matchup outcome** (weight = match result; the source model's own trust = opponent strength), per [docs/SUBSTRATE-FOUNDATION.md](../SUBSTRATE-FOUNDATION.md) truths 1–2. It is **never** a recompute: no GEMM at ingest, no forward pass, no activation/probe collection. Running the model forward (the `E·W·Wᵀ·Eᵀ` bilinear over vocab², or any flat top-k that discards most of the model) is the disease the anchor forbids, not a tuning knob — it took an hour on a 2 GB model and produced 646/32000 tokens.

The substrate keeps only the **emergent cross-source consensus**, never the weight, never bit-perfect (bit-perfect preservation is worthless per truth 6).

The original concern remains valid and is preserved: a flat numeric threshold (`|w| < ε`) is wrong because tensors live in different magnitude regimes, small weights are sometimes load-bearing, and large weights are sometimes noise. The original ADR's mistake was to "fix" that with a *relative* magnitude filter (per-tensor top-k%, per-row top-k). That is still magnitude pruning — the conventional neural-network "lottery-ticket" reflex (Frankle/Carbin) smuggled in — and it is now forbidden, because a weight is a match *outcome*, not a value to keep or discard.

## Decision

Sparsity is **emergent from matchup consensus, not a magnitude filter applied to weights** (per [RULES.md R3](../../RULES.md)):

1. **Absent token-pair relationships are never observed** — no relationship, no matchup, exact zero by construction ("zero is not an observation"). This is the *origin* of sparsity, not a cutoff at the door.
2. **Every real token-pair interaction is a matchup observation** fed to the Glicko-2 update — *small interactions included*. A weak outcome is real evidence, not noise to be thresholded away.
3. **Load-bearing vs. noise is decided by emergent consensus** — effective-μ, RD, source-trust, structural support, cross-source clustering (truths cluster → low RD; unsupported/outlier matchups scatter → high RD, discounted) — at the consensus/synthesis layer, **never by a per-model magnitude top-k at ingest**.
4. **Which token-pair matchups become attestations** is a **token-relational** selection — never raw-weight magnitude — validated by a **static-mathematical** retention test (the sparse aggregated-attestation subgraph preserves the dense subgraph's matchup-distribution / spectral structure). It is **NOT** probe-validated: the substrate never executes models at ingest.

### FORBIDDEN (rejected by this ADR's reconciliation, [RULES.md R3](../../RULES.md), and [ADR 0056](0056-weight-tensor-etl-as-arena-matchup-observation.md)'s alternatives-considered)

- Per-tensor **relative top-k% by weight magnitude** ("top 5% by importance") — the original ADR's Decision step 1. Magnitude is not importance; load-bearing interactions are sometimes small.
- **Per-row top-k by weight magnitude** — the original ADR's Decision step 2.
- Any flat **or relative** magnitude threshold on raw weights — significance must *emerge from consensus*, not be decided by a cutoff.
- **Probe-validated retention** ("synthesize candidate sparse subgraph; verify behavior preserved on a probe set") — the original ADR's Decision step 3. This is behavior/round-trip-preservation framing (banned per truth 6) and requires running the model (banned per truth 1). It is replaced by the static-mathematical retention test above.
- Treating a weight as a stored value at all.

## OPEN per docs/SUBSTRATE-FOUNDATION.md

The mechanism by which **interior `d×d` tensor cells (`q/k/v/o/gate/up/down`) resolve to token-pair matchups** without re-running the GEMM is **unsolved and must be pinned with Anthony.** `embed_tokens`/`lm_head` are directly token-anchored (cheap, real); the interior tensors are not. This ADR does **not** assert an interior resolution, an arena/kind assignment per interior tensor role, or the synthesis "pour facts into the mold" algorithm at frontier scale — all of which are OPEN per the anchor. Any future revision must mark these OPEN, not substitute a confident guess.

## Consequences

- Substrate captures the load-bearing structure as **emergent consensus over Glicko-2 matchup observations** — never stored weights, never bit-perfect (truths 1, 2, 6).
- No magnitude cutoff (flat or relative) lives anywhere in the ingestion code path; no flat global top-k that discards most of the model.
- Per-architecture variation lives in arena/kind semantics and the static-mathematical retention test — never in swapping the consensus mechanism for a magnitude prune.
- **Linguistic resources are exempt from any filtering** — every WordNet/OMW/UD/Wiktionary/Tatoeba/ConceptNet/Atomic2020 entry is curated and deliberate; every attestation goes in at full fidelity. These are OPTIONAL enrichment (independent ground truth for Glicko-2 to adjudicate against); semantic ingest of the model alone is the mandatory spine (truth 6).

## References

- [RULES.md R3](../../RULES.md) — Emergent sparsity from matchup consensus, NEVER weight-magnitude pruning
- [ADR 0056](0056-weight-tensor-etl-as-arena-matchup-observation.md) — WeightTensorETL as arena matchup observation
- [docs/SUBSTRATE-FOUNDATION.md](../SUBSTRATE-FOUNDATION.md) — ratified core (truths 1, 2, 6; OPEN: interior d×d resolution)
- Memory: project_laplace_invention.md — "Lottery ticket + sparse recording" section (historical framing)
