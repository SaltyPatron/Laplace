# 00 — CONTINUITY / READ THIS FIRST

Rewritten 2026-07-18. The previous version of this file embedded counts, rank ladders,
dead project paths, and build-state narrative — all of which rotted and misled the
agents it was written to orient. This version holds only two things: **where truth
lives**, and **the traps that wasted real sessions** (each re-verified on the rewrite
date). If a factual claim doesn't carry a date or a generated source here, it doesn't
belong here.

## The one rule

**Trust but verify. Read the actual code/DB before asserting anything. Never
pattern-match; never state unverified output as fact.** Laplace inverts mainstream ML
at nearly every level, so training-data instincts are usually wrong here. Deriving a
design from verified mechanism is good; asserting a high-probability guess as fact is
the failure that destroys trust.

## Where truth lives (pointers, not copies)

| What you need | Where it is |
|---|---|
| What Laplace is, epistemic status | [README.md](../../README.md) |
| The operating law: architecture, binding rules, build/seed/deploy tables | [CLAUDE.md](../../CLAUDE.md) |
| Agent conduct + Postgres service law | [AGENTS.md](../../AGENTS.md) |
| Every countable fact (projects, decomposers, relations, SQL families) | [docs/INVENTORY.md](../INVENTORY.md) — GENERATED, CI-gated; never trust a count in prose over it |
| Doc map with spec-vs-log classification | [docs/INDEX.md](../INDEX.md) |
| Binding specs (invariants, rules, record-vs-calculate, foundry, loop) | `docs/specs/` |
| The invention catalog (41 mechanisms, code-cited) | [docs/INVENTIONS.md](../INVENTIONS.md) |
| Open work | GitHub issues (from 2026-07-18; `.scratchpad/02` is the closed historical tracker) |
| Session logs / campaigns (historical, append-only) | `.scratchpad/` |
| Foundry synthesis mechanics | [05-synthesis-layers-heads.md](05-synthesis-layers-heads.md), [recipe-schema.md](recipe-schema.md), [modality-ladder-law.md](modality-ladder-law.md), `docs/specs/12`, `docs/specs/14` |

Rank weights, band names, relation counts: `engine/manifest/relation_types.toml` is
the single authority. Every prose copy of its numbers has drifted at least once.

## Traps — do NOT repeat these (each burned real time; re-verified 2026-07-18)

1. "law" in a filename (`pos_law`/`relation_law`) is NOT a "law layer." They're
   resolvers. Cute names left by prior agents are tripwires, not documentation.
2. The radius/rank-band 4-bucket plane mash (`consensus_layer_plane`) is the sabotage,
   not "the layers/heads." Real heads = per-attestation-type operators
   (`consensus_type_plane`), one DISTINCT operator per head. Top-k tiling one operator
   across heads = the dup/repeat bug.
3. A huge valid result ("everything English", millions of rows) is a valid equivalence
   class, not a bug to suppress.
4. Dev-box query latency is NOT an architectural limit — missing indexes / broken WIP
   queries / unbound KNN parameters. EXPLAIN before blaming the architecture
   (docs/specs/06 Rule #4; lesson L2).
5. Recipes are content-addressed substrate entities read via `model_recipes()` /
   `--recipe-from`. Hand-written JSONs are dev fixtures simulating the deposit; export
   never treats a disk file as the architecture.
6. Heavy math belongs native (`engine/` C/C++/MKL + SPI). C# and SQL orchestrate.
7. No architecture hardcoding. Everything is generic/data-driven — "Llama" is one
   deposited recipe, not code.
8. Ids are NEVER constructed outside the system: `canonical_id()`, `word_id()`,
   `relation_type_id()`, `consensus_id()` resolve through the native hash. (An agent
   once minted ids with a python blake3 — rejected; the law is in CLAUDE.md.)
9. `SELECT * FROM api('<substring>')` is the schema's self-introspection — check it
   before assuming a helper doesn't exist, and before writing a new one.
10. One ingest at a time; never kill a `Laplace.Cli`/psql/backend you didn't start —
    unexplained COPY = active ingest, stand down (CLAUDE.md build laws).

## How to work with the operator

Direct, competent, precise. No therapy/emotional/safety-script responses, ever — code
and facts. Never blame the operator. Honest about verified vs assumed. Binding detail:
`.cursor/rules/no-unsolicited-crisis-boilerplate.mdc`, `AGENTS.md`, CLAUDE.md's hard
ban.

## Maintaining this file

Pointers and traps only. No counts (INVENTORY owns them), no build-state narrative
(git/PRs own it), no plan references outside the repo. A trap stays only if
re-verified on the date stamped above; adding one requires naming the session cost it
caused.
