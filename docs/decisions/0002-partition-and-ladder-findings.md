# Partition layout and the composition ladder — measured findings

Measured 2026-08-10 against the live `laplace` database while seeding was
running. Estimates are `pg_class.reltuples` unless stated; exact counts are
marked.

## 1. Partition skew: three tables, all at the top-level routing tier

| table | strategy | parts | biggest | share |
| --- | --- | ---: | --- | ---: |
| `laplace.entities` | `LIST (tier)` | 6 | `entities_t3` | **65%** (5,884,355 / 9,017,573) |
| `laplace.attestations` | `LIST (type_id)` | 27 | `attestations_rdefault` | **57%** (3,782,013 / 6,691,774) |
| `laplace.consensus` | `LIST (type_id)` | 27 | `consensus_rdefault` | **56%** (3,399,293 / 6,048,231) |

Everything sub-partitioned by hash is **even**: every `consensus_r_*` /
`attestations_r_*` table sits at exactly 13% (1/8) in its biggest shard —
`HASH (subject_id)`, 8 ways. `entities_t2` is `HASH (id)`, 8 ways, also even.

The three skewed tables are precisely the three that never got the second level:

- `entities_t3` — `relkind='r'`, **0 sub-partitions**, 5.9M rows flat, while its
  smaller sibling `entities_t2` is `relkind='p'` with 8 hash shards.
- `consensus_rdefault`, `attestations_rdefault` — flat, while all 26 of their
  named siblings are hash-sharded. A Postgres `DEFAULT` partition can itself be
  partitioned, so this is not a structural obstacle.

`entities_t0` is also flat at 1.11M. `t1` (1,105) and `t4` (79,609) are fine.

## 2. `LIST (tier)` is the wrong top-level key — not just an unfinished one

An earlier note in this session said the scheme was sound and only the second
level was missing. That was wrong. The key choice itself is the problem:

- **`tier` is a byte, 0–255** (`app/Laplace.Core/Core/IntentStage.cs:79`:
  `if (tier < 0 || tier > 255) throw`), hard-partitioned into five
  `FOR VALUES IN ('0')…('4')` buckets plus `tdefault`. Any tier ≥ 5 silently
  lands in an unpartitioned default (currently 0 rows) — a latent cliff.
- **Nothing in production prunes on it.** The only `tier =` predicates in the
  repo are diagnostics: `scripts/sql/debug-france-pipeline.sql`,
  `probe-capital-france-paris.sql`, `debug-order-closure.sql`. Zero `WHERE tier`
  in `app/`. The LIST buys no pruning on any hot path.
- **The skew is semantic, so it cannot be balanced** — only subdivided after the
  fact. Exact counts: t0 1,114,240 · t1 1,105 · t2 2,500,215 · t3 5,919,949 ·
  t4 79,609. That is 5,357:1 between t1 and t3.
- **Ids are already uniform.** `get_byte(id,0)%8` over a 0.5% sample of
  `entities_t3`: 2821 / 3054 / 3370 / 3527 / 3778 / 3789 / 3849 / 3875 — flat
  within ±15%. Content-addressed ids are hashes, so `HASH(id)` distributes by
  construction. That is why every `_r_*` table lands on 13% untuned.
- **`tier` is modality-relative**, so a partition mixes modalities.
  `app/Laplace.Chess/Service/ChessCompose.cs` declares
  `SubstructureTier=1, PositionTier=2, SegmentTier=3, LineTier=4`. Tier 3 is a
  *sentence* in text and a *segment* in chess. It is a composition-depth
  counter, not a category.

**Implication:** `entities` should be `HASH (id)` at the top level like the rest
of the schema, with `tier` demoted to a plain indexed column for the diagnostics
that actually want it. Not yet verified: whether anything depends on
`entity_curve` / Hilbert ordering *within* a partition, since a repartition
rewrites physical order and is the one change that could regress locality.

## 3. What is actually in the catch-all partitions

Top relation types in `consensus_rdefault` (exact counts, `GROUP BY type_id`
joined to `laplace.canonical_names`):

```
IS_TYPED_AS            359,802     HAS_NAME_ALIAS          211,740
HAS_LINE_BREAK         356,930     HAS_VALENCE_PATTERN     181,583
HAS_EAST_ASIAN_WIDTH   355,548     HAS_SCRIPT              159,866
HAS_LEX_CATEGORY       320,018     CORRESPONDS_TO           93,372
HAS_BLOCK              303,808     DERIVATIONALLY_RELATED   59,103
HAS_AGE                299,448     HAS_GENERAL_CATEGORY     40,575
HAS_EXAMPLE            276,429     HAS_BIDI_CLASS           40,575
```

Not garbage — the highest-volume types in the system, none of which got a named
LIST partition. **The selection is inverted:** ten types in `rdefault` each
exceed 150k rows, while types that *did* get dedicated 8-way partitions include
`move` (12,502) and `outcome` (10,684).

Two separate fixes: sub-partitioning `rdefault` fixes *distribution*; promoting
the top types to named LIST partitions is what buys them *pruning*. Today a read
filtered on `HAS_EXAMPLE` scans a 3.4M heap while one filtered on `move` prunes
to a ~1,563-row shard.

### Trap: `HAS_LINE_BREAK` does not record line breaks

Those 356,930 rows are the UAX #14 **`Line_Break` character property** (class
AL, BA, CM, …) attached to a *codepoint* at the floor. Likewise
`HAS_EAST_ASIAN_WIDTH`, `HAS_BLOCK`, `HAS_AGE`, `HAS_SCRIPT`,
`HAS_GENERAL_CATEGORY`, `HAS_BIDI_CLASS` — roughly 1.5M rows of Unicode
character metadata, not text structure. Nothing here records where a line,
paragraph or page ends in a document.

## 4. The composition ladder is truncated after sentence

`engine/core/include/laplace/core/` contains exactly four segmentation headers:

```
grapheme_break.h   grapheme_floor.h   word_break.h   sentence_break.h
```

That is UAX #29's complete set — grapheme, word, sentence — each with
conformance tests against the UCD auxiliary files (`GraphemeBreakTest.txt`,
`WordBreakTest.txt`, `SentenceBreakTest.txt`). There is **no** line, paragraph,
page, section or chapter segmentation anywhere.

So the text ladder is:

    t0 codepoint → t1 grapheme → t2 word → t3 sentence → t4 document

and it jumps sentence straight to document. For the 639-document book corpus,
chapter and paragraph structure is not represented — a document is a flat bag of
sentences. UAX #29 does not define paragraph/page; those come from UAX #14 and
from document-format structure, and neither is implemented.

### Cost of adding a level

Cardinality is not the blocker — tier is a byte using 5 of 256 values. The cost
is that ids are content-addressed merkle hashes over children
(`hash128_merkle(1, g_kids, 2, &grapheme)` in
`engine/core/tests/test_text_decomposer.cpp`). Inserting a paragraph level
between sentence and document makes a document's id a hash of paragraphs rather
than sentences, so **every downstream id changes**. Adding a tier is a reseed,
not a migration — which means the cheapest moment to deepen the ladder is during
a reseed already being paid for, not after a graph is built on the shallow one.

## 5. Modality coverage: decomposers exist, data does not

28 decomposers are implemented, including media (`RgbaImageDecomposer`,
`TrackAudioDecomposer`, `FrameVideoDecomposer`), code (`RepoDecomposer`,
`StackDecomposer`, `TinyCodesDecomposer`), tabular (`ParquetDecomposer`,
`TabularDecomposer`), plus `MapNetDecomposer` and `ModelDecomposer`.

Live modality counts: `text 3,053,728 · documents 639 · chess 0 · models 0 ·
multilingual 0`.

For media/code/tabular the gap is ingestion, not implementation. But none of
those modalities has a published ladder the way text does — there is no image or
audio analogue of grapheme → word → sentence in `engine/core/include`. That part
is not designed yet, not merely unseeded.
