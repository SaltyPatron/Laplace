# Ingest throughput — measured findings, 2026-08-10

First clean ten-rung foundation ladder (`laplace_iter6`, all `rc=0`, zero `57P01`/`57014`),
plus an isolated FrameNet re-run. Every number here comes from a run log on this host, not
from a profile constant, a doc comment, or an estimate. Where a claim is **not** measured it
says so.

This document also records claims made during the session that turned out to be **wrong**,
and what falsified them, because three of them were asserted confidently and acted on before
being checked.

---

## 0. Vocabulary, because the terms were used loosely

| term | what it means | what it is not |
|---|---|---|
| **RBAR** (row-by-agonizing-row) | one row per operation, sequential | not merely "a loop" — a loop over a *set-based* call is fine |
| **set-based** | one statement expresses the whole set; the engine plans it once | not "batched" — a batch of 1,000 single-row INSERTs is still RBAR |
| **batch** | N units grouped into one round trip / one unit of work | says nothing about parallelism |
| **bulk** | a dedicated high-throughput load path that bypasses the normal write path (`COPY`) | not the same as a large batch of INSERTs |
| **threaded / parallel** | concurrent execution across cores | orthogonal to all of the above; parallel RBAR is still RBAR |

The four are independent axes. A pipeline can be batched and still RBAR (many small
statements per batch), bulk and still serial (one `COPY` at a time), parallel and still
slow (concurrent DOM parsers).

`COPY` is the bulk path in PostgreSQL and reaches 100k+ rows/s on ordinary hardware; the
canonical high-throughput shape is **`COPY` into a lean staging table, then move to the
indexed target in controlled steps**, with indexes/constraints created after the load.
Parallel `COPY` across workers buys a further ~3–4×.

---

## 1. Where the ladder's 1,327 seconds actually go

| phase | time | note |
|---|---|---|
| **COPY** — 14,186,799 rows | **133.3 s** | **106,400 rows/s** — the bulk path is healthy |
| existence probes | 206.8 s | O(tiers) descent, bounded by design |
| prep / parse / dedupe | 13.5 s | |
| **consensus fold** | **2,227.1 lane-s** | dominant; lanes run concurrently so this exceeds wall |
| ladder wall | 1,327 s | |

**The write path is not the bottleneck.** `COPY` moves 14.2M rows in 133 s. Everything
expensive is upstream of it or in the fold.

---

## 2. Fold cost is concentrated in two rungs

| rung | fold lane-s | cells | masks | cells/s | masks/cell |
|---|---|---|---|---|---|
| **cili** | **1,449.1** | 1,348,597 | 1,319,960 | **931** | 0.98 |
| **wordnet** | **695.5** | 2,689,887 | 883,752 | **3,867** | 0.33 |
| unicode | 53.8 | 1,631,895 | 415,396 | **30,347** | 0.25 |
| framenet | 16.0 | 510,515 | 296,543 | 31,937 | 0.58 |
| propbank | 4.0 | 127,791 | 63,764 | 32,044 | 0.50 |
| semlink | 1.3 | 67,155 | 7,343 | 50,228 | 0.11 |

`cili` + `wordnet` = **2,145 of 2,227 lane-seconds (96%)**. Fold rate collapses **33×**
between unicode and cili on the same code path.

Rate tracks **masks deposited per cell**. `highway_mask_deposit` takes
`pg_advisory_xact_lock(hashtext('highway_mask_deposit'))` — one global lock, by design (its
header documents the 2026-07-29 deadlock fix that made it one-at-a-time). More mask work per
cell means more time under one lock and every other fold lane convoys behind it. The
function's own header records this being measured before: *"196 calls, 651,955 ms
(3,321 ms mean) … 182 s of that run was pure convoy wait."*

**Not proven:** that the lock is the whole cause. The correlation is strong across six rungs
but `pg_locks` was not sampled during a cili fold. That is the decisive measurement and it
has not been taken.

Tracked: **#964**.

---

## 3. Compose rate per rung — and the claim it falsified

`rate_input_s` across every `INGEST_PROGRESS` tick:

| rung | ticks | mean units/s | max units/s |
|---|---|---|---|
| unicode | 11 | 106,540 | **195,827** |
| cili | 26 | 17,490 | 75,688 |
| wordnet | 13 | 7,321 | 19,190 |
| **framenet** | **258** | **31** | **75** |

**unicode peaks at 195,827 units/s through a sequential `foreach`.** A serial producer is
therefore *not* a bottleneck when per-unit cost is low.

Because a unicode "unit" is one codepoint and a FrameNet unit is a lexical entry with
definitions and annotated sentences, per-unit is not a fair comparison. Normalised:

- **rows/s** — unicode 2.8M / 54 s = **52,000**; framenet 1.04M / 602 s = **1,730** → **30×**
- **bytes/s** — unicode 734 MB / 54 s = **13.6 MB/s**; framenet 814 MB / 602 s = **1.35 MB/s** → **10×**

The defensible gap is **10× per byte, 30× per row**.

---

## 4. FrameNet's per-unit cost

Corpus: 14,908 XML files, 814 MB, mean **57,255 bytes/file** (13,573 `lu*.xml` at 55,576).

Per lexical unit, `FrameNetLuIngest`:
1. `XDocument.Load(path)` — full DOM materialisation of a ~57 KB document
2. **three** separate `root.Descendants(...)` traversals (`valenceUnit`, `pattern`, `sentence`)
3. `ContentEmitter.Emit` **per string** — lemma, definition, each valence pattern, each
   sentence, each target

A streaming reader already exists in the same component — `ParseFulltextAsync` uses
`XmlReader` with `Async = true`. The LU path, 91% of the corpus, does not use it.

A representative apply batch:

```
prep:    92,211e -> 27,604 distinct       92 ms      (3.3x duplication within one batch)
verify:  9,978e + 9,978p + 48,499a      1,383 ms     (17,626e skipped, cached)
kept:    9,607e / 9,607p / 21,753a novel              (10% of input is new)
copy:    attestations 215,314 rows      1,136 ms
round-trips: 15 = 1 lock + 1 journal + 9 probe + 3 copy + 1 merge, 26,746 merged
```

unicode's mirror image: `0 merged`, **43 round-trips for the entire 1.17M-entity run**,
`0a novel-by-construction`.

### The re-derivation tax on a cold database

`ContentTierSpine.TryStageIntoBuilder` documents this exact pathology in its own comment:
*"measured on the 2026-08-06 full-file run as **77 records/s with 11 compose workers
serialized** behind an armed-but-empty ledger."* **FrameNet runs at 70–77 units/s.**

Trace, when `ContentLadderLedger.Armed` is true but `HasEntries` is false:

```csharp
if (RootMemo.TryGetValue(key, out var memo))
{
    if (ContentLadderLedger.HasEntries && memo is { } m && IsPersisted(m))
    { rootId = m; return true; }
    // ledger empty -> no return -> falls through
}
...
return builder.ContentStage.TryAddContentWitness(canonicalUtf8, sourceId, out rootId);
```

A memo **hit** with an empty ledger falls through and re-derives. The ledger's contract makes
this deliberate — *"Membership must have NO false positives — a wrongly-skipped ladder is a
dropped entity, not a slow one. Ids enter only from `presentEntities`: probed present in the
target, or written by an apply of this run that has COMMITTED."* — so the fall-through is
**correct for safety**.

The consequence is a cold-start tax: on a fresh database nothing is present, the ledger stays
empty, and every recurring surface pays full ladder derivation until commits begin populating
it. This predicts exactly the observed split — high-repetition sources (framenet, cili,
wordnet) slow, unique-surface sources (unicode) fast, because unicode never repeats a surface
and so never pays it.

`ContentLadderLedgerTests` covers miss → derive → memoize → skip-after-commit. It does **not**
cover memo-hit-while-ledger-empty, which is FrameNet's dominant path.

Tracked: **#952**, **#967**.

---

## 5. Claims made during this session that were WRONG

Recorded because each was asserted with confidence and, in two cases, acted on.

| claim | why it was wrong | what falsified it |
|---|---|---|
| "Delete the probe path; use `COPY` + `ON CONFLICT`" | Identity is **Merkle-derived** — a tier-N id is a hash of its tier-(N−1) constituents. The descent *computes* the ids; it is not asking permission to insert. `ON CONFLICT` has nothing to conflict on yet. | `docs/specs/06_Engineering_Ruleset.txt:116` — Rule #8 step 5, "ONE trunk→tier descent … O(tiers) round trips per batch" |
| "FrameNet's profile under-declares 14×; fix the constant" | A bandaid on a per-source literal feeding a hardcoded 1 MiB target. Correcting one arbitrary number inside another arbitrary number. | `IngestSizing.TargetBytesPerBatch = 1 << 20` |
| "Every producer is single-threaded; that is the wall" | A keyword census (`grep Parallel.`), not a measurement. | unicode composes at **195,827 units/s** through the same sequential `foreach` |
| "Parallelise the FrameNet producer" | Implemented as a hand-rolled `Task.Run` sliding window — a bounded-parallel ordered map that .NET already provides. | Measured **74–77/s** against a 70/s baseline: **no change** |

The through-line: reaching for a rewrite that sounds like engineering before measuring, and
treating repo prose (`.scratchpad/38`'s "verified on 200,000 live consensus rows" — an
unsubstantiated sentence; the real test is 7 hand-written `VALUES` rows) as evidence.

---

## 6. Sizing constants, and which one binds

`ResolveRecordBatch` derives from `MemoryTopology` and `CpuTopology` for every term except
one: `TargetBytesPerBatch = 1 << 20`, hardcoded, on a host with 134,963,920,896 bytes of RAM.

Derived on this machine (12 P-cores, 4 GiB working-set budget), verified to reproduce the
observed `ingest_source_sizing` line for framenet and wordnet exactly:

| source | est | fromTarget | fromMemory | raw | batch | binding term |
|---|---|---|---|---|---|---|
| unicode | 48 | 21,845 | 32,768 | 8,192 | 8,192 | coreCeiling |
| cili | 256 | **4,096** | 32,768 | 4,096 | 4,096 | **TARGET (1 MiB)** |
| wordnet | 4,096 | **256** | 2,978 | 256 | **1,024** | **TARGET**, floored up |
| framenet | 4,096 | **256** | 5,957 | 256 | **1,024** | **TARGET**, floored up |

The 1 MiB literal **binds for three of four sources**; the memory model's answer (5,957 for
FrameNet) is computed and discarded, then `coreFloor` clamps in **records** while every other
term reasons in **bytes**.

`MeasureBytesPerRecord` exists precisely because *"those constants are estimates that nothing
ever checked against a corpus, and they are wrong by enough to matter"* — and it is gated
behind `LineDelimitedExtensions` (`.jsonl/.ndjson/.csv/.tsv/.tab/.conllu/.txt`), so `.xml`
never reaches it and FrameNet keeps its declared 4,096 forever.

**Direction not established.** Whether FrameNet's batches are too large or too small in bytes
was argued both ways during the session and is *not* settled here.
`MemoryTopology.WorkingSetFlushEnvelopeBytes` is deliberately small (RAM/64, ≤512 MiB) with a
recorded measurement — *"30k → 1.8k rec/s as a ~4 GiB set filled with ~3M records"* — so
"enlarge the buffer" is the failure that file already documents.

---

## 7. Cross-cutting

- **#958** — a local seed and a CI deploy share one cluster with no interlock. `57P01` killed a
  ladder twice; `ingest_run_journal` already records open runs and nothing consults it before
  `restart_postgres`.
- **#960** — `input_pct` reaches 293.5% because composed records are counted against a file
  denominator, which hides genuine duplicate composition.
- **#959** — `canonical_names_seed`'s `ON CONFLICT (id) DO NOTHING` discards derived-id
  collisions, which are identity-law violations, during the campaign meant to surface them.

---

## 8. What is proven, and what is not

**Proven on this host:** the phase decomposition (§1); fold cost by rung (§2); compose rate by
rung (§3); FrameNet's corpus shape and per-unit code path (§4); the binding sizing term (§6);
and that parallelising FrameNet's producer alone changes nothing.

**Not proven:** that the advisory lock causes the fold collapse (no `pg_locks` sample); that a
run-scoped present-set would recover the probe time; the correct direction for the batch
envelope; and any claim that a given rung *regressed* — there is **no prior baseline** in the
repository for any foundation rung, so every figure here is a first baseline.

## Sources

- [Parallel Processing of Large Volume ETL Jobs — SQLServerCentral](https://www.sqlservercentral.com/articles/parallel-processing-of-large-volume-etl-jobs)
- [Optimizing ETL with Parallel Processing — Airbyte](https://airbyte.com/data-engineering-resources/etl-parallel-processing)
- [Testing Postgres Ingest: INSERT vs Batch INSERT vs COPY — Tiger Data](https://www.tigerdata.com/learn/testing-postgres-ingest-insert-vs-batch-insert-vs-copy)
- [Faster bulk loading in Postgres with COPY — Citus](https://www.citusdata.com/blog/2017/11/08/faster-bulk-loading-in-postgresql-with-copy/)
- [High-Speed Data Load for PostgreSQL — Fastware](https://www.postgresql.fastware.com/blog/high-speed-data-load-for-your-postgresql-database)
- [Optimizing bulk loads in Postgres — pganalyze](https://pganalyze.com/blog/5mins-postgres-optimizing-bulk-loads-copy-vs-insert)
