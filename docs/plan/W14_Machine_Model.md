# W14 — The machine model: memory, instruction set, and the operator that cycles it

**Binds:** `docs/specs/15_Godel_Engine_OODA_Loop.txt` (the operator) ·
`docs/specs/37_Substrate_Operation_ISA.md` (the instruction set) ·
`docs/specs/33_Perfcache_Blob_Law.md` (the resident floor) ·
`docs/specs/36_Laplace_Forward_Pass.md` (the stage order)

**Status:** the mapping is already the repo's own framing, stated across three
specs that do not currently reference each other as one machine. What is new
here is (a) naming the layers as one architecture and (b) four **structural
gaps** that this session's failures identify — each of which is a missing piece
of the machine, not a bug in a function.

---

## 1. The layers

| layer | what it is | where it is specified |
|---|---|---|
| **Storage** | the substrate: content-addressed merkle DAG — `entities`, `physicalities`, `attestations`, `consensus`. Identity is the address. | `docs/ARCHITECTURE.md` §1–2 |
| **Resident floor** | tier 0, compiled to an mmap'd blob (`laplace_t0_perfcache.bin`) | spec 33 |
| **Instruction set** | ten opcodes — RESOLVE, SENSE, ELECT, WEIGHT, SCAN, SELECT, TRAVERSE, SEQUENCE, REALIZE, WITNESS — and one legal order, S0–S10 | spec 37 §1–2 |
| **Microcode** | the native C: `prompt_coherence.c`, `generate_walk.c`, `fold_route.c`, `steer_candidates.c`, … | `extension/laplace_substrate/src/` |
| **Operator** | the Gödel engine: the OODA cycle that fetches, orients, decides, acts, and folds the result back | spec 15 §0 |
| **Programs** | read shapes (`define`, `what_is`, `walk`, …) expressed as opcode sequences | spec 37 §3 |

Spec 15 already states the operator's defining property in its own words:

> *The Gödel property: the engine's own outputs are first-class inputs. A
> response is content-addressed and deposited as a witness (self-reference);
> feedback on a response is an attestation folding into the SAME consensus the
> next walk reads (self-improvement). Evaluation IS ingestion.*

That is the writeback stage closing onto the storage the next fetch reads. It is
what makes this an operator over a mutable store rather than a query planner.

## 2. The tier ladder is a memory hierarchy, and that is why Unicode is ingested

The tiers are not a taxonomy. They are an **addressing hierarchy with a
guaranteed floor** — and only the floor is universal:

```
tier 0   CODEPOINT — universal across every modality. CANNOT MISS.
tier 1+  MODALITY-SPECIFIC, and currently far shallower than the law allows.
```

**Only tier 0 is fixed.** `EntityTier` (`app/Laplace.Substrate/Crud/EntityTier.cs`)
names the TEXT reading; `ChessCompose` (`app/Laplace.Chess/Service/ChessCompose.cs:20-23`)
names its own for the same numbers:

| tier | text | chess |
|---|---|---|
| **0** | Codepoint | codepoint — **shared floor** |
| 1 | Grapheme | `SubstructureTier` |
| 2 | Word | `PositionTier` |
| 3 | Sentence/paragraph | *(unused)* |
| 4 | Document | `LineTier` |

A chess position and an English word both sit at tier 2 — not because a position
is word-like, but because **tier 2 is "the composed unit this lane reasons
over."** The number is an address; the meaning is per-lane.

### The current ladder is a default, not the law

`entities.sql.in:10` declares `tier smallint NOT NULL CHECK (tier >= 0 AND tier
< 256)`. **The schema permits 256 tiers.** The partition loop materializes 0–4
and everything else falls to `entities_tdefault`, which still stores and reads
correctly — it just forgoes a dedicated partition.

So text's `grapheme / word / sentence / document` is the *lazy* ladder. The
structure a document actually has is far more granular — clause, phrase,
sentence, paragraph, section, page, chapter, title-separated part, volume,
corpus — and all of it fits in a byte with room to spare. Nothing in the
identity law, the hash composer, or the fold cares how many rungs there are;
only the partition seeding and the per-lane C# constants do, and the partition
layout is explicitly greenfield (`db-reset` + reseed is its upgrade path).

**Consequence for every tier-aware operation.** A descent policy cannot hardcode
`word → grapheme → codepoint`, for two independent reasons: the ladder is
per-lane, and its depth is not fixed. It must walk **the lane's ladder, however
deep it is**, with only the floor known in advance. Writing a read that assumes
tier 2 means "word" is the same class of error as assuming a language — which
this session made three times.

Tier 0 cannot miss, because every input decomposes to codepoints under UAX-29
and **every codepoint is ingested from the UCD**. That is precisely why the
floor sources are seeded: Unicode gives every character its script, block,
general category and bidi class; ISO 639 gives the language registry; CILI gives
the cross-lingual concept hub. They are not corpora — they are the **firmware
layer that guarantees the machine can always decode its input.**

Tier 0's blob (`laplace_t0_perfcache.bin`) is the resident layer, mmap'd and
always hot — the L1 of this machine, and the reason spec 33 exists as a law
rather than an optimization note.

**A resolution that stops at tier 2 throws the floor away.** Measured this
session: the token `śpi` does not resolve at word tier, so the language tally
discarded it entirely — while `ś` at tier 0 carries
`HAS_XPOS = aglt:sg:sec:imperf:nwok`, a **Polish-specific** UD morphological
tag. The evidence was one tier below where the read looked.

## 3. Four structural gaps this session identified

Each is a missing property of the machine, not a defective function.

### G-A. No fallback addressing mode (tier descent)

OP1 `RESOLVE` returns ids or abstains (spec 37 §1: *"Abstains (`NULL` id) rather
than minting"*). Abstention is correct — minting would be a lie — but there is
**no opcode and no addressing mode for "miss at tier N, descend to tier N−1,
aggregate with tier-weighted confidence."**

Consequence, measured: `prompt_language` reads `prompt_state()`, which is
word-tier, so a highly inflected language loses its distinctive tokens to
non-resolution and is judged on the residue — the short words that are
homographs in every language. `Kot śpi na stole w domu` resolved only `kot`
(→Slovenian), `na` (→Irish), `stole` (→an English verb), and answered **English**
for a Polish sentence. Polish is in the substrate with **87,985 tagged senses**;
the failure was addressing, not coverage.

**What the ISA needs:** RESOLVE gains a descent policy — `EXACT` (today),
`DESCEND` (fall to the floor, tag each hit with the tier it came from), and a
weighting rule so a word-tier hit dominates a codepoint-tier hit **without a
codepoint hit being worth nothing.**

### G-B. No datapath between stages

The canonical order (spec 37 §2) is S1 RESOLVE → S2 SENSE → S3 ELECT → … → S8
REALIZE. Language is a property of the **request**, so it is resolved at S1.

It is then applied at **S8**, for rendering only. It never reaches S2 or S3.
`prompt_coherence`'s signature is `(p_prompt text)` — there is no language
parameter to pass it to. So the machine computes the right answer at fetch time
and the decode stage cannot see it.

Measured: `prompt_language('What is a glacier?')` ranks **English 8,553** over
Irish 5,149. Correct. The election picks the Irish sense anyway.

**What the ISA needs:** stage outputs must be addressable by later stages — a
pipeline register, not a return value that only the last stage reads. Every
S1/S2 product (language tally, tier of resolution, abstention reasons) is
context for S3.

### G-C. No status register

Spec 37 L5 requires every stage to report `ran | degraded | skipped` with a
reason. `chat()` returns bare `text` (`chat.sql.in`, `RETURNS text`), so **G9 is
listed as not buildable** in the gate table.

A CPU without flags cannot branch on the last operation's outcome. That is
exactly why every failure this session was silent: an ingest that declared
19,308,489 units and stored 0; a language tally computed and discarded; four
seed runs cancelled 15 seconds in. Each was recoverable information that no
stage was obliged to report.

**What the ISA needs:** the response envelope of L5, which also unblocks the
eval harness (W5) — a scorer cannot distinguish "elected correctly then rendered
badly" from "elected wrongly" without per-stage status.

### G-D. Operands are immediates, not registers

The election key ladder, `walk_branches`' `+3.0` topic bias, the never-passed
`kappa`, the `50/40` provenance weights in `converse_walk` — these are constants
compiled into the instruction stream. The engine cannot set them, so it cannot
adapt them, so the fold cannot rate them.

This is the deepest gap and the one that connects to the owner's own proposal
(`COMPLETION_PLAN.md` §2, retrieval-as-player): **values learn; the machinery
that retrieves them does not.** Making operator configuration a rated player in
the same arena is the design-consistent fix, and it is unbuilt.

## 4. What "the operator cycles the ISA" requires that is not yet true

Spec 15's OODA maps cleanly onto fetch–decode–execute–writeback:

| OODA | machine stage | ISA |
|---|---|---|
| OBSERVE | fetch | S0–S1 RESOLVE |
| ORIENT | decode | S2 SENSE, S3 ELECT |
| DECIDE | issue | S4 PLAN (shape → program) |
| ACT | execute | S5–S7 SCAN / TRAVERSE / SEQUENCE, S8 REALIZE |
| FEEDBACK | writeback | S10 WITNESS → fold |

The cycle is real and closed — turns are witnessed, feedback folds, the next
walk differs. What is not true today:

1. **The cycle runs once per turn, not per token.** Multi-step emission with
   WITNESS between steps is specified (W8) and unbuilt, so the machine is
   single-instruction-per-invocation rather than a loop.
2. **DECIDE is a `strcmp` ladder in five places**, not a program counter over a
   shape table (spec 37 §3, G5).
3. **Fetch cannot fall back** (G-A), **decode cannot see fetch's output**
   (G-B), and **no stage sets flags** (G-C).

## 5. Ordering

G-C first — the status register is the cheapest and unblocks measurement of
everything else, including W5's harness. Then G-B (the datapath: language into
the election is one concrete instance, and it fixes a live regression). Then
G-A (descent, which needs G-B's plumbing to carry the tier tag). G-D last, and
only behind W5, because "retrieval learns" cannot be evaluated without a scorer.

## 6. What this document is not

It is not a new architecture. Every layer named here already exists and is
already specified; specs 15, 33, 36 and 37 describe one machine without saying
so, and none of them names the four gaps above as *machine* properties rather
than function defects. The value of the framing is that it predicts where to
look: a failure that reads as "the language detector is wrong" is, in this
frame, obviously a missing addressing mode plus a missing datapath — and that is
what the measurements showed.
