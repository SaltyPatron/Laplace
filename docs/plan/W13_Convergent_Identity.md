# W13 — Convergent identity: why everything links to everything

**Related:** #574 (EXPLAINS under-extraction), #491, #765 · **Status:** thesis
verified in code, unmeasured at scale · **Audience:** whoever picks this up next

This document exists because the property it describes is the invention's
central claim, it is **implemented and verifiable**, and it is nowhere written
down as a design position. An implementer who does not understand it will build
joins that the substrate does not need.

---

## 1. The claim

> Same content, same hash. So a game ingested from a book and the same game
> ingested from a PGN archive are **one entity** — reached by two provenance
> paths, each trunking up to its own source. Nothing links them; they were never
> separate.

## 2. It is true. Verified 2026-08-02

**One position minter.** `ChessCompose.PositionId(string surface)`
(`ChessCompose.cs:107`) is the only function that mints a position id, and every
lane calls it: `ChessOpeningsDecomposer.cs:104`, `ChessReplay.cs:104`,
`ChessMoveCommentary.cs:59`, `ChessPositionRef.cs:45`, `SubstrateRootBias.cs:33`,
and the book lane through `ChessGraph.EmitPosition(b, m.StateKey(state), src)`
(`ChessBookDecomposer.cs:188`).

**A game is the merkle of its positions.** `ChessCompose.LineId(ReadOnlySpan<Hash128>)`
= `Hash128.Merkle(LineTier, orderedPositionIds)` (`ChessCompose.cs:33-34`). The
line id is a pure function of the position sequence — no source, no timestamp,
no venue in the hash.

**Therefore the same game from any witness mints the same id**, and the tree
already says so. `ChessVocabulary.cs:121`, on GH #736:

> *a book's grounded prose line IS the shared line entity (`ChessCompose.LineId`…)*

**The apparent counter-example is not one.** #491 ("two position identities in
one modality") refers to `Zobrist` (`Modality/Zobrist.cs`, `Modality/Search.cs`)
— the **search** lane's internal transposition hashing, not substrate identity.
No ingest path mints a position through Zobrist. The identity law is intact; #491
is a tidiness issue in the engine, not a crack in this thesis.

## 3. What follows structurally

Because provenance rides `source_id` and `context_id` on the *attestation*
rather than in the id:

```
                 ┌── book trunk ──> chapter ──> prose line ──┐
Morphy's game ───┤                                            ├──> ONE line id
                 └── archive trunk ──> PGN file ──> game ─────┘
```

- **The EXPLAINS bridge is a walk, not a join.** From the line id, one indexed
  read reaches every witness of it — the book that annotates it and the archive
  that records it — because both attested the same subject. #574's
  under-extraction is therefore a *parser* problem (how many games the book lane
  can recognise), not a linking problem. Every game it does recognise links for
  free. **That distinction should be in #574**, because "improve the linking" and
  "recognise more games" are different work and only the second exists.
- **Provenance is preserved without duplication.** One entity, many attestations,
  each carrying its own source and trust class. The book's claim and the
  archive's record can even *disagree* — and disagreement is expressible
  (`Outcome.Refute`) rather than being a data-quality error.
- **Corpus scale compounds instead of accumulating.** Ingesting Lumbras after the
  chess.com archives does not add a parallel copy of shared games; it adds
  *witnesses* to lines that already exist, which is exactly what the fold wants —
  more evidence per cell, not more cells.

## 4. The generalisation: convergence at a shared node

The chess case is one instance of the substrate's actual mechanism: **any two
domains that share content share a node, and a walk can cross there.**

The owner's illustration is the *Spaceballs* joke — "comb the desert," delivered
by men dragging a giant comb, ending in "we ain't found shit." The humour is a
collision of two unrelated senses of one surface: *comb* as exhaustive search and
*comb* as the implement. That is not a metaphor for what the substrate does; it
is **literally** what it does:

- `word_id('comb')` is **one** content id.
- `senses()` returns both senses hanging off it.
- A walk entering from the *search* domain and leaving through the *implement*
  domain **passes through that single node**.

So cross-domain association is not a capability that has to be trained in — it is
a consequence of content addressing. A transformer produces the same joke from
distributed representations that cannot be pointed at; here the pivot is an
addressable id, and the path that produced the association can be **printed,
cited, and refuted**.

The same mechanism is why a chess manual is just a book (W2, and the
`seed-documents` design): it enters the document lane as prose, and the games
inside it converge onto line ids the archive lane also witnesses. One corpus, two
kinds of structure, no special-casing.

## 5. The honest boundary — read this before repeating the claim

The owner's stronger position is that this offers *more* than gradient descent
or reinforcement learning, and that their outputs are queryable from the
substrate. Split it into the part that is demonstrated and the part that is not.

**Demonstrated, and genuinely not available from a trained network:**

- Every belief carries its witnesses; a claim can be traced to who said it.
- Refutation is signed evidence, not absence — an unattested id is distinct from
  an id attested false.
- A source can be evicted (`evict_source`) and the beliefs it supported recede.
- Learning is per-edge and online: no retraining, no catastrophic forgetting.
- The fold is bit-reproducible fixed point — the same evidence yields the same
  ratings on any machine.
- **A checkpoint really is ingestible.** The model lane decomposes transformer
  tensors into rated, provenanced edges (`engine/synthesis/`, `engine/dynamics/`,
  `FoundryCommands.cs`). "Their output is queryable from my substrate" is
  implemented, not aspirational.

**Not demonstrated, and not to be asserted:**

- **Generalisation to unseen combinations.** Gradient descent's real product is
  interpolation over a learned manifold. The substrate's analogue is the walk
  plus the fold, and its reach is **unmeasured** — that is exactly what W5's
  harness exists to settle. Until it runs, "more than gradient descent" is a
  hypothesis with a good argument behind it, not a result.
- **Query formation does not learn.** Values learn; the machinery that *retrieves*
  them is hand-set (election keys, bias weights, a never-passed kappa). See W7/W8
  and the retrieval-as-player idea in `COMPLETION_PLAN.md` §2 — the design-consistent
  fix, also unbuilt.
- **Fluency.** Convergent identity gives structure, not prose. Composition is W1.

**How to hold both at once:** the substrate's advantages are *epistemic* —
inspectability, provenance, editability, refutation — and those are real today.
The claim that it also matches or beats learned models at *producing* language or
novel inference is an empirical question with no measurement yet. Saying the
first without the second is honest. Saying the second without the harness is the
failure mode this whole plan is written against.

## 6. What to do with this

1. **Never write a join that content addressing already performs.** If two things
   are the same content, they are the same id; look for the id.
2. **Amend #574** to separate recognition (a parser problem, real and open) from
   linking (already solved by identity). The issue currently conflates them.
3. **Test the thesis directly once seeded** — it is cheap:
   ```sql
   -- a line witnessed by BOTH a book source and an archive source
   SELECT a.subject_id, count(DISTINCT a.source_id) AS witnesses
   FROM laplace.attestations a
   WHERE a.type_id = laplace.relation_type_id('PLAYS_LINE')
   GROUP BY a.subject_id HAVING count(DISTINCT a.source_id) > 1
   LIMIT 5;
   ```
   A non-empty result is the thesis, demonstrated on real data. An empty result
   after both lanes have run means either the book parser recognised nothing that
   overlaps (#574) or the identity path diverged — and **which one it is must be
   determined, not assumed.**
4. **Add a convergence probe to W5's harness.** Cross-source agreement on a shared
   id is a measurable property and the most direct evidence for the architecture's
   central claim. It belongs in the scored suite, not in prose.
